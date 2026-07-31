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
