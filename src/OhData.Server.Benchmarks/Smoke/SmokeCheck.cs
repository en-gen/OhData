using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks.Smoke;

/// <summary>
/// Pre-benchmark correctness gate. Runs every benchmarked scenario once against BOTH hosts and
/// asserts the responses are semantically equivalent (status codes, item counts, entity ids,
/// selected property shapes, count values). A benchmark that compares a 200 against a 404 — or
/// a 100-item page against a 495-item dump — is garbage; this fails fast instead.
/// </summary>
internal static class SmokeCheck
{
    private sealed class SmokeFailureException : Exception
    {
        public SmokeFailureException(string message) : base(message) { }
    }

    public static async Task<bool> RunAsync(int seed)
    {
        Console.WriteLine("── Smoke check: verifying both hosts return semantically equivalent responses ──");

        var (ohApp, oh, ohNavConnection) = await BenchmarkHosts.StartOhDataAsync(seed);
        var (msApp, ms, msNavConnection) = await BenchmarkHosts.StartMsODataAsync(seed);
        await using var ohAppScope = ohApp;
        using var ohScope = oh;
        using var ohNavConnScope = ohNavConnection;
        await using var msAppScope = msApp;
        using var msScope = ms;
        using var msNavConnScope = msNavConnection;

        await PrintDepartmentFanOutAsync(ohApp, seed);

        int failures = 0;

        // Cross-host data identity is the property the entire comparison rests on: both hosts must
        // hold byte-identical seeded rows for a given seed, or every ratio the benchmarks report is
        // meaningless. Checked directly against each host's database (bypassing HTTP/query-option
        // semantics entirely) rather than inferred from any one endpoint's response shape.
        try
        {
            await AssertIdenticalSeedDataAsync(ohApp, msApp, seed);
            Console.WriteLine("  PASS  Cross-host seeded data identity (row hash)");
        }
        catch (SmokeFailureException ex)
        {
            failures++;
            Console.WriteLine($"  FAIL  Cross-host seeded data identity (row hash): {ex.Message}");
        }

        foreach (var (name, check) in Checks())
        {
            try
            {
                await check(oh, ms);
                Console.WriteLine($"  PASS  {name}");
            }
            catch (SmokeFailureException ex)
            {
                failures++;
                Console.WriteLine($"  FAIL  {name}: {ex.Message}");
            }
        }

        if (failures > 0)
        {
            Console.WriteLine($"Smoke check FAILED ({failures} scenario(s)). Benchmarks will not run.");
            return false;
        }

        Console.WriteLine("Smoke check passed: all scenarios semantically equivalent across hosts.");
        return true;
    }

    /// <summary>
    /// Hashes every seeded <see cref="BenchDepartment"/>/<see cref="BenchEmployee"/> row (id-ordered, all
    /// structural properties) from each host's own <see cref="BenchOrgDbContext"/> and asserts the two
    /// hashes match — the smoke-check-level proof that <see cref="BenchOrgData.Seed"/>'s "generate the
    /// blueprint once, clone per host" discipline actually produced byte-identical data across the two
    /// independently-seeded hosts for <paramref name="seed"/>, not just plausible-looking data.
    /// </summary>
    private static async Task AssertIdenticalSeedDataAsync(WebApplication ohApp, WebApplication msApp, int seed)
    {
        string hashOh = await ComputeSeedRowHashAsync(ohApp);
        string hashMs = await ComputeSeedRowHashAsync(msApp);
        Assert(hashOh == hashMs,
            $"seeded row hash mismatch for seed {seed}: OhData={hashOh} MS={hashMs} — the two hosts do not " +
            "hold byte-identical data, which invalidates every comparison this suite reports.");
    }

    private static async Task<string> ComputeSeedRowHashAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BenchOrgDbContext>();

        var departments = await db.BenchDepartments.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
        var employees = await db.BenchEmployees.AsNoTracking().OrderBy(e => e.Id).ToListAsync();

        var sb = new StringBuilder();
        foreach (var d in departments)
            sb.Append(d.Id).Append('|').Append(d.Name).Append(';');
        sb.Append("--EMPLOYEES--");
        foreach (var e in employees)
        {
            sb.Append(e.Id).Append('|').Append(e.Name).Append('|').Append(e.Salary)
              .Append('|').Append(e.DepartmentId).Append('|').Append(e.ManagerId).Append(';');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>Diagnostic only (not a correctness assertion): reports the seed's actual
    /// employees-per-department distribution so a reader of <c>--smoke</c> output can see the skew
    /// profile a given seed produces without hand-computing it.</summary>
    private static async Task PrintDepartmentFanOutAsync(WebApplication ohApp, int seed)
    {
        using var scope = ohApp.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BenchOrgDbContext>();
        var byDepartment = await db.BenchEmployees.AsNoTracking()
            .GroupBy(e => e.DepartmentId)
            .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
            .ToListAsync();
        int[] sizes = byDepartment.Select(x => x.Count).OrderBy(x => x).ToArray();
        int median = sizes.Length % 2 == 1
            ? sizes[sizes.Length / 2]
            : (sizes[sizes.Length / 2 - 1] + sizes[sizes.Length / 2]) / 2;
        var largest = byDepartment.OrderByDescending(x => x.Count).First();
        var smallest = byDepartment.OrderBy(x => x.Count).First();
        Console.WriteLine(
            $"── Department fan-out for seed {seed}: min={sizes[0]} max={sizes[^1]} median={median} " +
            $"total={sizes.Sum()} across {sizes.Length} departments " +
            $"(largest=dept {largest.DepartmentId} @ {largest.Count}, smallest=dept {smallest.DepartmentId} @ {smallest.Count}) ──");
    }

    private static IEnumerable<(string Name, Func<HttpClient, HttpClient, Task> Check)> Checks()
    {
        yield return ("GetAll page (100 items)", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.GetAllUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.GetAllUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: BenchmarkData.PageSize);
        }
        );

        yield return ("$filter", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.FilterUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.FilterUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: BenchmarkData.PageSize);
            long firstId = GetIds(a).First();
            Assert(firstId == 506, $"expected first filtered id 506, got {firstId}");
        }
        );

        yield return ("$orderby", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.OrderByUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.OrderByUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: BenchmarkData.PageSize);
            long firstId = GetIds(a).First();
            Assert(firstId == BenchmarkData.WidgetCount, $"expected name-desc first id {BenchmarkData.WidgetCount}, got {firstId}");
        }
        );

        yield return ("$select", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.SelectUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.SelectUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: BenchmarkData.PageSize);
            foreach (var (label, json) in new[] { ("OhData", a), ("MS OData", b) })
            {
                string[] props = ((JsonObject)json["value"]![0]!)
                    .Where(kv => !kv.Key.StartsWith('@'))
                    .Select(kv => kv.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
                Assert(props.SequenceEqual(new[] { "Id", "Name" }),
                    $"{label} $select shape was [{string.Join(",", props)}], expected [Id,Name]");
            }
        }
        );

        yield return ("$top + $skip", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.TopSkipUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.TopSkipUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: 50);
            long[] ids = GetIds(a).ToArray();
            Assert(ids[0] == 101 && ids[^1] == 150, $"expected ids 101..150, got {ids[0]}..{ids[^1]}");
        }
        );

        yield return ("$count=true", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.CountUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.CountUrl, HttpStatusCode.OK);
            AssertSameIds(a, b, expectedCount: BenchmarkData.PageSize);
            long countA = (long)a["@odata.count"]!;
            long countB = (long)b["@odata.count"]!;
            Assert(countA == countB, $"@odata.count mismatch: OhData={countA} MS={countB}");
            Assert(countA == 495, $"expected @odata.count 495, got {countA}");
        }
        );

        yield return ("GetById", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.GetByIdUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.GetByIdUrl, HttpStatusCode.OK);
            AssertSameEntity(a, b, expectedId: BenchmarkData.LookupId);
        }
        );

        yield return ("POST", async (oh, ms) =>
        {
            var a = await SendJsonAsync(oh, BenchmarkRequests.CreatePost(), HttpStatusCode.Created);
            var b = await SendJsonAsync(ms, BenchmarkRequests.CreatePost(), HttpStatusCode.Created);
            AssertSameEntity(a, b, expectedId: BenchmarkData.WidgetCount + 1);
        }
        );

        yield return ("PUT", async (oh, ms) =>
        {
            var a = await SendJsonAsync(oh, BenchmarkRequests.CreatePut(), HttpStatusCode.OK);
            var b = await SendJsonAsync(ms, BenchmarkRequests.CreatePut(), HttpStatusCode.OK);
            AssertSameEntity(a, b, expectedId: BenchmarkData.LookupId);
        }
        );

        yield return ("PATCH", async (oh, ms) =>
        {
            var a = await SendJsonAsync(oh, BenchmarkRequests.CreatePatch(), HttpStatusCode.OK);
            var b = await SendJsonAsync(ms, BenchmarkRequests.CreatePatch(), HttpStatusCode.OK);
            AssertSameEntity(a, b, expectedId: BenchmarkData.LookupId);
            Assert((string)a["Name"]! == "Patched-Smoke", $"OhData PATCH did not apply delta: name={a["Name"]}");
            Assert((string)b["Name"]! == "Patched-Smoke", $"MS OData PATCH did not apply delta: name={b["Name"]}");
        }
        );

        yield return ("DELETE", async (oh, ms) =>
        {
            using var respA = await oh.SendAsync(BenchmarkRequests.CreateDelete());
            using var respB = await ms.SendAsync(BenchmarkRequests.CreateDelete());
            Assert(respA.StatusCode == HttpStatusCode.NoContent, $"OhData DELETE returned {(int)respA.StatusCode}, expected 204");
            Assert(respB.StatusCode == HttpStatusCode.NoContent, $"MS OData DELETE returned {(int)respB.StatusCode}, expected 204");
        }
        );

        // ── Navigation scenarios (BenchDepartment/BenchEmployee) ────────────────────────────────────
        // Neither OData nor either server implementation guarantees ordering of a collection
        // navigation's members unless the request itself carries a nested $orderby — so where a
        // scenario below doesn't request one, the check normalizes by comparing the CHILD ID SET
        // (sorted) instead of the raw sequence. That is a normalization of an already-unordered
        // dimension, not a weakening of the check: parent ordering, child membership/count, and every
        // nested projection are still asserted exactly.

        yield return ("$expand collection nav (pushdown JOIN)", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.DeptExpandCollectionUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.DeptExpandCollectionUrl, HttpStatusCode.OK);
            AssertSameParentsAndChildIdSets(a, b, "Employees", expectedParents: BenchOrgData.DepartmentPageSize);
            // The request now carries an explicit $top=DepartmentPageSize (root paging — see
            // BenchmarkRequests), so the expanded total only covers the departments on that first page,
            // not every department; AssertSameParentsAndChildIdSets already proves per-parent child sets
            // match, so this just reconfirms the aggregate is identical rather than merely per-parent.
            int totalA = SumChildCounts(a, "Employees");
            int totalB = SumChildCounts(b, "Employees");
            Assert(totalA == totalB, $"expanded Employees totals differ across the paged departments: OhData={totalA} MS={totalB}");
        }
        );

        yield return ("nested $expand=A($expand=B)", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.DeptExpandNestedUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.DeptExpandNestedUrl, HttpStatusCode.OK);
            AssertSameParentsAndChildIdSets(a, b, "Employees", expectedParents: BenchOrgData.DepartmentPageSize);
            AssertSameNestedSingleValued(a, b, "Employees", "Manager");
        }
        );

        yield return ("$expand nested $top;$orderby;$count;$select", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.DeptExpandNestedOptionsUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.DeptExpandNestedOptionsUrl, HttpStatusCode.OK);
            var deptsA = (JsonArray)a["value"]!;
            var deptsB = (JsonArray)b["value"]!;
            Assert(deptsA.Count == BenchOrgData.DepartmentPageSize, $"OhData returned {deptsA.Count} departments, expected {BenchOrgData.DepartmentPageSize}");
            Assert(deptsB.Count == BenchOrgData.DepartmentPageSize, $"MS OData returned {deptsB.Count} departments, expected {BenchOrgData.DepartmentPageSize}");

            for (int i = 0; i < deptsA.Count; i++)
            {
                long deptIdA = (long)deptsA[i]!["Id"]!;
                long deptIdB = (long)deptsB[i]!["Id"]!;
                Assert(deptIdA == deptIdB, $"department[{i}] id differs: OhData={deptIdA} MS={deptIdB}");

                var employeesA = (JsonArray)deptsA[i]!["Employees"]!;
                var employeesB = (JsonArray)deptsB[i]!["Employees"]!;
                Assert(employeesA.Count <= 10, $"department {deptIdA} OhData windowed Employees returned {employeesA.Count}, expected <= 10");
                long[] idsA = employeesA.Select(e => (long)e!["Id"]!).ToArray();
                long[] idsB = employeesB.Select(e => (long)e!["Id"]!).ToArray();
                Assert(idsA.SequenceEqual(idsB), $"department {deptIdA} windowed Employees id sequence differs: OhData [{string.Join(",", idsA)}] vs MS [{string.Join(",", idsB)}]");

                long countA = (long)deptsA[i]!["Employees@odata.count"]!;
                long countB = (long)deptsB[i]!["Employees@odata.count"]!;
                Assert(countA == countB, $"department {deptIdA} Employees@odata.count differs: OhData={countA} MS={countB}");

                if (employeesA.Count > 0)
                {
                    foreach (var (label, item) in new[] { ("OhData", employeesA[0]!), ("MS OData", employeesB[0]!) })
                    {
                        string[] props = ((JsonObject)item)
                            .Where(kv => !kv.Key.StartsWith('@'))
                            .Select(kv => kv.Key)
                            .OrderBy(k => k, StringComparer.Ordinal)
                            .ToArray();
                        Assert(props.SequenceEqual(new[] { "Id", "Name" }),
                            $"{label} nested $select shape for department {deptIdA} was [{string.Join(",", props)}], expected [Id,Name]");
                    }
                }
            }
        }
        );

        yield return ("$select + $expand combined", async (oh, ms) =>
        {
            var a = await GetJsonAsync(oh, BenchmarkRequests.DeptSelectExpandUrl, HttpStatusCode.OK);
            var b = await GetJsonAsync(ms, BenchmarkRequests.DeptSelectExpandUrl, HttpStatusCode.OK);
            AssertSameParentsAndChildIdSets(a, b, "Employees", expectedParents: BenchOrgData.DepartmentPageSize);
            foreach (var (label, json) in new[] { ("OhData", a), ("MS OData", b) })
            {
                string[] props = ((JsonObject)json["value"]![0]!)
                    .Where(kv => !kv.Key.StartsWith('@'))
                    .Select(kv => kv.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
                Assert(props.SequenceEqual(new[] { "Employees", "Id", "Name" }),
                    $"{label} $select+$expand shape was [{string.Join(",", props)}], expected [Employees,Id,Name]");
            }
        }
        );

        yield return ("$levels (self-referential)", async (oh, ms) =>
        {
            var envA = await GetJsonAsync(oh, BenchmarkRequests.EmployeeLevelsUrl, HttpStatusCode.OK);
            var envB = await GetJsonAsync(ms, BenchmarkRequests.EmployeeLevelsUrl, HttpStatusCode.OK);
            var valueA = (JsonArray)envA["value"]!;
            var valueB = (JsonArray)envB["value"]!;
            Assert(valueA.Count == 1, $"OhData $filter=Id eq {BenchOrgData.RootEmployeeId} returned {valueA.Count} rows, expected 1");
            Assert(valueB.Count == 1, $"MS OData $filter=Id eq {BenchOrgData.RootEmployeeId} returned {valueB.Count} rows, expected 1");
            JsonNode a = valueA[0]!;
            JsonNode b = valueB[0]!;

            long rootIdA = (long)a["Id"]!;
            long rootIdB = (long)b["Id"]!;
            Assert(rootIdA == BenchOrgData.RootEmployeeId, $"OhData root id was {rootIdA}, expected {BenchOrgData.RootEmployeeId}");
            Assert(rootIdB == BenchOrgData.RootEmployeeId, $"MS OData root id was {rootIdB}, expected {BenchOrgData.RootEmployeeId}");

            // The manager tree's branching factor is seed-derived (bounded — see
            // BenchOrgData.Min/MaxManagerBranchingFactor), so the exact tree size at $levels=2 varies by
            // seed instead of being a fixed 31. What's asserted instead: the tree is non-trivial (more
            // than just the root — proves at least one level actually expanded), bounded by the worst-case
            // branching factor two levels deep (proves the tree didn't runaway/cycle), and — the part that
            // actually matters for a fair comparison — both hosts agree on the EXACT same set of ids.
            const int minPlausibleTreeSize = 1 + BenchOrgData.MinManagerBranchingFactor;
            const int maxPlausibleTreeSize = 1 + BenchOrgData.MaxManagerBranchingFactor + BenchOrgData.MaxManagerBranchingFactor * BenchOrgData.MaxManagerBranchingFactor;
            long[] idsA = CollectTreeIds(a, "Reports").OrderBy(x => x).ToArray();
            long[] idsB = CollectTreeIds(b, "Reports").OrderBy(x => x).ToArray();
            Assert(idsA.Length >= minPlausibleTreeSize && idsA.Length <= maxPlausibleTreeSize,
                $"OhData $levels tree had {idsA.Length} employees, expected between {minPlausibleTreeSize} and {maxPlausibleTreeSize}");
            Assert(idsB.Length >= minPlausibleTreeSize && idsB.Length <= maxPlausibleTreeSize,
                $"MS OData $levels tree had {idsB.Length} employees, expected between {minPlausibleTreeSize} and {maxPlausibleTreeSize}");
            Assert(idsA.SequenceEqual(idsB), $"$levels tree id sets differ: OhData [{string.Join(",", idsA)}] vs MS [{string.Join(",", idsB)}]");
        }
        );
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(url);
        return await ReadJsonAsync(response, expected, $"GET {url}");
    }

    private static async Task<JsonNode> SendJsonAsync(HttpClient client, HttpRequestMessage request, HttpStatusCode expected)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            return await ReadJsonAsync(response, expected, $"{request.Method} {request.RequestUri}");
        }
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected, string what)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert(response.StatusCode == expected,
            $"{what} returned {(int)response.StatusCode}, expected {(int)expected}. Body: {Truncate(body)}");
        try
        {
            return JsonNode.Parse(body)!;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new SmokeFailureException($"{what} returned unparseable JSON ({ex.Message}): {Truncate(body)}");
        }
    }

    private static IEnumerable<long> GetIds(JsonNode envelope) =>
        ((JsonArray)envelope["value"]!).Select(item => (long)item!["Id"]!);

    private static void AssertSameIds(JsonNode a, JsonNode b, int expectedCount)
    {
        long[] idsA = GetIds(a).ToArray();
        long[] idsB = GetIds(b).ToArray();
        Assert(idsA.Length == expectedCount, $"OhData returned {idsA.Length} items, expected {expectedCount}");
        Assert(idsB.Length == expectedCount, $"MS OData returned {idsB.Length} items, expected {expectedCount}");
        Assert(idsA.SequenceEqual(idsB),
            $"item id sequences differ: OhData [{string.Join(",", idsA.Take(5))}...] vs MS [{string.Join(",", idsB.Take(5))}...]");
    }

    private static void AssertSameEntity(JsonNode a, JsonNode b, int expectedId)
    {
        foreach (string prop in new[] { "Id", "Name", "Category", "Price", "IsActive" })
        {
            string? valA = a[prop]?.ToJsonString();
            string? valB = b[prop]?.ToJsonString();
            Assert(valA is not null && valA == valB, $"property '{prop}' differs: OhData={valA} MS={valB}");
        }
        Assert((long)a["Id"]! == expectedId, $"expected id {expectedId}, got {a["Id"]}");
    }

    /// <summary>
    /// Compares a <c>$expand</c>'d collection response: same parent id sequence (order matters — the
    /// request always carries a root <c>$orderby=id</c>) and, per parent, the same SET of expanded
    /// child ids (order does NOT matter unless the request also carries a nested <c>$orderby</c> —
    /// neither server implementation guarantees child ordering without one) AND, per matched child
    /// (matched by id), the same value for every structural property — not just <c>Id</c>. A host that
    /// dropped or corrupted <c>Salary</c>/<c>DepartmentId</c>/<c>ManagerId</c> on the expanded
    /// <c>BenchEmployee</c> children would pass an id-only check but fails this one, the same
    /// full-value discipline <see cref="AssertSameEntity"/> already applies to top-level entities.
    /// </summary>
    private static void AssertSameParentsAndChildIdSets(JsonNode a, JsonNode b, string childProperty, int expectedParents)
    {
        var parentsA = (JsonArray)a["value"]!;
        var parentsB = (JsonArray)b["value"]!;
        Assert(parentsA.Count == expectedParents, $"OhData returned {parentsA.Count} parents, expected {expectedParents}");
        Assert(parentsB.Count == expectedParents, $"MS OData returned {parentsB.Count} parents, expected {expectedParents}");

        for (int i = 0; i < expectedParents; i++)
        {
            long parentIdA = (long)parentsA[i]!["Id"]!;
            long parentIdB = (long)parentsB[i]!["Id"]!;
            Assert(parentIdA == parentIdB, $"parent[{i}] id differs: OhData={parentIdA} MS={parentIdB}");

            var childrenA = ((JsonArray)parentsA[i]![childProperty]!).ToDictionary(c => (long)c!["Id"]!, c => c!);
            var childrenB = ((JsonArray)parentsB[i]![childProperty]!).ToDictionary(c => (long)c!["Id"]!, c => c!);
            long[] childIdsA = childrenA.Keys.OrderBy(x => x).ToArray();
            long[] childIdsB = childrenB.Keys.OrderBy(x => x).ToArray();
            Assert(childIdsA.SequenceEqual(childIdsB),
                $"parent {parentIdA} {childProperty} id set differs: OhData [{string.Join(",", childIdsA)}] vs MS [{string.Join(",", childIdsB)}]");

            foreach (var (childId, childA) in childrenA)
            {
                AssertSameProperties(childA, childrenB[childId], BenchEmployeeProperties,
                    $"parent {parentIdA} {childProperty} child {childId}");
            }
        }
    }

    /// <summary>Sums the size of <paramref name="childProperty"/> across every item in a collection response's <c>value</c> array.</summary>
    private static int SumChildCounts(JsonNode envelope, string childProperty) =>
        ((JsonArray)envelope["value"]!).Sum(parent => ((JsonArray)parent![childProperty]!).Count);

    /// <summary>
    /// For each expanded item under <paramref name="collectionProperty"/> (order-independent — matched
    /// by id), asserts the nested single-valued navigation <paramref name="singleValuedProperty"/> is
    /// null on both hosts or present on both hosts with the same value for every structural property —
    /// not just <c>Id</c> — the same full-value discipline <see cref="AssertSameEntity"/> already
    /// applies to top-level entities.
    /// </summary>
    private static void AssertSameNestedSingleValued(JsonNode a, JsonNode b, string collectionProperty, string singleValuedProperty)
    {
        var parentsA = (JsonArray)a["value"]!;
        var parentsB = (JsonArray)b["value"]!;
        for (int i = 0; i < parentsA.Count; i++)
        {
            var childrenA = ((JsonArray)parentsA[i]![collectionProperty]!)
                .ToDictionary(c => (long)c!["Id"]!, c => c);
            var childrenB = ((JsonArray)parentsB[i]![collectionProperty]!)
                .ToDictionary(c => (long)c!["Id"]!, c => c);
            foreach (var (childId, childA) in childrenA)
            {
                JsonObject? nestedA = childA![singleValuedProperty] as JsonObject;
                JsonObject? nestedB = childrenB[childId]![singleValuedProperty] as JsonObject;
                Assert((nestedA is null) == (nestedB is null),
                    $"{collectionProperty} {childId} nested {singleValuedProperty} null-ness differs: OhData={(nestedA is null ? "null" : "present")} MS={(nestedB is null ? "null" : "present")}");
                if (nestedA is not null && nestedB is not null)
                {
                    AssertSameProperties(nestedA, nestedB, BenchEmployeeProperties,
                        $"{collectionProperty} {childId} nested {singleValuedProperty}");
                }
            }
        }
    }

    /// <summary>Structural (non-navigation) properties of a bare (no <c>$select</c>) <c>BenchEmployee</c>
    /// projection — the shape every scenario feeding <see cref="AssertSameParentsAndChildIdSets"/> and
    /// <see cref="AssertSameNestedSingleValued"/> actually requests.</summary>
    private static readonly string[] BenchEmployeeProperties = { "Id", "Name", "Salary", "DepartmentId", "ManagerId" };

    /// <summary>Asserts <paramref name="a"/> and <paramref name="b"/> have the same value (including
    /// both-null) for every property in <paramref name="properties"/>. Mirrors <see cref="AssertSameEntity"/>'s
    /// full-value comparison, generalized to a caller-supplied property list and null-tolerant per property
    /// (unlike <see cref="AssertSameEntity"/>, which asserts non-null for its fixed widget property set).</summary>
    private static void AssertSameProperties(JsonNode a, JsonNode b, string[] properties, string context)
    {
        foreach (string prop in properties)
        {
            string? valA = a[prop]?.ToJsonString();
            string? valB = b[prop]?.ToJsonString();
            Assert(valA == valB, $"{context}: property '{prop}' differs: OhData={valA ?? "null"} MS={valB ?? "null"}");
        }
    }

    /// <summary>
    /// Recursively walks a <c>$levels</c>-expanded tree, collecting the <c>Id</c> of the given node and
    /// every descendant reachable through <paramref name="navProperty"/>. A node beyond the requested
    /// depth simply has no <paramref name="navProperty"/> array to recurse into, so this naturally stops
    /// at the boundary without needing to know the depth in advance.
    /// </summary>
    private static IEnumerable<long> CollectTreeIds(JsonNode node, string navProperty)
    {
        yield return (long)node["Id"]!;
        if (node[navProperty] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                foreach (long id in CollectTreeIds(child!, navProperty))
                    yield return id;
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new SmokeFailureException(message);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
