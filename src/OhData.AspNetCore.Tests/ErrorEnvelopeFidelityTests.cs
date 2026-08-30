using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>A profile whose <c>GetAll</c> throws a <see cref="TaskCanceledException"/> on a request
/// that was never aborted — the shape an <c>HttpClient</c> timeout to a downstream dependency takes
/// inside a handler. Uses the shared <see cref="Widget"/> model.</summary>
internal sealed class DependencyTimeoutProfile : EntitySetProfile<int, Widget>
{
    internal const string Marker = "simulated HttpClient timeout on an unrelated token";

    public DependencyTimeoutProfile() : base(x => x.Id)
    {
        EntitySetName = "TimeoutWidgets";
        GetAll = _ => throw new TaskCanceledException(Marker);
    }
}

/// <summary>Host-registered converters. A host may legitimately register either; both are supported
/// configuration, not contrivances.</summary>
internal sealed class ThrowingStringConverter : JsonConverter<string>
{
    internal const string Marker = "host string converter fault (test)";
    public override string Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.GetString()!;
    public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o)
        => throw new InvalidOperationException(Marker);
}

internal sealed class ThrowingInt32Converter : JsonConverter<int>
{
    internal const string Marker = "host int converter fault (test)";
    public override int Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.GetInt32();
    public override void Write(Utf8JsonWriter w, int v, JsonSerializerOptions o)
        => throw new InvalidOperationException(Marker);
}

/// <summary>
/// #493 and #495 — two ways the ERROR path was less trustworthy than the success path.
///
/// <para>#493: the group-level exception filter declined to catch the whole
/// <see cref="OperationCanceledException"/> family on the theory that a cancellation means the
/// client went away. That is a fact about the REQUEST, not about the exception type, and
/// <see cref="TaskCanceledException"/> is what <c>HttpClient</c> throws on its own timeout — a
/// server-side dependency fault. Measured on the pre-fix tree: HTTP 500 with an empty body, no
/// envelope, and no OhData log at all. The filter now consults
/// <c>HttpContext.RequestAborted</c> as well; <see cref="CancellationTests"/> continues to pin the
/// genuine-abort behaviour it must not have changed.</para>
///
/// <para>#495: every OData envelope built as a <c>Dictionary&lt;string, ...&gt;</c> was serialized
/// at <c>IResult</c>-execute time under the HOST's <c>HttpJsonOptions</c> — outside the filter (the
/// #396 hazard, on the paths #396 did not convert) and outside OhData's own options. Two distinct
/// consequences, both measured: a host <c>DictionaryKeyPolicy</c> reshaped the contractual keys of
/// every error response AND every collection response, and a throwing host converter took the
/// envelope with it, including the filter's own 500 envelope.</para>
///
/// <para>The byte-identity assertions below were captured from the PRE-fix tree and pasted
/// verbatim. They are the property that matters most here: a default-configured host must not see
/// one byte change.</para>
/// </summary>
public class ErrorEnvelopeFidelityTests
{
    private static Task<TestFixture> BuildAsync(
        CapturingLoggerProvider? logs = null, Action<JsonSerializerOptions>? hostJson = null)
        => TestHostBuilder.BuildAsync(
            o =>
            {
                o.AddEntitySetProfile<WidgetProfile>();
                o.AddEntitySetProfile<BoundOpsProfile>();
                o.AddEntitySetProfile<DependencyTimeoutProfile>();
            },
            configureServices: s =>
            {
                s.AddSingleton(new BoundOpsStore());
                if (logs is not null) s.AddLogging(b => b.AddProvider(logs));
                if (hostJson is not null) s.ConfigureHttpJsonOptions(j => hostJson(j.SerializerOptions));
            });

    // ── #493 ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandlerTaskCanceledException_OnALiveRequest_Gets500Envelope_AndIsLogged()
    {
        var logs = new CapturingLoggerProvider();
        await using TestFixture fx = await BuildAsync(logs);

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/TimeoutWidgets");

        // Pre-fix: 500 with a completely empty body and nothing logged by OhData.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal("InternalServerError", json.GetProperty("error").GetProperty("code").GetString());

        // S7 still holds: the handler's own message never reaches the client...
        Assert.DoesNotContain(DependencyTimeoutProfile.Marker, body, StringComparison.Ordinal);

        // ...but the operator gets the real exception.
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception is TaskCanceledException &&
            e.Exception.Message.Contains(DependencyTimeoutProfile.Marker, StringComparison.Ordinal));
    }

    // ── #495: byte-identity on a default host ──────────────────────────────────────

    public static TheoryData<string, HttpStatusCode, string> DefaultHostBodies() => new()
    {
        {
            "/odata/Widgets(99)", HttpStatusCode.NotFound,
            "{\"error\":{\"code\":\"NotFound\",\"message\":\"Widgets with key '99' was not found.\"}}"
        },
        {
            // Carries the optional `target` member, so the three-member error object is covered too.
            "/odata/Widgets(abc)", HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid key format for Widgets: 'abc'\",\"target\":\"key\"}}"
        },
        {
            // 501 since the §9.3.1 taxonomy landed. The BYTES are unchanged, which is the point:
            // only the status line moved, so a client matching on the envelope keeps working.
            "/odata/Widgets?$orderby=Name", HttpStatusCode.NotImplemented,
            "{\"error\":{\"code\":\"UnsupportedQueryOption\",\"message\":\"This resource does not support $filter or $orderby. Configure GetQueryable to enable server-side query processing.\"}}"
        },
        {
            // Service document.
            "/odata", HttpStatusCode.OK,
            "{\"@odata.context\":\"http://localhost/odata/$metadata\",\"value\":[{\"name\":\"Widgets\",\"kind\":\"EntitySet\",\"url\":\"Widgets\"},{\"name\":\"BoundWidgets\",\"kind\":\"EntitySet\",\"url\":\"BoundWidgets\"},{\"name\":\"TimeoutWidgets\",\"kind\":\"EntitySet\",\"url\":\"TimeoutWidgets\"}]}"
        },
        {
            // WrapBoundOpResult's collection branch.
            "/odata/BoundWidgets/GetByName?name=Alpha", HttpStatusCode.OK,
            "{\"@odata.context\":\"http://localhost/odata/$metadata#BoundWidgets\",\"value\":[{\"Id\":1,\"Name\":\"Alpha\"}]}"
        },
        {
            // WrapBoundOpResult's Edm-primitive branch.
            "/odata/BoundWidgets/DoubleCount?factor=2", HttpStatusCode.OK,
            "{\"@odata.context\":\"http://localhost/odata/$metadata#Edm.Int32\",\"value\":4}"
        },
        {
            // The ordinary collection envelope — not named in #495, but the same dictionary.
            "/odata/Widgets", HttpStatusCode.OK,
            "{\"@odata.context\":\"http://localhost/odata/$metadata#Widgets\",\"value\":[{\"Id\":1,\"Name\":\"Sprocket\"},{\"Id\":2,\"Name\":\"Cog\"}]}"
        },
    };

    [Theory]
    [MemberData(nameof(DefaultHostBodies))]
    public async Task DefaultHost_BodyAndHeadersAreByteIdenticalToTheDeferredPath(
        string url, HttpStatusCode status, string expected)
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage response = await fx.Client.GetAsync(url);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(expected, body);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>The pre-rendered envelopes write <c>Content-Length</c> explicitly, so it has to be
    /// the real UTF-8 byte count — not the character count, which differs the moment a message
    /// quotes a non-ASCII key.</summary>
    [Fact]
    public async Task PreRenderedErrorEnvelope_ContentLengthIsTheRealUtf8ByteCount()
    {
        await using TestFixture fx = await BuildAsync();

        // A non-ASCII key: unparseable as an int, so it comes back in the 400's message verbatim
        // (the host's default encoder does not escape it), making the byte count exceed the
        // character count.
        HttpResponseMessage response = await fx.Client.GetAsync("/odata/Widgets(caf%C3%A9)");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.True(bytes.Length > System.Text.Encoding.UTF8.GetString(bytes).Length,
            "the fixture is only meaningful if the payload is multi-byte");
    }

    // ── #495: a host key policy must not reshape anything ──────────────────────────

    [Theory]
    [InlineData("/odata")]
    [InlineData("/odata/Widgets")]
    [InlineData("/odata/Widgets(99)")]
    [InlineData("/odata/Widgets(abc)")]
    [InlineData("/odata/Widgets?$orderby=Name")]
    [InlineData("/odata/BoundWidgets/GetByName?name=Alpha")]
    [InlineData("/odata/BoundWidgets/DoubleCount?factor=2")]
    public async Task HostDictionaryKeyPolicy_DoesNotReshapeAnyEnvelope(string url)
    {
        // Pre-fix, with SnakeCaseUpper: {"ERROR":{"CODE":...}} on every error response and
        // {"@ODATA.CONTEXT":...,"VALUE":[...]} on every collection and bound-operation response.
        await using TestFixture withPolicy = await BuildAsync(
            hostJson: o => o.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper);
        await using TestFixture control = await BuildAsync();

        HttpResponseMessage a = await withPolicy.Client.GetAsync(url);
        HttpResponseMessage b = await control.Client.GetAsync(url);

        Assert.Equal(b.StatusCode, a.StatusCode);
        Assert.Equal(await b.Content.ReadAsStringAsync(), await a.Content.ReadAsStringAsync());
    }

    // ── #495: a throwing host converter must not take the envelope with it ─────────

    /// <summary>
    /// The decisive probe. A host <c>JsonConverter&lt;string&gt;</c> that throws makes EVERY
    /// response body that contains a string unserializable — including the group filter's own 500
    /// envelope, which was written after the filter chain unwound. Pre-fix the client got an empty,
    /// envelope-less 500 with no OhData log; the framework's last-resort error path was itself the
    /// thing that failed.
    /// </summary>
    [Theory]
    [InlineData("/odata/Widgets")]
    [InlineData("/odata/Widgets(1)")]
    [InlineData("/odata/BoundWidgets/GetByName?name=Alpha")]
    public async Task ThrowingHostStringConverter_StillYieldsTheErrorEnvelope(string url)
    {
        var logs = new CapturingLoggerProvider();
        await using TestFixture fx = await BuildAsync(
            logs, o => o.Converters.Add(new ThrowingStringConverter()));

        HttpResponseMessage response = await fx.Client.GetAsync(url);
        await AssertLoggedInternalServerError(response, logs, ThrowingStringConverter.Marker);
    }

    /// <summary>
    /// #396 listed the Edm-primitive branch of <c>WrapBoundOpResult</c> as a knowing residual: "only
    /// a host-registered converter for a primitive type could fault there". It could, and did.
    /// </summary>
    [Fact]
    public async Task ThrowingHostPrimitiveConverter_OnTheBoundOperationEnvelope_StillYieldsTheErrorEnvelope()
    {
        var logs = new CapturingLoggerProvider();
        await using TestFixture fx = await BuildAsync(
            logs, o => o.Converters.Add(new ThrowingInt32Converter()));

        HttpResponseMessage response = await fx.Client.GetAsync("/odata/BoundWidgets/DoubleCount?factor=2");
        await AssertLoggedInternalServerError(response, logs, ThrowingInt32Converter.Marker);
    }

    /// <summary>The error envelope and the service document carry no user model data at all, so a
    /// host converter has no business reaching them — and, critically, cannot break them.</summary>
    [Theory]
    [InlineData("/odata")]
    [InlineData("/odata/Widgets(99)")]
    [InlineData("/odata/Widgets(abc)")]
    public async Task ThrowingHostStringConverter_DoesNotReachTheFrameworkOwnedEnvelopes(string url)
    {
        await using TestFixture withConverter = await BuildAsync(
            hostJson: o => o.Converters.Add(new ThrowingStringConverter()));
        await using TestFixture control = await BuildAsync();

        HttpResponseMessage a = await withConverter.Client.GetAsync(url);
        HttpResponseMessage b = await control.Client.GetAsync(url);

        Assert.Equal(b.StatusCode, a.StatusCode);
        Assert.Equal(await b.Content.ReadAsStringAsync(), await a.Content.ReadAsStringAsync());
    }

    private static async Task AssertLoggedInternalServerError(
        HttpResponseMessage response, CapturingLoggerProvider logs, string faultMarker)
    {
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // A complete envelope and nothing else — a body truncated mid-write would not parse.
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.Single(json.EnumerateObject());
        Assert.Equal("InternalServerError", json.GetProperty("error").GetProperty("code").GetString());

        Assert.DoesNotContain(faultMarker, body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception is not null &&
            e.Exception.ToString().Contains(faultMarker, StringComparison.Ordinal));
    }
}
