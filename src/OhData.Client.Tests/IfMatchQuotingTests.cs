using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.Client.Tests;

/// <summary>
/// S10: <c>If-Match</c>/<c>If-None-Match</c> must always be sent as an RFC 7232 §2.3
/// entity-tag (<c>DQUOTE *etagc DQUOTE</c>, optionally <c>W/</c>-prefixed). Previously the
/// client sent whatever string the caller passed through unmodified — including the unquoted
/// value <see cref="KeyedEntitySetClient{T}.GetWithETagAsync"/> itself returns (it strips the
/// response's quotes before handing the ETag back to the caller) — which a strict server
/// rejects or never matches. These tests capture the outgoing request via a fake
/// <see cref="HttpMessageHandler"/> so they exercise the real header-construction path without
/// depending on a live server.
/// </summary>
public class IfMatchQuotingTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":1,\"name\":\"Sprocket\"}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static (OhDataClient Client, CapturingHandler Handler) BuildClient()
    {
        var handler = new CapturingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/odata/") };
        return (new OhDataClient(httpClient), handler);
    }

    private static string? IfMatchHeader(CapturingHandler handler) =>
        handler.LastRequest!.Headers.TryGetValues("If-Match", out var values)
            ? System.Linq.Enumerable.First(values)
            : null;

    private static string? IfNoneMatchHeader(CapturingHandler handler) =>
        handler.LastRequest!.Headers.TryGetValues("If-None-Match", out var values)
            ? System.Linq.Enumerable.First(values)
            : null;

    // ── PUT ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutAsync_UnquotedIfMatch_IsQuotedOnTheWire()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).PutAsync(new Widget { Id = 1, Name = "X" }, ifMatch: "abc123");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    [Fact]
    public async Task PutAsync_AlreadyQuotedIfMatch_IsNotDoubleQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).PutAsync(new Widget { Id = 1, Name = "X" }, ifMatch: "\"abc123\"");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    // ── PATCH ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchAsync_UnquotedIfMatch_IsQuotedOnTheWire()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).PatchAsync(new { Name = "X" }, ifMatch: "abc123");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    [Fact]
    public async Task PatchAsync_AlreadyQuotedIfMatch_IsNotDoubleQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).PatchAsync(new { Name = "X" }, ifMatch: "\"abc123\"");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_UnquotedIfMatch_IsQuotedOnTheWire()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).DeleteAsync(ifMatch: "abc123");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    [Fact]
    public async Task DeleteAsync_AlreadyQuotedIfMatch_IsNotDoubleQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).DeleteAsync(ifMatch: "\"abc123\"");
        Assert.Equal("\"abc123\"", IfMatchHeader(handler));
    }

    // ── Wildcard and weak validators pass through untouched/correctly ─────────────

    [Fact]
    public async Task DeleteAsync_WildcardIfMatch_IsNotQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).DeleteAsync(ifMatch: "*");
        Assert.Equal("*", IfMatchHeader(handler));
    }

    [Fact]
    public async Task DeleteAsync_WeakIfMatch_QuotesOnlyTheOpaqueTag()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).DeleteAsync(ifMatch: "W/abc123");
        Assert.Equal("W/\"abc123\"", IfMatchHeader(handler));
    }

    [Fact]
    public async Task DeleteAsync_AlreadyQuotedWeakIfMatch_IsNotDoubleQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).DeleteAsync(ifMatch: "W/\"abc123\"");
        Assert.Equal("W/\"abc123\"", IfMatchHeader(handler));
    }

    // ── If-None-Match (conditional GET) ─────────────────────────────────────────

    [Fact]
    public async Task GetIfChangedAsync_UnquotedIfNoneMatch_IsQuotedOnTheWire()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).GetIfChangedAsync(ifNoneMatch: "abc123");
        Assert.Equal("\"abc123\"", IfNoneMatchHeader(handler));
    }

    [Fact]
    public async Task GetIfChangedAsync_AlreadyQuotedIfNoneMatch_IsNotDoubleQuoted()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).GetIfChangedAsync(ifNoneMatch: "\"abc123\"");
        Assert.Equal("\"abc123\"", IfNoneMatchHeader(handler));
    }

    // ── No header sent when ifMatch is null ─────────────────────────────────────

    [Fact]
    public async Task PutAsync_NullIfMatch_SendsNoIfMatchHeader()
    {
        var (client, handler) = BuildClient();
        await client.For<Widget>("Widgets").Key(1).PutAsync(new Widget { Id = 1, Name = "X" });
        Assert.Null(IfMatchHeader(handler));
    }
}
