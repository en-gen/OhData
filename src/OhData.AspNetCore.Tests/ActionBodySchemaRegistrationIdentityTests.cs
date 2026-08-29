using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #499: <c>ActionBodySchemaTypeFactory</c>'s memoization key omits registration identity, so two
/// named registrations that declare the same entity set name and the same action name share ONE
/// generated body-schema type — whichever registration maps first wins, and every other
/// registration's OpenAPI document shows the wrong request body shape.
///
/// <para>
/// The cache (<c>ActionBodySchemaTypeFactory</c>'s <c>s_cache</c>) is a process-wide static, and
/// that is deliberate — it exists so the <c>Expression</c>/<c>Reflection.Emit</c>-adjacent work in
/// <c>DefineType</c> runs once per distinct shape rather than once per request. The defect is the
/// KEY, not the cache's lifetime: none of the three call sites in
/// <c>OhDataEndpointFactory</c> include which registration the route belongs to, only the entity
/// set / action name (and for the unbound case, not even that — just the operation name).
/// </para>
///
/// <para>
/// Fixture shape is deliberately the one <c>docs/versioning.md</c> documents as the recommended
/// pattern: two named registrations (v1/v2) declaring the SAME entity set name ("SchemaOrders")
/// via two distinct profile types, mapped into ONE app so both routes' generated metadata can be
/// read and compared directly. v2's actions carry one extra parameter each, mirroring the
/// issue's own motivating scenario — a versioned action whose parameters evolved.
/// </para>
/// </summary>
public class ActionBodySchemaRegistrationIdentityTests
{
    /// <summary>
    /// Key site 1: the collection-level bound action key, <c>"{EntitySetName}.{ActionName}"</c>
    /// pre-fix. v1's <c>Submit(string note)</c> and v2's <c>Submit(string note, int priority)</c>
    /// both resolve to the entity set "SchemaOrders" and action "Submit".
    /// </summary>
    [Fact]
    public async Task CollectionBoundAction_TwoRegistrations_SameEntitySetAndActionName_GetDistinctBodySchemas()
    {
        await using var fx = await BuildTwoRegistrationFixture();

        Type v1Schema = BodySchemaFor(fx, "/v1", "SchemaOrders", "Submit");
        Type v2Schema = BodySchemaFor(fx, "/v2", "SchemaOrders", "Submit");

        AssertPropertyNames(v1Schema, "note");
        AssertPropertyNames(v2Schema, "note", "priority");
        Assert.NotSame(v1Schema, v2Schema);
    }

    /// <summary>
    /// Key site 2: the entity-level bound action key, <c>"{EntitySetName}.{ActionName}.Entity"</c>
    /// pre-fix. v1's <c>Approve(int key, string reason)</c> and v2's
    /// <c>Approve(int key, string reason, bool escalate)</c> both resolve to entity set
    /// "SchemaOrders" and action "Approve" (the leading key parameter is excluded from the body
    /// schema on both sides).
    /// </summary>
    [Fact]
    public async Task EntityBoundAction_TwoRegistrations_SameEntitySetAndActionName_GetDistinctBodySchemas()
    {
        await using var fx = await BuildTwoRegistrationFixture();

        Type v1Schema = BodySchemaFor(fx, "/v1", "SchemaOrders", "Approve");
        Type v2Schema = BodySchemaFor(fx, "/v2", "SchemaOrders", "Approve");

        AssertPropertyNames(v1Schema, "reason");
        AssertPropertyNames(v2Schema, "reason", "escalate");
        Assert.NotSame(v1Schema, v2Schema);
    }

    /// <summary>
    /// Key site 3: the unbound-operation key, <c>"Unbound.{Name}"</c> pre-fix — the worst of the
    /// three, per the #499/#425 discussion, because it carries neither registration nor entity set
    /// identity. v1's unbound action <c>Ping(string message)</c> and v2's unbound action
    /// <c>Ping(string message, int retries)</c> are registered under the same name "Ping" in two
    /// different registrations.
    /// </summary>
    [Fact]
    public async Task UnboundAction_TwoRegistrations_SameOperationName_GetDistinctBodySchemas()
    {
        await using var fx = await BuildTwoRegistrationFixture();

        Type v1Schema = UnboundBodySchemaFor(fx, "/v1", "Ping");
        Type v2Schema = UnboundBodySchemaFor(fx, "/v2", "Ping");

        AssertPropertyNames(v1Schema, "message");
        AssertPropertyNames(v2Schema, "message", "retries");
        Assert.NotSame(v1Schema, v2Schema);
    }

    // ── #547: the key is a NAME, not an identity ────────────────────────────────

    /// <summary>
    /// #547. #499 keyed the cache by <c>registration.Name</c>, which closes the case above (two
    /// DIFFERENTLY NAMED registrations) and leaves the one its own subject claimed to close:
    /// <c>Name</c> is <c>__default__</c> for EVERY unnamed registration in the process, so two
    /// independent <see cref="WebApplication"/>s that never heard of each other still produce the
    /// same key. The second host's OpenAPI document then silently documents the FIRST host's body
    /// shape.
    ///
    /// <para>
    /// Two hosts in one process is not an exotic shape — it is what every integration-test suite
    /// does, and it is exactly where a wrong OpenAPI document is hardest to attribute.
    /// </para>
    ///
    /// <para>
    /// All three key sites are asserted in one test because they share one fixture pair and one
    /// mechanism; splitting them would build four hosts to prove one thing three times.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoHosts_BothUnnamedRegistrations_SameEntitySetAndActionName_GetDistinctBodySchemas()
    {
        // Host 1 maps first and populates the shared static cache; host 2 is the one that would
        // silently inherit its schema.
        await using TestFixture host1 = await BuildSingleDefaultRegistrationHost(
            o => o.AddEntitySetProfile<ZzSchemaProfileHost1>().AddAction(ZzUnboundHandlers.SubmitHost1, "ZzPing"));
        await using TestFixture host2 = await BuildSingleDefaultRegistrationHost(
            o => o.AddEntitySetProfile<ZzSchemaProfileHost2>().AddAction(ZzUnboundHandlers.SubmitHost2, "ZzPing"));

        // Key site 1: collection-level bound action.
        Type h1Submit = BodySchemaFor(host1, "/odata", "ZZSchemas", "Submit");
        Type h2Submit = BodySchemaFor(host2, "/odata", "ZZSchemas", "Submit");
        AssertPropertyNames(h1Submit, "note");
        AssertPropertyNames(h2Submit, "note", "priority");
        Assert.NotSame(h1Submit, h2Submit);

        // Key site 2: entity-level bound action.
        Type h1Approve = BodySchemaFor(host1, "/odata", "ZZSchemas", "Approve");
        Type h2Approve = BodySchemaFor(host2, "/odata", "ZZSchemas", "Approve");
        AssertPropertyNames(h1Approve, "reason");
        AssertPropertyNames(h2Approve, "reason", "escalate");
        Assert.NotSame(h1Approve, h2Approve);

        // Key site 3: unbound action — the one carrying neither registration nor entity set name.
        Type h1Ping = UnboundBodySchemaFor(host1, "/odata", "ZzPing");
        Type h2Ping = UnboundBodySchemaFor(host2, "/odata", "ZzPing");
        AssertPropertyNames(h1Ping, "message");
        AssertPropertyNames(h2Ping, "message", "retries");
        Assert.NotSame(h1Ping, h2Ping);
    }

    /// <summary>
    /// The bounding half, and the reason the fix cannot simply be "stop memoizing". The cache is a
    /// process-wide static on purpose: it exists so the <c>Reflection.Emit</c> work in
    /// <c>DefineType</c> runs once per distinct shape rather than once per route mapped. Within ONE
    /// registration, the same route mapped twice — a host started, disposed, and started again over
    /// the same registration instance — must still hand back the same memoized type.
    /// </summary>
    [Fact]
    public async Task WithinOneRegistration_TheSchemaTypeIsStillMemoized()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o => o
            .WithPrefix("/odata")
            .AddEntitySetProfile<ZzSchemaProfileHost1>());

        var app = builder.Build();
        // Two MapOhData() calls over ONE registration instance: the second must hit the cache.
        app.MapOhData();
        app.MapOhData();
        await app.StartAsync();
        await using var fx = new TestFixture(app);

        EndpointDataSource source = fx.App.Services.GetRequiredService<EndpointDataSource>();
        List<Type> submitSchemas = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").EndsWith("/Submit", StringComparison.Ordinal))
            .Select(e => e.Metadata.GetMetadata<OhDataRequestBodyMetadata>())
            .Where(m => m is not null)
            .Select(m => m!.BodyType)
            .ToList();

        Assert.Equal(2, submitSchemas.Count);
        Assert.Same(submitSchemas[0], submitSchemas[1]);
    }

    // ── Fixture / helpers ───────────────────────────────────────────────────────

    private static async Task<TestFixture> BuildSingleDefaultRegistrationHost(Action<OhDataBuilder> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix("/odata");
            configure(o);
        });
        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static async Task<TestFixture> BuildTwoRegistrationFixture()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhData("v1", o => o
            .WithPrefix("/v1")
            .AddEntitySetProfile<SchemaOrderProfileV1>()
            .AddAction(UnboundActionHandlers.PingV1, "Ping"));

        builder.Services.AddOhData("v2", o => o
            .WithPrefix("/v2")
            .AddEntitySetProfile<SchemaOrderProfileV2>()
            .AddAction(UnboundActionHandlers.PingV2, "Ping"));

        var app = builder.Build();
        // Mapping order matters for the pre-fix repro: v1 maps (and populates the shared static
        // cache) first, so v2 is the registration that would silently inherit v1's schema.
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        return new TestFixture(app);
    }

    private static Type BodySchemaFor(TestFixture fx, string prefix, string entitySet, string actionName)
    {
        RouteEndpoint endpoint = FindEndpoint(fx, "POST", prefix, entitySet, actionName);
        OhDataRequestBodyMetadata? meta = endpoint.Metadata.GetMetadata<OhDataRequestBodyMetadata>();
        Assert.NotNull(meta);
        return meta!.BodyType;
    }

    private static Type UnboundBodySchemaFor(TestFixture fx, string prefix, string actionName)
    {
        RouteEndpoint endpoint = FindEndpoint(fx, "POST", prefix, entitySet: null, actionName);
        OhDataRequestBodyMetadata? meta = endpoint.Metadata.GetMetadata<OhDataRequestBodyMetadata>();
        Assert.NotNull(meta);
        return meta!.BodyType;
    }

    private static RouteEndpoint FindEndpoint(
        TestFixture fx, string httpMethod, string prefix, string? entitySet, string actionName)
    {
        EndpointDataSource source = fx.App.Services.GetRequiredService<EndpointDataSource>();
        List<RouteEndpoint> matches = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                string raw = e.RoutePattern.RawText ?? "";
                bool methodMatches = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(httpMethod) ?? false;
                bool prefixMatches = raw.StartsWith(prefix, StringComparison.Ordinal);
                bool entitySetMatches = entitySet is null || raw.Contains($"/{entitySet}", StringComparison.Ordinal);
                bool actionMatches = raw.EndsWith($"/{actionName}", StringComparison.Ordinal);
                return methodMatches && prefixMatches && entitySetMatches && actionMatches;
            })
            .ToList();

        if (matches.Count != 1)
        {
            string available = string.Join(", ", source.Endpoints.OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText));
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one {httpMethod} endpoint under '{prefix}' ending in '/{actionName}' " +
                $"(entity set: {entitySet ?? "<unbound>"}), found {matches.Count}. Available: {available}");
        }

        return matches[0];
    }

    private static void AssertPropertyNames(Type schemaType, params string[] expected)
    {
        string[] actual = schemaType.GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        string[] expectedSorted = expected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedSorted, actual);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────

internal class SchemaOrder
{
    public int Id { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>v1: single-parameter actions, matching docs/versioning.md's "same EntitySetName across
/// registrations" recommended shape.</summary>
internal class SchemaOrderProfileV1 : EntitySetProfile<int, SchemaOrder>
{
    public SchemaOrderProfileV1() : base(x => x.Id)
    {
        EntitySetName = "SchemaOrders";
        GetAll = ct => Task.FromResult<IEnumerable<SchemaOrder>>(Array.Empty<SchemaOrder>());
        GetById = (id, ct) => Task.FromResult<SchemaOrder?>(null);
        BindAction(Submit);
        BindEntityAction(Approve);
    }

    // Action: POST /SchemaOrders/Submit { "note": "..." }
    private void Submit(string note) { }

    // Entity action: POST /SchemaOrders({key})/Approve { "reason": "..." }
    private void Approve(int key, string reason) { }
}

/// <summary>v2: same entity set name and action names as v1, but the parameters evolved — the
/// archetypal reason to run two versions side by side (#499's own framing).</summary>
internal class SchemaOrderProfileV2 : EntitySetProfile<int, SchemaOrder>
{
    public SchemaOrderProfileV2() : base(x => x.Id)
    {
        EntitySetName = "SchemaOrders";
        GetAll = ct => Task.FromResult<IEnumerable<SchemaOrder>>(Array.Empty<SchemaOrder>());
        GetById = (id, ct) => Task.FromResult<SchemaOrder?>(null);
        BindAction(Submit);
        BindEntityAction(Approve);
    }

    // Action: POST /SchemaOrders/Submit { "note": "...", "priority": 0 }
    private void Submit(string note, int priority) { }

    // Entity action: POST /SchemaOrders({key})/Approve { "reason": "...", "escalate": false }
    private void Approve(int key, string reason, bool escalate) { }
}

/// <summary>Unbound action handlers — must be static (or otherwise stable) methods since
/// <c>AddAction</c> is called from the builder, not from inside a profile.</summary>
internal static class UnboundActionHandlers
{
    public static void PingV1(string message) { }
    public static void PingV2(string message, int retries) { }
}

// ── #547 fixtures: the same entity set + action names in two DEFAULT (unnamed) registrations ──

internal class ZzSchema
{
    public int Id { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>#547: host 1's shape — one body parameter per action.</summary>
internal class ZzSchemaProfileHost1 : EntitySetProfile<int, ZzSchema>
{
    public ZzSchemaProfileHost1() : base(x => x.Id)
    {
        EntitySetName = "ZZSchemas";
        GetAll = ct => Task.FromResult<IEnumerable<ZzSchema>>(Array.Empty<ZzSchema>());
        GetById = (id, ct) => Task.FromResult<ZzSchema?>(null);
        BindAction(Submit);
        BindEntityAction(Approve);
    }

    private void Submit(string note) { }
    private void Approve(int key, string reason) { }
}

/// <summary>#547: host 2's shape — the same entity set and action names, one parameter more.</summary>
internal class ZzSchemaProfileHost2 : EntitySetProfile<int, ZzSchema>
{
    public ZzSchemaProfileHost2() : base(x => x.Id)
    {
        EntitySetName = "ZZSchemas";
        GetAll = ct => Task.FromResult<IEnumerable<ZzSchema>>(Array.Empty<ZzSchema>());
        GetById = (id, ct) => Task.FromResult<ZzSchema?>(null);
        BindAction(Submit);
        BindEntityAction(Approve);
    }

    private void Submit(string note, int priority) { }
    private void Approve(int key, string reason, bool escalate) { }
}

internal static class ZzUnboundHandlers
{
    public static void SubmitHost1(string message) { }
    public static void SubmitHost2(string message, int retries) { }
}
