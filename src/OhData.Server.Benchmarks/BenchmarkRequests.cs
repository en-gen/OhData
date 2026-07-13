using System.Net.Http;
using System.Text;
using System.Text.Json;
using OhData.Server.Benchmarks.Model;

namespace OhData.Server.Benchmarks;

/// <summary>
/// The exact requests exercised by both the smoke check and the benchmarks — one definition so
/// what is verified for correctness is precisely what is measured. All URLs are relative to the
/// per-host <c>/odata/</c> base address and are identical for both servers (camelCase property
/// names in query options match both wire formats).
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
