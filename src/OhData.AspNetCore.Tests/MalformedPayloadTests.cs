using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData.Abstractions;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Adversarial coverage for POST/PUT/PATCH bodies, Content-Type handling, key-syntax abuse on
/// keyed routes, and If-Match abuse. This package is about to be published — consumers' clients
/// will send garbage, and the framework must degrade gracefully (4xx + OData error envelope, or
/// at minimum never a 500/hang) rather than crash.
///
/// Where the framework's actual behaviour does not match the documented OData error contract
/// (<c>{"error":{"code":...,"message":...}}</c> on every 4xx, never a 500), the test is written
/// for the CORRECT behaviour and marked <c>Skip = "BUG: ..."</c>. See the PR body for the full
/// list of product bugs found by this suite.
/// </summary>
public class MalformedPayloadTests
{
    // ── POST: malformed / adversarial bodies ──────────────────────────────────────

    [Fact(Skip = "BUG: POST /Widgets with syntactically invalid JSON returns 400 with an EMPTY body " +
                 "instead of the OData error envelope {\"error\":{...}}. Root cause: POST binds the " +
                 "request body directly via a `TModel model` minimal-API parameter, so ASP.NET Core's " +
                 "built-in JSON body binder rejects the request before OhData's error-formatting code " +
                 "ever runs (contrast with PATCH, which manually deserializes and catches JsonException " +
                 "to produce a proper OData error body). See PR body 'Product bugs found'.")]
    public async Task Post_SyntacticallyInvalidJson_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var malformedJsonContent = new StringContent("{ broken json", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", malformedJsonContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.True(err.TryGetProperty("code", out _));
        Assert.True(err.TryGetProperty("message", out _));

        // Connection must remain usable after a malformed request.
        var followUp = await fx.Client.GetAsync("/odata/MalformedWidgets");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact(Skip = "BUG: POST /Widgets with an empty body returns 400 with an EMPTY body instead of the " +
                 "OData error envelope. Same root cause as the invalid-JSON case above.")]
    public async Task Post_EmptyBody_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var emptyJsonContent = new StringContent("", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", emptyJsonContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: POST /Widgets with a JSON array instead of a JSON object returns 400 with an " +
                 "EMPTY body instead of the OData error envelope. Same root cause as above.")]
    public async Task Post_JsonArrayInsteadOfObject_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: POST /Widgets with a wrong-typed field (string where int expected) returns 400 " +
                 "with an EMPTY body instead of the OData error envelope. Same root cause as above.")]
    public async Task Post_WrongTypedField_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var content = new StringContent("{\"id\":\"notanint\",\"name\":\"x\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: POST /Widgets with ~100-level deeply nested JSON returns 400 with an EMPTY body " +
                 "instead of the OData error envelope. Same root cause as above (contrast with PATCH, " +
                 "which surfaces a proper OData 'InvalidBody' error for the same input).")]
    public async Task Post_DeeplyNestedJson_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        string nested = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + string.Concat(Enumerable.Repeat("}", 100));
        using var content = new StringContent(nested, Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Post_UnknownExtraFields_AreIgnored_Returns201()
    {
        // Documented/expected behavior: unrecognized JSON properties are silently ignored by the
        // JSON deserializer rather than rejected.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var content = new StringContent("{\"name\":\"Extra\",\"totallyBogusField\":123}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("totallyBogusField", out _));
    }

    [Fact]
    public async Task Post_NullValueForNonNullableProperty_IsAccepted_Returns201()
    {
        // Current behavior: the framework does not enforce non-null / required-property validation
        // on write. Documented here so the contract is explicit rather than accidental.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        using var content = new StringContent("{\"name\":null}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_DuplicateJsonKeys_LastValueWins_Returns201()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets",
            new StringContent("{\"name\":\"first\",\"name\":\"second\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("second", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Post_VeryLargeStringValue_1MB_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        string bigName = new string('y', 1024 * 1024);
        string bodyJson = JsonSerializer.Serialize(new { name = bigName });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets",
            new StringContent(bodyJson, Encoding.UTF8, "application/json"));
        sw.Stop();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");

        // Connection must remain usable after handling a large payload.
        var followUp = await fx.Client.GetAsync("/odata/MalformedWidgets");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    // ── PUT: malformed / adversarial bodies ────────────────────────────────────────

    [Fact(Skip = "BUG: PUT /Widgets(1) with syntactically invalid JSON returns 400 with an EMPTY body " +
                 "instead of the OData error envelope. Same root cause as the POST cases above — PUT " +
                 "also binds the body via a `TModel model` minimal-API parameter.")]
    public async Task Put_SyntacticallyInvalidJson_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(1)",
            new StringContent("{ broken json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: PUT /Widgets(1) with a JSON array instead of a JSON object returns 400 with an " +
                 "EMPTY body instead of the OData error envelope. Same root cause as above.")]
    public async Task Put_JsonArrayInsteadOfObject_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(1)",
            new StringContent("[1,2,3]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: PUT /Widgets(1) with a wrong-typed field returns 400 with an EMPTY body instead " +
                 "of the OData error envelope. Same root cause as above.")]
    public async Task Put_WrongTypedField_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"id\":\"notanint\",\"name\":\"x\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Put_UnknownExtraFields_AreIgnored_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"id\":1,\"name\":\"Updated\",\"bogus\":123}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_NullValueForNonNullableProperty_IsAccepted_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"id\":1,\"name\":null}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── PATCH: malformed / adversarial bodies ──────────────────────────────────────
    // Note: unlike POST/PUT, PATCH manually deserializes the body into a JsonElement and catches
    // JsonException, so most malformed-body cases here already work correctly. We assert the
    // correct behavior directly (no Skip) except where noted.

    [Fact]
    public async Task Patch_SyntacticallyInvalidJson_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("{ broken json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("InvalidBody", err.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_EmptyBody_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact(Skip = "BUG: PATCH /Widgets(1) with a JSON array body throws an UNHANDLED " +
                 "System.InvalidOperationException ('The requested operation requires an element of " +
                 "type 'Object', but the target element has type 'Array'.') instead of returning a 400 " +
                 "OData error. Root cause: the PATCH handler's try/catch only catches JsonException and " +
                 "FormatException; a syntactically-valid-but-wrong-shaped JSON array passes JSON " +
                 "parsing and then crashes on body.EnumerateObject() (JsonElement.EnumerateObject " +
                 "throws InvalidOperationException for non-object JsonValueKind). With no exception " +
                 "handling middleware configured, this manifests as an unhandled 500 in production. " +
                 "See PR body 'Product bugs found'.")]
    public async Task Patch_JsonArrayInsteadOfObject_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("[1,2,3]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));

        // Connection must remain usable after the malformed request.
        var followUp = await fx.Client.GetAsync("/odata/MalformedWidgets");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Patch_DeeplyNestedJson_Returns400WithODataErrorBody()
    {
        // PATCH's manual JsonElement deserialization respects System.Text.Json's default max
        // depth (64) and surfaces it as a proper OData error rather than crashing.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        string nested = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + string.Concat(Enumerable.Repeat("}", 100));
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent(nested, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InvalidBody", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_UnknownExtraFields_AreIgnored_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"name\":\"x\",\"totallyBogus\":123}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Patch_DuplicateJsonKeys_LastValueWins_Returns200()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"name\":\"first\",\"name\":\"second\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("second", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Patch_VeryLargeStringValue_1MB_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        string bigName = new string('z', 1024 * 1024);
        string bodyJson = JsonSerializer.Serialize(new { name = bigName });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent(bodyJson, Encoding.UTF8, "application/json"));
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");
    }

    // ── Content-Type edge cases on writes ──────────────────────────────────────────

    [Fact(Skip = "BUG: POST /Widgets with Content-Type: text/plain returns 415 with an EMPTY body " +
                 "instead of an OData error envelope. Root cause: ASP.NET Core's minimal-API content " +
                 "negotiation for the `TModel model` parameter rejects the request before OhData's " +
                 "error-formatting code runs.")]
    public async Task Post_TextPlainContentType_Returns415WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Post_TextPlainContentType_Returns415_NeverA500()
    {
        // Non-Skip companion: regardless of body shape, confirm the status code is a well-formed
        // 4xx (415) and the server never 500s or hangs.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var followUp = await fx.Client.GetAsync("/odata/MalformedWidgets");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Post_ApplicationXmlContentType_Returns415_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PostAsync("/odata/MalformedWidgets",
            new StringContent("<widget/>", Encoding.UTF8, "application/xml"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Post_MissingContentType_Returns415_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/odata/MalformedWidgets")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"x\"}"))
        };
        // No Content-Type header set at all.
        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Post_ContentTypeWithODataMetadataParameter_IsAccepted_Returns201()
    {
        // "application/json;odata.metadata=full" — a JSON content type with extra parameters —
        // must still be recognized as JSON.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/odata/MalformedWidgets")
        {
            Content = new StringContent("{\"name\":\"x\"}", Encoding.UTF8)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json;odata.metadata=full");

        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact(Skip = "BUG: PATCH /Widgets(1) with Content-Type: text/plain returns 415 with an EMPTY body " +
                 "instead of an OData error envelope. Root cause: the route is annotated with " +
                 "`.Accepts<TModel>(\"application/json\")`, so ASP.NET Core's content-type filter " +
                 "rejects the request before the handler's manual JSON parsing (and its OData error " +
                 "formatting) ever runs.")]
    public async Task Patch_TextPlainContentType_Returns415WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var json = await response.Content.ReadFromJsonAssertingODataError();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Patch_TextPlainContentType_Returns415_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(1)",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ── Key syntax abuse on GET/PUT/PATCH/DELETE ────────────────────────────────────

    [Fact]
    public async Task GetById_NonNumericKeyForIntEntity_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/MalformedWidgets(abc)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BadRequest", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetById_EmptyParens_Returns404_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/MalformedWidgets()");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ExtraTrailingParen_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/MalformedWidgets(1))");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetById_OverflowingIntKey_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/MalformedWidgets(999999999999999999999999)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetById_QuotedKeyForIntEntity_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.GetAsync("/odata/MalformedWidgets('1')");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Put_OverflowingIntKey_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PutAsync("/odata/MalformedWidgets(999999999999999999999999)",
            new StringContent("{\"id\":1,\"name\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Patch_NonNumericKeyForIntEntity_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.PatchAsync("/odata/MalformedWidgets(abc)",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BadRequest", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_NonNumericKeyForIntEntity_Returns400WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedWidgetProfile>());
        var response = await fx.Client.DeleteAsync("/odata/MalformedWidgets(abc)");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BadRequest", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetById_UrlEncodedControlCharsInStringKey_Returns404WithODataErrorBody()
    {
        // Control characters (0x01, 0x02) percent-encoded in a string key must round-trip safely
        // through routing/key parsing without crashing — the entity simply isn't found.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedStringKeyProfile>());
        string key = Uri.EscapeDataString("");
        var response = await fx.Client.GetAsync($"/odata/MalformedStringThings('{key}')");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetById_UrlEncodedNewlineInStringKey_Returns404WithODataErrorBody()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedStringKeyProfile>());
        string key = Uri.EscapeDataString("a\nb");
        var response = await fx.Client.GetAsync($"/odata/MalformedStringThings('{key}')");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out _));
    }

    // ── If-Match abuse ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_IfMatch_GarbageFormat_Returns412_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedETagWidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/MalformedETagWidgets(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"x\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "garbage-not-an-etag");

        var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PreconditionFailed", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_IfMatch_ExtremelyLongValue_Returns412_NeverA500()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<MalformedETagWidgetProfile>());
        var request = new HttpRequestMessage(HttpMethod.Put, "/odata/MalformedETagWidgets(1)")
        {
            Content = new StringContent("{\"id\":1,\"name\":\"x\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"" + new string('e', 10_000) + "\"");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await fx.Client.SendAsync(request);
        sw.Stop();

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected a fast response, took {sw.ElapsedMilliseconds}ms");
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────

    private class MalformedWidget { public int Id { get; set; } public string Name { get; set; } = ""; }

    private class MalformedWidgetProfile : EntitySetProfile<int, MalformedWidget>
    {
        private readonly List<MalformedWidget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

        public MalformedWidgetProfile() : base(x => x.Id)
        {
            EntitySetName = "MalformedWidgets";

            GetAll = (ct) => Task.FromResult<IEnumerable<MalformedWidget>>(_store);
            GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));

            Post = (widget, ct) =>
            {
                widget.Id = _store.Count > 0 ? _store.Max(w => w.Id) + 1 : 1;
                _store.Add(widget);
                return Task.FromResult<MalformedWidget?>(widget);
            };

            Put = (id, widget, ct) =>
            {
                _store.RemoveAll(w => w.Id == id);
                widget.Id = id;
                _store.Add(widget);
                return Task.FromResult(widget);
            };

            Delete = (id, ct) => Task.FromResult(_store.RemoveAll(w => w.Id == id) > 0);

            Patch = (id, delta, ct) =>
            {
                var existing = _store.FirstOrDefault(w => w.Id == id);
                if (existing is null) return Task.FromResult<MalformedWidget?>(null);
                delta.Patch(existing);
                return Task.FromResult<MalformedWidget?>(existing);
            };
        }
    }

    private class MalformedStringThing { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }

    private class MalformedStringKeyProfile : EntitySetProfile<string, MalformedStringThing>
    {
        private static readonly List<MalformedStringThing> Store = new() { new() { Id = "alpha", Name = "A" } };

        public MalformedStringKeyProfile() : base(x => x.Id)
        {
            EntitySetName = "MalformedStringThings";
            GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(t => t.Id == id));
        }
    }

    private class MalformedETagWidgetProfile : EntitySetProfile<int, MalformedWidget>
    {
        private readonly List<MalformedWidget> _store = new() { new() { Id = 1, Name = "Sprocket" } };

        public MalformedETagWidgetProfile() : base(x => x.Id)
        {
            EntitySetName = "MalformedETagWidgets";
            GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
            Put = (id, widget, ct) =>
            {
                _store.RemoveAll(w => w.Id == id);
                widget.Id = id;
                _store.Add(widget);
                return Task.FromResult(widget);
            };
            UseETag(x => x.Name);
        }
    }
}

internal static class MalformedPayloadTestHelpers
{
    /// <summary>
    /// Reads the response body as JSON, asserting it is non-empty first with a clear failure
    /// message identifying the "empty body instead of OData error envelope" failure mode this
    /// suite is specifically watching for.
    /// </summary>
    public static async Task<JsonElement> ReadFromJsonAssertingODataError(this HttpContent content)
    {
        string body = await content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), "Expected a non-empty OData error body, but the response body was empty.");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
