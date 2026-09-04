using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>A DTO that serializes the first member fine and then throws. The two halves are the
/// point: a deferred serializer writes <c>Ok</c> to the wire (after the 200 status line) and only
/// then faults, which is what produced the observed "200 + truncated body".</summary>
internal sealed class OpFaultBox
{
    internal const string PartialMarker = "written-before-the-fault";
    internal const string FaultMarker = "operation result serialization fault (test)";

    public string Ok { get; set; } = PartialMarker;
    public string Boom => throw new InvalidOperationException(FaultMarker);
}

internal sealed class OpHealthyBox
{
    public int Value { get; set; }
    public string? Label { get; set; }
}

internal sealed class OpFaultWidget
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public OpFaultBox Box { get; set; } = new();
}

internal sealed class OpHealthyWidget
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class OpFaultProfile : EntitySetProfile<int, OpFaultWidget>
{
    public OpFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "OpFaults";
        GetById = (id, ct) => OhDataResult.Success<OpFaultWidget?>(
            new OpFaultWidget { Id = id, Name = "n" });

        BindFunction(FaultyFunction);
        BindAction(FaultyAction);
        BindEntityFunction(FaultyEntityFunction);
        BindEntityAction(FaultyEntityAction);
    }

    // Returns a non-TModel DTO, so it lands on WrapBoundOpResult's final branch — the one that
    // hands a raw CLR graph to the serializer.
    private Task<OpFaultBox> FaultyFunction() => Task.FromResult(new OpFaultBox());
    private Task<OpFaultBox> FaultyAction() => Task.FromResult(new OpFaultBox());
    private Task<OpFaultBox> FaultyEntityFunction(int key) => Task.FromResult(new OpFaultBox());
    private Task<OpFaultBox> FaultyEntityAction(int key) => Task.FromResult(new OpFaultBox());
}

internal sealed class OpHealthyProfile : EntitySetProfile<int, OpHealthyWidget>
{
    public OpHealthyProfile() : base(x => x.Id)
    {
        EntitySetName = "OpHealthy";
        GetAll = ct => OhDataResult.Success<IEnumerable<OpHealthyWidget>>(
            new[] { new OpHealthyWidget { Id = 1, Name = "one" } });
        GetById = (id, ct) => OhDataResult.Success<OpHealthyWidget?>(
            new OpHealthyWidget { Id = id, Name = "one" });

        BindFunction(HealthyFunction);
        BindAction(HealthyAction);
    }

    private Task<OpHealthyBox> HealthyFunction() =>
        Task.FromResult(new OpHealthyBox { Value = 42, Label = "ok" });

    private Task<OpHealthyBox> HealthyAction() =>
        Task.FromResult(new OpHealthyBox { Value = 42, Label = "ok" });
}

/// <summary>
/// #396 — bound/unbound operation routes bypassed the OData error envelope entirely.
///
/// <para>A minimal-API <c>IResult</c> is executed by <c>RequestDelegateFactory</c> AFTER the
/// endpoint-filter pipeline has unwound, so a serialization fault inside <c>Results.Json</c> is
/// outside the group-level exception filter's <c>try</c> — and the 200 status line and headers are
/// already on the wire by the time the serializer reaches the faulting member. Measured on the
/// pre-fix tree: <c>Request finished ... - 200</c> and a truncated body. That is worse than the
/// envelope-less 500 the filter was written to eliminate; a success status with a malformed body
/// defeats client-side error handling completely.</para>
///
/// <para>The fix serializes inside the filter's scope (see <c>PreRenderedJson</c>), so the fault
/// happens where the filter can still convert it and before anything is committed. The entity
/// routes were already correct for the identical condition — they build a JsonNode in the handler —
/// and <see cref="EntityRoute_SerializationFault_WasAlreadyCorrect"/> pins that rather than
/// assuming it.</para>
/// </summary>
public class OperationResultSerializationFaultTests
{
    private static async Task<(TestFixture Fixture, CapturingLoggerProvider Logs)> BuildAsync()
    {
        var logs = new CapturingLoggerProvider();
        var fx = await TestHostBuilder.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<OpFaultProfile>();
                o.AddEntitySetProfile<OpHealthyProfile>();
                o.AddFunction(FaultyUnboundFunction, name: "FaultyUnboundFunction");
                o.AddAction(FaultyUnboundAction, name: "FaultyUnboundAction");
                o.AddFunction(HealthyUnboundFunction, name: "HealthyUnboundFunction");
                o.AddAction(HealthyUnboundAction, name: "HealthyUnboundAction");
            },
            configureServices: services => services.AddLogging(b => b.AddProvider(logs)));
        return (fx, logs);
    }

    private static Task<OpFaultBox> FaultyUnboundFunction() => Task.FromResult(new OpFaultBox());
    private static Task<OpFaultBox> FaultyUnboundAction() => Task.FromResult(new OpFaultBox());
    private static Task<OpHealthyBox> HealthyUnboundFunction() =>
        Task.FromResult(new OpHealthyBox { Value = 42, Label = "ok" });
    private static Task<OpHealthyBox> HealthyUnboundAction() =>
        Task.FromResult(new OpHealthyBox { Value = 42, Label = "ok" });

    // ── The defect: every operation route that hands a raw CLR graph to the serializer ──

    public static TheoryData<string> FaultingGetRoutes() => new()
    {
        "/odata/OpFaults/FaultyFunction",         // collection-level bound function
        "/odata/OpFaults(1)/FaultyEntityFunction", // entity-level bound function
        "/odata/FaultyUnboundFunction",            // unbound function
        "/odata/OpFaults(1)/Box",                  // structural-property read (raw complex value)
    };

    public static TheoryData<string> FaultingPostRoutes() => new()
    {
        "/odata/OpFaults/FaultyAction",          // collection-level bound action
        "/odata/OpFaults(1)/FaultyEntityAction", // entity-level bound action
        "/odata/FaultyUnboundAction",            // unbound action
    };

    [Theory]
    [MemberData(nameof(FaultingGetRoutes))]
    public async Task FaultingGetOperation_Returns500EnvelopeWithNoPartialBody(string url)
    {
        var (fx, logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync(url);
        await AssertEnvelopedFault(response, logs);
    }

    [Theory]
    [MemberData(nameof(FaultingPostRoutes))]
    public async Task FaultingPostOperation_Returns500EnvelopeWithNoPartialBody(string url)
    {
        var (fx, logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.PostAsync(url, content: null);
        await AssertEnvelopedFault(response, logs);
    }

    private static async Task AssertEnvelopedFault(
        HttpResponseMessage response, CapturingLoggerProvider logs)
    {
        string body = await response.Content.ReadAsStringAsync();

        // 1. The status is 500, not the pre-fix 200.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 2. NOTHING of the half-serialized result preceded the envelope. Pre-fix the body began
        //    with the fragment {"Ok":"written-before-the-fault" and then simply stopped.
        Assert.DoesNotContain(OpFaultBox.PartialMarker, body, StringComparison.Ordinal);

        // 3. The body is the complete OData error envelope and nothing else — a truncated payload
        //    would not round-trip through the parser at all, and a concatenated one would not have
        //    "error" as its single member.
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.Single(json.EnumerateObject());
        JsonElement error = json.GetProperty("error");
        Assert.Equal("InternalServerError", error.GetProperty("code").GetString());

        // 4. The generic message is all the client gets: the real fault must not leak.
        Assert.DoesNotContain(OpFaultBox.FaultMarker, body, StringComparison.Ordinal);

        // 5. ...but the operator does get it, via the group filter's own LogError.
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception is not null &&
            e.Exception.ToString().Contains(OpFaultBox.FaultMarker, StringComparison.Ordinal));
    }

    /// <summary>The entity routes were BELIEVED correct for the identical condition because they
    /// serialize to a JsonNode inside the handler. Verified, not assumed.</summary>
    [Fact]
    public async Task EntityRoute_SerializationFault_WasAlreadyCorrect()
    {
        var (fx, logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/OpFaults(1)");
        await AssertEnvelopedFault(response, logs);
    }

    // ── The non-faulting case is untouched ──────────────────────────────────────────

    /// <summary>Serializing eagerly must not change one byte of a successful response. The expected
    /// strings below were captured from the pre-fix tree.</summary>
    [Theory]
    [InlineData("/odata/OpHealthy/HealthyFunction")]
    [InlineData("/odata/HealthyUnboundFunction")]
    public async Task HealthyFunction_BodyIsByteIdenticalToDeferredSerialization(string url)
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("{\"Value\":42,\"Label\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/odata/OpHealthy/HealthyAction")]
    [InlineData("/odata/HealthyUnboundAction")]
    public async Task HealthyAction_BodyIsByteIdenticalToDeferredSerialization(string url)
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.PostAsync(url, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"Value\":42,\"Label\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Pre-rendering happens inside the handler, so it sits UNDER the group filters that
    /// negotiate content and stamp the version header — those still run, and still short-circuit
    /// before the operation is ever invoked when they reject the request.</summary>
    [Fact]
    public async Task PreRenderedResult_StillCarriesODataVersionHeader()
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/OpHealthy/HealthyFunction");

        Assert.Equal("4.0", Assert.Single(response.Headers.GetValues("OData-Version")));
    }

    [Fact]
    public async Task PreRenderedResult_StillHonorsFormatNegotiation()
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        HttpResponseMessage rejected = await fx.Client.GetAsync(
            "/odata/OpHealthy/HealthyFunction?$format=xml");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        HttpResponseMessage accepted = await fx.Client.GetAsync(
            "/odata/OpHealthy/HealthyFunction?$format=json");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("{\"Value\":42,\"Label\":\"ok\"}", await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PreRenderedResult_StillHonorsAcceptNegotiation()
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/OpHealthy/HealthyFunction");
        request.Headers.Add("Accept", "application/xml");
        HttpResponseMessage response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task PreRenderedResult_StillHonorsODataMaxVersionValidation()
    {
        var (fx, _logs) = await BuildAsync();
        await using var _ = fx;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/odata/OpHealthy/HealthyFunction");
        request.Headers.Add("OData-MaxVersion", "3.0");
        HttpResponseMessage response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
