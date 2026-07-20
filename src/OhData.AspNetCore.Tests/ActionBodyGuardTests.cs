using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// B2: the four route families that read a JSON request body by hand (entity-level bound
/// actions, collection-level bound actions, unbound actions, and <c>$ref</c> POST/PUT) must
/// never return a raw, envelope-less 500 for an empty, malformed, non-object, or wrong-content-
/// type body -- the same bug class already fixed for POST/PUT/PATCH (see the "BUG 2 fix" /
/// "B2 fix" comments in <see cref="OhDataEndpointFactory"/>).
///
/// S7: a handler that throws (rather than returning an <c>ODataError</c> <c>IResult</c>) must
/// also produce a 500 with the OData error envelope, a generic message, and never leak the
/// exception's own message or stack trace.
/// </summary>
public class ActionBodyGuardTests
{
    private static StringContent JsonBody(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static StringContent WrongContentType(string body) =>
        new(body, Encoding.UTF8, "text/plain");

    /// <summary>
    /// Asserts the response carries the OData error envelope (§9.4:
    /// <c>{"error":{"code":"...","message":"..."}}</c>) with a non-empty body -- the exact
    /// shape every deliberate error path in <see cref="OhDataEndpointFactory"/> produces.
    /// </summary>
    private static async Task<JsonElement> AssertErrorEnvelope(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(raw), "error response body must not be empty");
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.True(json.TryGetProperty("error", out JsonElement error), "response is missing the 'error' envelope");
        Assert.True(error.TryGetProperty("code", out JsonElement code), "error envelope is missing 'code'");
        Assert.True(error.TryGetProperty("message", out _), "error envelope is missing 'message'");
        Assert.False(string.IsNullOrWhiteSpace(code.GetString()), "error code must not be empty");
        return json;
    }

    // ── Collection-level bound actions: POST /{EntitySet}/{ActionName} ────────────

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task CollectionBoundAction_BadBody_Returns400WithEnvelope(string body)
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new BoundOpsStore()));

        var response = await fx.Client.PostAsync("/odata/BoundWidgets/AddSuffix", JsonBody(body));

        await AssertErrorEnvelope(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CollectionBoundAction_WrongContentType_Returns415WithEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new BoundOpsStore()));

        var response = await fx.Client.PostAsync(
            "/odata/BoundWidgets/AddSuffix", WrongContentType("{\"suffix\":\"!\"}"));

        await AssertErrorEnvelope(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task CollectionBoundAction_ZeroParam_EmptyBody_StillReturns204()
    {
        // Regression guard: an action with zero parameters never reads the body at all, so an
        // empty (or any) body/content-type must keep working exactly as before this fix.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new BoundOpsStore()));

        var response = await fx.Client.PostAsync("/odata/BoundWidgets/ClearAll", JsonBody(""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── Entity-level bound actions: POST /{EntitySet}({key})/{ActionName} ─────────

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task EntityBoundAction_BadBody_Returns400WithEnvelope(string body)
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EntityBoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new EntityBoundOpsStore()));

        var response = await fx.Client.PostAsync("/odata/EntityBoundWidgets(1)/RenameWidget", JsonBody(body));

        await AssertErrorEnvelope(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EntityBoundAction_WrongContentType_Returns415WithEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EntityBoundOpsProfile>(),
            configureServices: s => s.AddSingleton(new EntityBoundOpsStore()));

        var response = await fx.Client.PostAsync(
            "/odata/EntityBoundWidgets(1)/RenameWidget", WrongContentType("{\"newName\":\"X\"}"));

        await AssertErrorEnvelope(response, HttpStatusCode.UnsupportedMediaType);
    }

    // ── Unbound actions: POST /{prefix}/{ActionName} ───────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task UnboundAction_BadBody_Returns400WithEnvelope(string body)
    {
        Func<int, int, Task<int>> addDelegate = (a, b) => Task.FromResult(a + b);
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddAction(addDelegate, "AddNumbersGuard"));

        var response = await fx.Client.PostAsync("/odata/AddNumbersGuard", JsonBody(body));

        await AssertErrorEnvelope(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnboundAction_WrongContentType_Returns415WithEnvelope()
    {
        Func<int, int, Task<int>> addDelegate = (a, b) => Task.FromResult(a + b);
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddAction(addDelegate, "AddNumbersGuard2"));

        var response = await fx.Client.PostAsync(
            "/odata/AddNumbersGuard2", WrongContentType("{\"a\":1,\"b\":2}"));

        await AssertErrorEnvelope(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task UnboundAction_ZeroParam_EmptyBody_StillReturns204()
    {
        bool called = false;
        Func<Task> pingDelegate = () => { called = true; return Task.CompletedTask; };
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddAction(pingDelegate, "PingGuard"));

        var response = await fx.Client.PostAsync("/odata/PingGuard", JsonBody(""));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(called);
    }

    // ── $ref POST (collection nav) / PUT (single-valued nav) ──────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task RefPost_BadBody_Returns400WithEnvelope(string body)
    {
        // NavQueryProfile ("NavQueryParents") has a collection nav ("Children") with addRef
        // configured -- reused as-is from EndpointMappingTests's own $ref coverage.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());

        var response = await fx.Client.PostAsync("/odata/NavQueryParents(1)/Children/$ref", JsonBody(body));

        await AssertErrorEnvelope(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefPost_WrongContentType_Returns415WithEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());

        var response = await fx.Client.PostAsync(
            "/odata/NavQueryParents(1)/Children/$ref",
            WrongContentType("{\"@odata.id\":\"http://localhost/odata/Children(1)\"}"));

        await AssertErrorEnvelope(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public async Task RefPut_BadBody_Returns400WithEnvelope(string body)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RefPutGuardProfile>());

        var response = await fx.Client.PutAsync("/odata/RefPutGuardParents(1)/PrimaryChild/$ref", JsonBody(body));

        await AssertErrorEnvelope(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefPut_WrongContentType_Returns415WithEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<RefPutGuardProfile>());

        var response = await fx.Client.PutAsync(
            "/odata/RefPutGuardParents(1)/PrimaryChild/$ref",
            WrongContentType("{\"@odata.id\":\"http://localhost/odata/Children(1)\"}"));

        await AssertErrorEnvelope(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task RefPost_ValidBody_StillReturns204()
    {
        // Regression guard: the happy path must still work after adding the body-shape guards.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<NavQueryProfile>());

        var response = await fx.Client.PostAsync(
            "/odata/NavQueryParents(1)/Children/$ref",
            JsonBody("{\"@odata.id\":\"http://localhost/odata/Children(1)\"}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ── S7: unhandled handler exceptions get a 500 with the OData error envelope ──

    [Fact]
    public async Task ThrowingGetAll_Returns500WithGenericEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowGuardProfile>());

        var response = await fx.Client.GetAsync("/odata/ThrowGuardWidgets");

        JsonElement json = await AssertErrorEnvelope(response, HttpStatusCode.InternalServerError);
        string code = json.GetProperty("error").GetProperty("code").GetString() ?? "";
        string message = json.GetProperty("error").GetProperty("message").GetString() ?? "";
        Assert.Equal("InternalServerError", code);
        AssertNoLeakedExceptionDetails(message);
    }

    [Fact]
    public async Task ThrowingBoundAction_Returns500WithGenericEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowGuardProfile>());

        var response = await fx.Client.PostAsync("/odata/ThrowGuardWidgets/Boom", JsonBody("{}"));

        JsonElement json = await AssertErrorEnvelope(response, HttpStatusCode.InternalServerError);
        string code = json.GetProperty("error").GetProperty("code").GetString() ?? "";
        string message = json.GetProperty("error").GetProperty("message").GetString() ?? "";
        Assert.Equal("InternalServerError", code);
        AssertNoLeakedExceptionDetails(message);
    }

    [Fact]
    public async Task ThrowingNavHandler_Returns500WithGenericEnvelope()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowGuardProfile>());

        var response = await fx.Client.GetAsync("/odata/ThrowGuardWidgets(1)/Children");

        JsonElement json = await AssertErrorEnvelope(response, HttpStatusCode.InternalServerError);
        string code = json.GetProperty("error").GetProperty("code").GetString() ?? "";
        string message = json.GetProperty("error").GetProperty("message").GetString() ?? "";
        Assert.Equal("InternalServerError", code);
        AssertNoLeakedExceptionDetails(message);
    }

    [Fact]
    public async Task ThrowingHandler_DoesNotSwallowSiblingRoute()
    {
        // The exception filter must not interfere with routes that deliberately return an
        // ODataError IResult -- those are return values, never thrown, so they must reach the
        // client completely unaffected by the presence of the group-level exception filter.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowGuardProfile>());

        var response = await fx.Client.GetAsync("/odata/ThrowGuardWidgets(notanint)/Children");

        // Bad key format is a deliberate 400 (FormatException caught locally), not the
        // exception-filter's generic 500 path.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static void AssertNoLeakedExceptionDetails(string message)
    {
        Assert.DoesNotContain("secret-db", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal); // stack-trace frame marker
    }

    // ── Fixtures local to this file ────────────────────────────────────────────────

    private class RefPutGuardParent
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public RefPutGuardChild? PrimaryChild { get; set; }
    }

    private class RefPutGuardChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Minimal single-valued nav profile exposing PUT $ref (setRef) for body-guard coverage.</summary>
    private class RefPutGuardProfile : EntitySetProfile<int, RefPutGuardParent>
    {
        public RefPutGuardProfile() : base(x => x.Id)
        {
            EntitySetName = "RefPutGuardParents";
            GetById = (id, ct) => Task.FromResult<RefPutGuardParent?>(new RefPutGuardParent { Id = id, Name = "P" });

            HasOptional(
                navigation: x => x.PrimaryChild!,
                get: (parentId, ct) => Task.FromResult<RefPutGuardChild?>(null),
                setRef: (parentId, relatedId, ct) => Task.CompletedTask);
        }
    }

    private class ThrowGuardWidget
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public IEnumerable<ThrowGuardChild>? Children { get; set; }
    }

    private class ThrowGuardChild
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Every handler deliberately throws instead of returning a result, to exercise the S7
    /// group-level exception filter. The exception messages embed fake secrets so tests can
    /// assert they never reach the response body.
    /// </summary>
    private class ThrowGuardProfile : EntitySetProfile<int, ThrowGuardWidget>
    {
        public ThrowGuardProfile() : base(x => x.Id)
        {
            EntitySetName = "ThrowGuardWidgets";

            GetAll = (ct) => throw new InvalidOperationException(
                "simulated failure: Server=secret-db;Password=hunter2");
            GetById = (id, ct) => throw new InvalidOperationException(
                "simulated failure: Server=secret-db;Password=hunter2");

            BindAction(Boom);

            HasMany(x => x.Children!, getAll: (parentId, ct) => throw new InvalidOperationException(
                "simulated nav failure: Server=secret-db;Password=hunter2"));
        }

        private void Boom() => throw new InvalidOperationException(
            "simulated action failure: Server=secret-db;Password=hunter2");
    }
}
