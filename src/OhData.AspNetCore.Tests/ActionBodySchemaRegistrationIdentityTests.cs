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

    // ── Fixture / helpers ───────────────────────────────────────────────────────

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
