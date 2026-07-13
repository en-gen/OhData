using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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

    public static async Task<bool> RunAsync()
    {
        Console.WriteLine("── Smoke check: verifying both hosts return semantically equivalent responses ──");

        var (ohApp, oh) = await BenchmarkHosts.StartOhDataAsync();
        var (msApp, ms) = await BenchmarkHosts.StartMsODataAsync();
        await using var ohAppScope = ohApp;
        using var ohScope = oh;
        await using var msAppScope = msApp;
        using var msScope = ms;

        int failures = 0;
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
                Assert(props.SequenceEqual(new[] { "id", "name" }),
                    $"{label} $select shape was [{string.Join(",", props)}], expected [id,name]");
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
            Assert((string)a["name"]! == "Patched-Smoke", $"OhData PATCH did not apply delta: name={a["name"]}");
            Assert((string)b["name"]! == "Patched-Smoke", $"MS OData PATCH did not apply delta: name={b["name"]}");
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
        catch (Exception ex)
        {
            throw new SmokeFailureException($"{what} returned unparseable JSON ({ex.Message}): {Truncate(body)}");
        }
    }

    private static IEnumerable<long> GetIds(JsonNode envelope) =>
        ((JsonArray)envelope["value"]!).Select(item => (long)item!["id"]!);

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
        foreach (string prop in new[] { "id", "name", "category", "price", "isActive" })
        {
            string? valA = a[prop]?.ToJsonString();
            string? valB = b[prop]?.ToJsonString();
            Assert(valA is not null && valA == valB, $"property '{prop}' differs: OhData={valA} MS={valB}");
        }
        Assert((long)a["id"]! == expectedId, $"expected id {expectedId}, got {a["id"]}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new SmokeFailureException(message);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
