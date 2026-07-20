using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Coverage pass: structural-property routes (GET/`$value`/PUT/PATCH/DELETE on
/// <c>/{Set}({key})/{Property}</c>) — the bad-key rejections, raw-value formatting for
/// non-string primitive types, the <c>byte[]</c> octet-stream path, null-to-non-nullable rejection,
/// conditional GET (304), and the DELETE ETag header — none of which prior tests exercised.
/// </summary>
public class CoveragePropertyRouteTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("GET", "/odata/Docs(abc)/Title")]
    [InlineData("GET", "/odata/Docs(abc)/Title/$value")]
    [InlineData("PATCH", "/odata/Docs(abc)/Title")]
    [InlineData("DELETE", "/odata/Docs(abc)/Note")]
    public async Task PropertyRoute_BadKeyFormat_Returns400(string method, string url)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "PATCH") req.Content = Json("{\"value\":\"x\"}");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ByteArrayProperty_Value_ReturnsOctetStream()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        var resp = await fx.Client.GetAsync("/odata/Docs(1)/Data/$value");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await resp.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("Active", "true")]
    [InlineData("CreatedAt", "2020")] // ISO-8601 date contains the year
    public async Task PrimitiveProperty_Value_FormatsAsText(string prop, string expectedSubstring)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        var resp = await fx.Client.GetAsync($"/odata/Docs(1)/{prop}/$value");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain", resp.Content.Headers.ContentType?.MediaType);
        Assert.Contains(expectedSubstring, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PropertyWrite_NullToNonNullableValueType_Returns400()
    {
        // Active is a non-nullable value type (bool); setting it to null must be rejected.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        var resp = await fx.Client.PatchAsync("/odata/Docs(1)/Active", Json("{\"value\":null}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PropertyGet_IfNoneMatch_MatchingEtag_Returns304()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        var first = await fx.Client.GetAsync("/odata/Docs(1)/Title");
        string? etag = first.Headers.ETag?.ToString();
        Assert.NotNull(etag);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/odata/Docs(1)/Title");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, resp.StatusCode);
    }

    [Fact]
    public async Task PropertyDelete_Nullable_EmitsEtagHeader()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DocProfile>());
        var resp = await fx.Client.DeleteAsync("/odata/Docs(1)/Note");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.NotNull(resp.Headers.ETag);
    }
}

internal class Doc
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public bool Active { get; set; }
    public DateTime CreatedAt { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

internal class DocProfile : EntitySetProfile<int, Doc>
{
    private readonly Doc _doc = new()
    {
        Id = 1,
        Title = "Title",
        Note = "note",
        Active = true,
        CreatedAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Data = new byte[] { 1, 2, 3 },
    };

    public DocProfile() : base(x => x.Id)
    {
        EntitySetName = "Docs";
        UseETag(x => x.Id, x => x.Title);
        GetById = (id, ct) => Task.FromResult<Doc?>(id == 1 ? _doc : null);
        Patch = (id, delta, ct) =>
        {
            if (id != 1) return Task.FromResult<Doc?>(null);
            delta.Patch(_doc);
            return Task.FromResult<Doc?>(_doc);
        };
    }
}
