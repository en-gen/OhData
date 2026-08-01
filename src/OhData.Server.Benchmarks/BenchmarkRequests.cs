using System.Net.Http;
using System.Text;
using System.Text.Json;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks;

/// <summary>
/// The exact requests exercised by both the smoke check and the benchmarks — one definition so
/// what is verified for correctness is precisely what is measured. All URLs are relative to the
/// per-host <c>/odata/</c> base address and are identical for both servers (both hosts emit
/// PascalCase post-flip, and the query-option property names below match both wire formats).
/// </summary>
internal static class BenchmarkRequests
{
    public const string GetAllUrl = "BenchWidgets";
    public const string FilterUrl = "BenchWidgets?$filter=price gt 500";
    public const string OrderByUrl = "BenchWidgets?$orderby=name desc";
    public const string SelectUrl = "BenchWidgets?$select=id,name";
    public const string TopSkipUrl = "BenchWidgets?$top=50&$skip=100&$orderby=id";
    public const string CountUrl = "BenchWidgets?$count=true&$filter=price gt 500";
    public const string GetByIdUrl = "BenchWidgets(500)";
    public const string EntityUrl = "BenchWidgets(500)";

    // ── Navigation scenarios (BenchDepartment/BenchEmployee, EF Core Sqlite-backed) ────────────────
    // Closes the $expand pushdown coverage gap: BenchWidget has zero EDM navigations, so none of the
    // benchmarks above exercise OhData's "one JOIN per page" $expand pushdown, nested $expand, $levels,
    // or a bidirectional (parent<->child) navigation pair — see BenchmarkHosts for why these five run
    // against EF Core Sqlite rather than the List&lt;T&gt; store the widget scenarios use.
    //
    // ── Known asymmetries the smoke gate tolerates (adversarial review fold-in) ─────────────────────
    // These are genuine, understood differences between the two hosts' responses on these five
    // scenarios. None distorts the fairness of the comparison (row content is identical, or the
    // divergence is a pre-existing spec nit outside what is being measured) but none of them is
    // caught by the smoke check either, so they are recorded here instead of being rediscovered:
    //   1. OhData emits `@odata.nextLink` on all five BenchDepartments responses; Microsoft emits
    //      none. OhData applies MaxTop=20 (BenchOrgData.DepartmentPageSize) as an implicit page
    //      limit and, because the page comes back exactly full (DepartmentCount is also 20),
    //      advertises a next page that doesn't exist. No timing distortion — same rows either way —
    //      but the two responses are not byte-for-byte/semantically identical envelopes.
    //   2. `@odata.context` differs: `#BenchDepartments` on OhData vs
    //      `#BenchDepartments(Employees())` on MS OData — OhData omits the expand clause from the
    //      context URL. Pre-existing spec nit, unrelated to this branch.
    //   3. MaxTop means different things on the two sides. OhData treats it as an implicit page
    //      size (applied even when the client sends no $top); MS's [EnableQuery(MaxTop=...)] only
    //      caps a client-*supplied* $top and does nothing to an unpaged request on its own. This is
    //      neutral for every scenario here only because DepartmentPageSize == DepartmentCount == 20
    //      (see point 4 below) and EmployeePageSize == EmployeeCount for the one BenchEmployees
    //      scenario ($levels), which additionally $filters down to a single row. An unfiltered,
    //      unpaged BenchEmployees scenario would diverge sharply (OhData would page to 100 rows,
    //      MS OData would return all 1000) and nothing in this suite would catch it.
    //   4. `[EnableQuery(PageSize=...)]` is deliberately omitted on both new MS controllers
    //      (BenchDepartmentsController, BenchEmployeesController) — see the reasoning documented on
    //      each: PageSize wraps every collection in the response (including expanded/nested ones) in
    //      a TruncatedCollection, and composing that with nested $expand needs the SQL APPLY
    //      operation, which SQLite's EF Core provider can't emit (500). Don't "fix" this later
    //      without re-reading that reasoning.
    //
    // ── Dataset caveats (not fairness defects, but they limit generalization) ───────────────────────
    //   - Fan-out is perfectly uniform: exactly EmployeeCount/DepartmentCount = 50 employees per
    //     department, and exactly ManagerBranchingFactor = 5 reports per manager. Uniform fan-out is
    //     the easy case for a JOIN/pushdown strategy; skewed fan-out (a handful of huge departments or
    //     managers) is where nested-$top windowing strategies are most likely to diverge, and this
    //     suite never exercises that.
    //   - DepartmentPageSize == DepartmentCount, so root-collection paging (a second page of
    //     departments) is never exercised by any expand scenario — every BenchDepartments scenario
    //     here returns its entire result set on page one.

    /// <summary>Bare <c>$expand</c> of a collection navigation — the pushdown JOIN itself.</summary>
    public const string DeptExpandCollectionUrl = "BenchDepartments?$expand=Employees&$orderby=id";

    /// <summary>Nested <c>$expand=A($expand=B)</c> — a 3-table JOIN chain (Department → Employee →
    /// Employee-as-Manager, the self-referential single-valued nav).</summary>
    public const string DeptExpandNestedUrl = "BenchDepartments?$expand=Employees($expand=Manager)&$orderby=id";

    /// <summary><c>$expand</c> carrying nested <c>$top</c>/<c>$orderby</c>/<c>$count</c>/<c>$select</c> —
    /// all applied per parent, windowed and pruned in the same JOIN'd query.</summary>
    public const string DeptExpandNestedOptionsUrl =
        "BenchDepartments?$expand=Employees($top=10;$orderby=id;$count=true;$select=Id,Name)&$orderby=id";

    /// <summary><c>$select</c> and <c>$expand</c> combined — root columns pruned, expanded collection
    /// left unfiltered.</summary>
    public const string DeptSelectExpandUrl = "BenchDepartments?$select=Id,Name&$expand=Employees&$orderby=id";

    /// <summary>
    /// <c>$levels</c> on the self-referential manager tree, rooted at <see cref="BenchOrgData.RootEmployeeId"/>:
    /// root + 5 direct reports + 25 of their reports = 31 employees (see <see cref="BenchOrgData"/>).
    /// Goes through the COLLECTION route with a <c>$filter</c> down to the single root row rather than
    /// <c>GetById</c>: OhData's (and, empirically, Microsoft.AspNetCore.OData's) <c>$expand</c> pushdown
    /// rewrites the LINQ query feeding the response, which requires the entity to still be an unmaterialized
    /// <c>IQueryable</c> element when <c>$expand</c> is applied — true for the collection route (built
    /// straight off <c>GetQueryable</c>) but not for a single-entity <c>GetById</c> route backed by a
    /// custom handler that already materialized the row (both hosts' <c>GetById</c> here do exactly that
    /// — a plain <c>FirstOrDefault</c> — so a delegate-less/pushdown nav comes back empty there on
    /// EITHER host, verified empirically). <c>$filter</c> down to one row on the collection route sidesteps
    /// that entirely and is the shape actually being measured.
    /// </summary>
    public static readonly string EmployeeLevelsUrl =
        $"BenchEmployees?$filter=Id eq {BenchOrgData.RootEmployeeId}&$expand=Reports($levels={BenchOrgData.LevelsExpandDepth})";

    private static readonly string PostJson = JsonSerializer.Serialize(new
    {
        name = "NewWidget",
        category = "Alpha",
        price = 9.99m,
        isActive = true,
        createdAt = "2026-01-01T00:00:00Z",
    });

    private static readonly string PutJson = JsonSerializer.Serialize(new
    {
        id = BenchmarkData.LookupId,
        name = "Updated",
        category = "Beta",
        price = 1.00m,
        isActive = false,
        createdAt = "2026-01-01T00:00:00Z",
    });

    private static readonly string PatchJson = JsonSerializer.Serialize(new { name = "Patched-Smoke" });

    public static HttpRequestMessage CreatePost() => new(HttpMethod.Post, GetAllUrl)
    {
        Content = new StringContent(PostJson, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// PUT with <c>Prefer: return=representation</c> on both hosts. Microsoft.AspNetCore.OData's
    /// <c>Updated()</c> returns 204 No Content unless the client asks for the representation;
    /// OhData returns 200 + body by default and honours the same preference. Sending the header
    /// to both keeps requests and response semantics symmetric (200 + entity body on each side).
    /// </summary>
    public static HttpRequestMessage CreatePut()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, EntityUrl)
        {
            Content = new StringContent(PutJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        return request;
    }

    /// <summary>PATCH with <c>Prefer: return=representation</c> — same rationale as <see cref="CreatePut"/>.</summary>
    public static HttpRequestMessage CreatePatch()
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, EntityUrl)
        {
            Content = new StringContent(PatchJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        return request;
    }

    public static HttpRequestMessage CreateDelete() => new(HttpMethod.Delete, EntityUrl);
}
