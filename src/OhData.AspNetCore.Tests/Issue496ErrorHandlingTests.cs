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
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// ── #496 fixtures ────────────────────────────────────────────────────────────────

/// <summary>#496 finding 1: a <c>Post</c> handler that returns <c>null</c>. The delegate's declared
/// return type is <c>Task&lt;TModel?&gt;</c>, so this is a compile-legal handler — the framework has
/// to decide what it means, and "the client sent a bad request" is not it.</summary>
internal sealed class NullPostProfile : EntitySetProfile<int, Widget>
{
    public NullPostProfile() : base(x => x.Id)
    {
        EntitySetName = "NullPostWidgets";
        GetById = (id, ct) => Task.FromResult<Widget?>(null);
        Post = (widget, ct) => Task.FromResult<Widget?>(null);
    }
}

/// <summary>#496 finding 4: a read handler that throws <see cref="Microsoft.OData.ODataException"/>
/// — the shape a handler proxying a downstream OData service takes when that service faults.
/// Nothing about the request is malformed.</summary>
internal sealed class HandlerODataFaultProfile : EntitySetProfile<int, Widget>
{
    internal const string Marker = "downstream odata service at sql://internal-host/db said no";

    public HandlerODataFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "HandlerODataFaultWidgets";
        GetAll = ct => throw new Microsoft.OData.ODataException(Marker);
        GetById = (id, ct) => throw new Microsoft.OData.ODataException(Marker);
    }
}

/// <summary>#496 finding 4 (second half): a keyed-route handler that throws
/// <see cref="FormatException"/> from its own body. The key in the URL parses fine.</summary>
internal sealed class HandlerFormatFaultProfile : EntitySetProfile<int, Widget>
{
    internal const string Marker = "downstream csv column 7 at /var/data/secret.csv is not a number";

    public HandlerFormatFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "HandlerFormatFaultWidgets";
        GetById = (id, ct) => throw new FormatException(Marker);
        GetAll = ct => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
    }
}

internal sealed class NavFaultOrder
{
    public int Id { get; set; }
    public IEnumerable<NavFaultLine>? Lines { get; set; }
}

internal sealed class NavFaultLine
{
    public int Id { get; set; }
}

/// <summary>#496 finding 4: the read routes' whole-body <c>try</c> also encloses
/// <c>ApplyCollectionPipelineAsync</c>, which invokes the profile's own <c>$expand</c> delegates
/// (#358's <c>NavFault</c> fixture proves that reach). A navigation delegate throwing
/// <see cref="Microsoft.OData.ODataException"/> was therefore answered with the same message-leaking
/// <c>400</c> as a root handler's.</summary>
internal sealed class NavDelegateODataFaultProfile : EntitySetProfile<int, NavFaultOrder>
{
    internal const string Marker = "expand delegate: upstream odata feed /internal/lines is down";

    private static readonly List<NavFaultOrder> _orders = new() { new() { Id = 1 } };

    public NavDelegateODataFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "NavFaultOrders";
        ExpandEnabled = true;

        GetAll = ct => Task.FromResult<IEnumerable<NavFaultOrder>>(_orders);
        GetById = (id, ct) => Task.FromResult(_orders.FirstOrDefault(o => o.Id == id));

        HasMany(x => x.Lines!, getAll: (orderId, ct) =>
            throw new Microsoft.OData.ODataException(Marker));
    }
}

/// <summary>
/// #496 — four independent error-handling findings from the round-2 adversarial review.
///
/// <para><b>1.</b> <c>Post</c> returning <c>null</c> answered <c>400 BadRequest</c> with
/// <c>"Post handler returned null."</c> — a server-side contract violation blamed on the client,
/// with the server's own handler named back to it. Every other null policy in the framework is a
/// 200/404/204.</para>
///
/// <para><b>2.</b> The #203 <c>Content-Length</c> fast-reject <c>413</c> short-circuits from a
/// filter registered OUTSIDE the one that sets <c>OData-Version</c>, so that response shipped
/// without the header §8.1.5 requires on every response.</para>
///
/// <para><b>3.</b> The group exception filter's comment claimed it was "the outermost group filter
/// (added first)". It is neither — the #200 observability filter is added first and wraps it. That
/// is a documentation defect; the only assertable consequence is the ordering itself, which
/// <see cref="BodyLimit_FastReject413_CarriesTheODataVersionHeader"/> now pins from the outside:
/// the header must be set by a filter outside every filter that can short-circuit.</para>
///
/// <para><b>4.</b> The read routes' whole-body <c>try</c> encloses handler invocation, so a
/// <c>Microsoft.OData.ODataException</c> from USER code became a <c>400</c> with
/// <c>ex.Message</c> passed verbatim to the client — a targeted bypass of the no-disclosure rule —
/// and a handler-origin <c>FormatException</c> on any keyed route was relabelled
/// <c>400 "Invalid key format for …"</c> even though the key parsed fine.</para>
///
/// <para>Each fix has a paired control asserting the framework's OWN 400s are byte-identical: the
/// point is to stop claiming client fault for server faults, not to stop reporting client faults.</para>
/// </summary>
public class Issue496ErrorHandlingTests
{
    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static async Task<(TestFixture Fixture, CapturingLoggerProvider Logs)> BuildAsync(
        Action<OhDataBuilder> configure)
    {
        var logs = new CapturingLoggerProvider();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            configure, configureServices: s => s.AddLogging(b => b.AddProvider(logs)));
        return (fx, logs);
    }

    // ── Finding 1: Post handler returning null ──────────────────────────────────

    [Fact]
    public async Task PostHandlerReturningNull_Is500_NotA400ThatNamesTheHandler()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<NullPostProfile>());
        await using TestFixture _ = fx;

        HttpResponseMessage response = await fx.Client.PostAsync(
            "/odata/NullPostWidgets", Json("{\"Name\":\"ok\"}"));
        string body = await response.Content.ReadAsStringAsync();

        // Pre-fix: 400 {"error":{"code":"BadRequest","message":"Post handler returned null."}}
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("InternalServerError", json.GetProperty("error").GetProperty("code").GetString());

        // The client is never told about the server's own handler.
        Assert.DoesNotContain("handler", body, StringComparison.OrdinalIgnoreCase);

        // The operator is.
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    // ── Finding 2: the fast-reject 413 and the OData-Version header ──────────────

    [Fact]
    public async Task BodyLimit_FastReject413_CarriesTheODataVersionHeader()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BodyLimitProfile>());

        // ~400 bytes against BodyLimitProfile's MaxRequestBodyBytes = 200. Content-Length is set by
        // StringContent, so this takes the fast-reject path, not the Kestrel streaming path.
        HttpResponseMessage response = await fx.Client.PostAsync(
            "/odata/BodyLimitWidgets", Json("{\"Name\":\"" + new string('x', 380) + "\"}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        // Pre-fix: the header is absent — the body-limit filter short-circuits from OUTSIDE the
        // filter that sets it. §8.1.5 requires it on every response.
        Assert.True(response.Headers.TryGetValues("OData-Version", out IEnumerable<string>? values),
            "413 fast-reject response carried no OData-Version header");
        Assert.Equal("4.0", Assert.Single(values!));
    }

    [Fact]
    public async Task ControlsThatAlreadyCarriedTheHeader_StillDo()
    {
        var (fx, _) = await BuildAsync(o =>
        {
            o.AddEntitySetProfile<WidgetProfile>();
            o.AddEntitySetProfile<HandlerODataFaultProfile>();
        });
        await using TestFixture _fx = fx;

        HttpResponseMessage ok = await fx.Client.GetAsync("/odata/Widgets");
        Assert.Equal("4.0", Assert.Single(ok.Headers.GetValues("OData-Version")));

        HttpResponseMessage notFound = await fx.Client.GetAsync("/odata/Widgets(9999)");
        Assert.Equal("4.0", Assert.Single(notFound.Headers.GetValues("OData-Version")));

        HttpResponseMessage serverError = await fx.Client.GetAsync("/odata/HandlerODataFaultWidgets");
        Assert.Equal("4.0", Assert.Single(serverError.Headers.GetValues("OData-Version")));
    }

    // ── Finding 4a: a handler-thrown ODataException ─────────────────────────────

    [Fact]
    public async Task HandlerThrownODataException_Is500WithoutItsMessage()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<HandlerODataFaultProfile>());
        await using TestFixture _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/HandlerODataFaultWidgets");
        string body = await response.Content.ReadAsStringAsync();

        // Pre-fix: 400 InvalidQueryOption with the handler's message verbatim in the body.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(HandlerODataFaultProfile.Marker, body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception is not null &&
            (e.Exception.Message.Contains(HandlerODataFaultProfile.Marker, StringComparison.Ordinal) ||
             e.Exception.InnerException?.Message.Contains(HandlerODataFaultProfile.Marker, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task HandlerThrownODataException_OnGetById_Is500WithoutItsMessage()
    {
        var (fx, _) = await BuildAsync(o => o.AddEntitySetProfile<HandlerODataFaultProfile>());
        await using TestFixture _fx = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/HandlerODataFaultWidgets(1)");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(HandlerODataFaultProfile.Marker, body, StringComparison.Ordinal);
    }

    /// <summary>The control for finding 4a. A FRAMEWORK-thrown <c>ODataException</c> — from the
    /// model-bound allowlist validation that runs on the same routes, inside the same try — must
    /// keep its 400 AND its message, byte for byte. These bytes were captured from the pre-fix
    /// build.</summary>
    [Fact]
    public async Task FrameworkThrownODataException_KeepsItsByteIdentical400()
    {
        var (fx, _) = await BuildAsync(o => o.AddEntitySetProfile<NameFilterOnlyProfile>());
        await using TestFixture _fx = fx;

        // NameFilterOnlyProfile allowlists $filter to Name, so filtering on Id trips the EDM's
        // model-bound NotFilterable annotation inside ValidatePropertyAllowlists -- a framework
        // ODataException raised inside the very try this issue narrows.
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NameFilterWidgets?$filter=Id eq 1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"InvalidQueryOption\",\"message\":\"The property 'Id' cannot be used in the $filter query option.\"}}",
            await response.Content.ReadAsStringAsync());
    }

    // ── Finding 4b: a handler-thrown FormatException on a well-formed key ────────

    [Fact]
    public async Task HandlerThrownFormatException_OnAWellFormedKey_IsNotRelabelledABadKey()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<HandlerFormatFaultProfile>());
        await using TestFixture _ = fx;

        // "1" parses cleanly as the int key. The FormatException comes from inside the handler.
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/HandlerFormatFaultWidgets(1)");
        string body = await response.Content.ReadAsStringAsync();

        // Pre-fix: 400 {"error":{"code":"BadRequest","message":"Invalid key format for
        // HandlerFormatFaultWidgets: '1'","target":"key"}} — a client-blamed 4xx for a server fault.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Invalid key format", body, StringComparison.Ordinal);
        Assert.DoesNotContain(HandlerFormatFaultProfile.Marker, body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    [Fact]
    public async Task NavigationDelegateThrownODataException_Is500WithoutItsMessage()
    {
        var (fx, logs) = await BuildAsync(o => o.AddEntitySetProfile<NavDelegateODataFaultProfile>());
        await using TestFixture _ = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/NavFaultOrders?$expand=Lines");
        string body = await response.Content.ReadAsStringAsync();

        // Pre-fix: 400 InvalidQueryOption with the delegate's message verbatim in the body.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(NavDelegateODataFaultProfile.Marker, body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception is not null);
    }

    /// <summary>The control for finding 4b: a genuinely unparseable key still gets the same 400,
    /// byte for byte. Captured from the pre-fix build.</summary>
    [Fact]
    public async Task MalformedKey_KeepsItsByteIdentical400()
    {
        var (fx, _) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using TestFixture _fx = fx;

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/Widgets(notanint)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid key format for Widgets: 'notanint'\",\"target\":\"key\"}}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MalformedKey_OnAWriteRoute_KeepsItsByteIdentical400()
    {
        var (fx, _) = await BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());
        await using TestFixture _fx = fx;

        HttpResponseMessage response = await fx.Client.PatchAsync(
            "/odata/Widgets(notanint)", Json("{\"Name\":\"x\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid key format for Widgets: 'notanint'\",\"target\":\"key\"}}",
            await response.Content.ReadAsStringAsync());
    }
}
