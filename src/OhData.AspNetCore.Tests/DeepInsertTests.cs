using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Tests for deep insert — nested related entities in <c>POST /{EntitySet}</c>
/// (OData §11.4.2.2). Rides the existing <c>Post</c> handler; no new handler delegate. Gated by
/// the new <c>AllowDeepWrites</c> profile flag (default <c>false</c>, entity-level granularity).
/// <para>
/// Default (<c>false</c>): System.Text.Json already binds nested navigation values into the
/// deserialized model during the existing POST pipeline; the framework strips them (sets them to
/// <c>null</c>) before <c>Post</c> is invoked, so a handler that doesn't expect a graph never
/// silently persists only part of one.
/// </para>
/// <para>
/// Opt-in (<c>true</c>): the full deserialized graph is passed to <c>Post</c> as-is. The handler
/// owns atomic persistence of the whole graph.
/// </para>
/// <para>
/// <c>@odata.bind</c> (linking to an existing entity, JSON format §8.5) is documented
/// non-support: a request body containing the annotation anywhere is rejected with
/// <c>501 Not Implemented</c> rather than silently ignored.
/// </para>
/// </summary>
public class DeepInsertTests
{
    // ── Default (AllowDeepWrites = false): nested navigation values are stripped ────

    [Fact]
    public async Task Post_Default_StripsNestedCollectionNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Alice",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Instrumented via a capturing fixture: assert the handler itself received a stripped
        // graph, not merely that the response happens to omit it. The framework nulls the
        // navigation property out entirely (rather than substituting an empty collection).
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByHandler!.Lines);

        // #240: the POST echo omits the un-expanded navigation entirely (matching a read of the
        // same type), rather than leaking it as an explicit null.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("Lines", out _));
    }

    [Fact]
    public async Task Post_Default_StripsNestedSingleValuedNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Bob",
            category = new { name = "Hardware" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByHandler!.Category);

        // #240: the stripped single-valued navigation is omitted from the echo, not echoed as null.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.TryGetProperty("Category", out _));
    }

    [Fact]
    public async Task Post_Default_NonNavigationCollectionProperty_Survives()
    {
        // Only CLR properties declared as navigations via HasMany/HasOptional/HasRequired are
        // stripped. A plain (non-nav) collection property is left untouched even when the
        // profile has not opted into deep insert.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Carol",
            tags = new[] { "rush", "gift-wrap" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByHandler);
        Assert.Equal(new[] { "rush", "gift-wrap" }, DeepInsertDefaultProfile.LastReceivedByHandler!.Tags);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("Tags").GetArrayLength());
    }

    // ── Opt-in (AllowDeepWrites = true): full graph passed through, echoed in response ──

    [Fact]
    public async Task Post_OptIn_PassesFullGraphToHandler_AndEchoesChildrenInResponse()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertOptInOrders", new
        {
            customer = "Dave",
            lines = new[]
            {
                new { sku = "WIDGET-1", quantity = 2 },
                new { sku = "GADGET-9", quantity = 1 },
            },
            category = new { name = "Electronics" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The handler-owned persistence path received the whole graph, not a stripped parent.
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler);
        Assert.Equal(2, DeepInsertOptInProfile.LastReceivedByHandler!.Lines.Count);
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler!.Category);

        // §11.4.2.2: the 201 response echoes the created graph, nested values serialized inline.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lines = json.GetProperty("Lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal("WIDGET-1", lines[0].GetProperty("Sku").GetString());
        Assert.Equal("GADGET-9", lines[1].GetProperty("Sku").GetString());
        Assert.Equal("Electronics", json.GetProperty("Category").GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Post_OptIn_ReturnMinimal_Returns204WithODataEntityId_AndStillPersistsGraph()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/odata/DeepInsertOptInOrders")
        {
            Content = JsonContent.Create(new
            {
                customer = "Erin",
                lines = new[] { new { sku = "SPROCKET-3", quantity = 5 } },
            }),
        };
        request.Headers.TryAddWithoutValidation("Prefer", "return=minimal");

        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("OData-EntityId"));
        Assert.True(response.Headers.Contains("Preference-Applied"));
        Assert.Equal("return=minimal", response.Headers.GetValues("Preference-Applied").First());

        // 204 has no body, but the handler still received (and, per contract, persisted) the
        // full graph.
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByHandler);
        Assert.Single(DeepInsertOptInProfile.LastReceivedByHandler!.Lines);
    }

    // ── Deep update (#457): PUT/PATCH obey the same flag ─────────────────────────
    //
    // Deep update -- a nested graph in PUT/PATCH -- is OData 4.01 §11.4.3.1, a separate named
    // feature from deep insert (§11.4.2.2, POST-only), and docs/deep-insert.md has declared it out
    // of scope since 1.0.0. It was not ENFORCED: System.Text.Json bound the nested values and they
    // reached the Put handler (and entered the Delta<TModel> on PATCH) regardless of the flag.
    //
    // Every assertion below is at the HANDLER, never on the wire: #240 omits every EDM navigation
    // from the 200/201 echo whether it was stripped or not, so the response says nothing either
    // way. That is also why this went unnoticed.

    [Fact]
    public async Task Put_Default_StripsNestedCollectionNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            id = 1,
            customer = "Alice",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Lines);

        // BOUNDING: the strip is the navigations, not the body. Scalars survive.
        Assert.Equal("Alice", DeepInsertDefaultProfile.LastReceivedByWriteHandler.Customer);
    }

    [Fact]
    public async Task Put_Default_StripsNestedSingleValuedNav_BeforeHandlerSeesIt()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            id = 1,
            customer = "Bob",
            category = new { name = "Hardware" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Category);
    }

    [Fact]
    public async Task Put_Default_NonNavigationCollectionProperty_Survives()
    {
        // The same bound as on POST: only CLR properties the EDM (or the profile) calls a
        // navigation are stripped. A plain collection property is untouched.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            id = 1,
            customer = "Carol",
            tags = new[] { "rush", "gift-wrap" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Equal(new[] { "rush", "gift-wrap" }, DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Tags);
    }

    [Fact]
    public async Task Patch_Default_NavigationNeverEntersTheDelta()
    {
        // The stronger half. Delta<TEntity> explicitly excludes navigation writes and the
        // delta-mapping subsystem (DeltaMappingCompiler) is scalars/structural only, so a
        // navigation in the Delta<TModel> on the way IN contradicts the subsystem it feeds.
        // Nulling it after the fact is not equivalent: GetChangedPropertyNames() would still
        // name it, and delta.Patch(existing) would still write it.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastPatchChangedProperties = null;
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PatchAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            customer = "Dana",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
            category = new { name = "Hardware" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastPatchChangedProperties);
        Assert.DoesNotContain("Lines", DeepInsertDefaultProfile.LastPatchChangedProperties!);
        Assert.DoesNotContain("Category", DeepInsertDefaultProfile.LastPatchChangedProperties!);

        // BOUNDING: the scalar the same body carried is still in the delta, and still applied.
        Assert.Contains("Customer", DeepInsertDefaultProfile.LastPatchChangedProperties!);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Equal("Dana", DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Customer);
    }

    [Fact]
    public async Task Patch_Default_NonNavigationCollectionProperty_StillEntersTheDelta()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastPatchChangedProperties = null;

        var response = await fx.Client.PatchAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            tags = new[] { "rush" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastPatchChangedProperties);
        Assert.Contains("Tags", DeepInsertDefaultProfile.LastPatchChangedProperties!);
    }

    [Fact]
    public async Task Put_OptIn_PassesFullGraphToHandler()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());
        DeepInsertOptInProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertOptInOrders(1)", new
        {
            id = 1,
            customer = "Erin",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
            category = new { name = "Electronics" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByWriteHandler);
        Assert.Single(DeepInsertOptInProfile.LastReceivedByWriteHandler!.Lines);
        Assert.NotNull(DeepInsertOptInProfile.LastReceivedByWriteHandler!.Category);
    }

    [Fact]
    public async Task Patch_OptIn_NavigationEntersTheDelta()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());
        DeepInsertOptInProfile.LastPatchChangedProperties = null;

        var response = await fx.Client.PatchAsJsonAsync("/odata/DeepInsertOptInOrders(1)", new
        {
            customer = "Frank",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertOptInProfile.LastPatchChangedProperties);
        Assert.Contains("Lines", DeepInsertOptInProfile.LastPatchChangedProperties!);
    }

    // ── #506: the strip is gated on the body having NAMED the navigation ─────────
    //
    // The strip exists to stop a handler that does not expect a graph from silently persisting part
    // of one. If the body sent no graph there is nothing to prevent — and nulling anyway DESTROYS
    // state the handler would otherwise have had. Every assertion below is at the HANDLER: #240
    // omits every EDM navigation from the 200/201 echo whether it was stripped or not, so the wire
    // says nothing either way (the same blind spot that hid #457).

    [Fact]
    public async Task Put_Default_NavigationTheBodyNeverNamed_IsLeftAlone()
    {
        // THE REGRESSION #506 IS ABOUT. Before the fix this PUT — which mentions no navigation at
        // all — nulled every navigation on the model, Kids included, and Kids has a PRIVATE setter
        // that System.Text.Json never touched. The handler got null where the constructor had put
        // an empty list.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            id = 1,
            customer = "Olivia",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = DeepInsertDefaultProfile.LastReceivedByWriteHandler;
        Assert.NotNull(received);

        // The private-setter, constructor-initialized collection survives intact.
        Assert.NotNull(received!.Kids);
        Assert.Empty(received.Kids);

        // So does a public-setter one the body did not name.
        Assert.NotNull(received.Lines);
        Assert.Empty(received.Lines);

        // BOUNDING: a single-valued navigation the constructor leaves null is still null — the fix
        // preserves what was there, it does not invent a value.
        Assert.Null(received.Category);
        Assert.Null(received.AuditStamp);

        // BOUNDING: the scalars are untouched, as always.
        Assert.Equal("Olivia", received.Customer);
    }

    [Fact]
    public async Task Put_Default_StripsOnlyTheNavigationsTheBodyNamed()
    {
        // The pairing that makes the gate a gate rather than a removal: in ONE request, a named
        // navigation is still stripped (#504's whole purpose) while an unnamed one is not.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        var response = await fx.Client.PutAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            id = 1,
            customer = "Peggy",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = DeepInsertDefaultProfile.LastReceivedByWriteHandler;
        Assert.NotNull(received);
        Assert.Null(received!.Lines);
        Assert.NotNull(received.Kids);
        Assert.Empty(received.Kids);

        // FAIL-CLOSED CHECK: the gate did not simply exempt the private-setter navigation. Name it
        // and it is stripped like any other — the filter over `SetMethod is not null` is left wide
        // on purpose (narrowing it to a PUBLIC setter would exempt a [JsonInclude] private-setter
        // navigation that System.Text.Json binds perfectly well, which opens a deep-write hole).
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var namedKidsContent = new StringContent(
            "{\"id\":1,\"customer\":\"Peggy\",\"kids\":[{\"label\":\"k\"}]}",
            Encoding.UTF8, "application/json");
        var namedKids = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", namedKidsContent);
        Assert.Equal(HttpStatusCode.OK, namedKids.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Kids);
    }

    [Fact]
    public async Task Put_Default_MatchesANavigationNameTheWayTheBinderDid()
    {
        // Case-insensitively (the framework's write options set PropertyNameCaseInsensitive
        // unconditionally) and [JsonPropertyName]-aware. If the gate matched more narrowly than the
        // binder bound, a client could name a navigation, have it BOUND, and have it survive the
        // strip — reopening #504 through a spelling.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var shoutedContent = new StringContent(
            "{\"id\":1,\"customer\":\"Quinn\",\"LINES\":[{\"sku\":\"W\",\"quantity\":1}]}",
            Encoding.UTF8, "application/json");
        var shouted = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", shoutedContent);
        Assert.Equal(HttpStatusCode.OK, shouted.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Lines);

        // The renamed navigation, named by its JSON name — the spelling the client and the binder
        // both use.
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var renamedContent = new StringContent(
            "{\"id\":1,\"customer\":\"Rupert\",\"stamp\":{\"by\":\"audit\"}}",
            Encoding.UTF8, "application/json");
        var renamed = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", renamedContent);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.AuditStamp);

        // BOUNDING: the gate reads the ROOT object's members. `kids` here is a member of the nested
        // category value, not of the order, so it does not count as the order having named its own
        // Kids navigation — while `category`, which the root really does name, is still stripped.
        // deepWriteNavPropsToStrip holds properties of TModel; a same-named member of some other
        // type is not one of them.
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var nestedContent = new StringContent(
            "{\"id\":1,\"customer\":\"Sybil\",\"category\":{\"name\":\"H\",\"kids\":[{\"label\":\"nope\"}]}}",
            Encoding.UTF8, "application/json");
        var nested = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", nestedContent);
        Assert.Equal(HttpStatusCode.OK, nested.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Category);
        Assert.NotNull(DeepInsertDefaultProfile.LastReceivedByWriteHandler.Kids);
        Assert.Empty(DeepInsertDefaultProfile.LastReceivedByWriteHandler.Kids);
    }

    [Fact]
    public async Task Post_Default_NavigationTheBodyNeverNamed_IsLeftAlone()
    {
        // POST's half of #506 — a SEPARATE, pre-existing breaking change rather than a regression:
        // the collection POST has nulled unnamed navigations since 1.0.0. It is fixed alongside PUT
        // because leaving one verb gated and the other unconditional would put back exactly the
        // per-verb write-path divergence this milestone spent ten PRs removing.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByHandler = null;

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Trent",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var received = DeepInsertDefaultProfile.LastReceivedByHandler;
        Assert.NotNull(received);
        Assert.NotNull(received!.Kids);
        Assert.Empty(received.Kids);
        Assert.NotNull(received.Lines);
        Assert.Empty(received.Lines);
        Assert.Equal("Trent", received.Customer);
    }

    [Fact]
    public async Task Post_Default_StripsOnlyTheNavigationsTheBodyNamed()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByHandler = null;

        var response = await fx.Client.PostAsJsonAsync("/odata/DeepInsertDefaultOrders", new
        {
            customer = "Ursula",
            category = new { name = "Hardware" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var received = DeepInsertDefaultProfile.LastReceivedByHandler;
        Assert.NotNull(received);
        // Named -> still stripped. Deep insert is unchanged for a client that actually sent a graph.
        Assert.Null(received!.Category);
        // Unnamed -> untouched.
        Assert.NotNull(received.Lines);
        Assert.Empty(received.Lines);
        Assert.NotNull(received.Kids);
        Assert.Empty(received.Kids);
    }

    [Fact]
    public async Task Patch_Default_WithholdsOnlyTheNavigationsTheBodyNamed_Unchanged()
    {
        // PATCH already had the right behaviour and is deliberately NOT touched by #506: its
        // `continue` fires while iterating the BODY's properties, so it can only ever withhold what
        // the body carried. This pins that, so a later "make the three verbs consistent" edit cannot
        // quietly make PATCH unconditional to match what POST/PUT used to do.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastPatchChangedProperties = null;

        var response = await fx.Client.PatchAsJsonAsync("/odata/DeepInsertDefaultOrders(1)", new
        {
            customer = "Victor",
            lines = new[] { new { sku = "WIDGET-1", quantity = 2 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(DeepInsertDefaultProfile.LastPatchChangedProperties);
        // The named navigation never entered the delta...
        Assert.DoesNotContain("Lines", DeepInsertDefaultProfile.LastPatchChangedProperties!);
        // ...and neither did the ones the body never named, so nothing clears them either.
        Assert.DoesNotContain("Kids", DeepInsertDefaultProfile.LastPatchChangedProperties!);
        Assert.DoesNotContain("AuditStamp", DeepInsertDefaultProfile.LastPatchChangedProperties!);
        Assert.Contains("Customer", DeepInsertDefaultProfile.LastPatchChangedProperties!);
    }

    // ── @odata.bind: documented non-support → 501 ────────────────────────────────

    [Fact]
    public async Task Post_ODataBindAnnotation_Returns501NotImplemented()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Frank\",\"category@odata.bind\":\"DeepInsertOptInCategories(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertOptInOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("error", out var err));
        Assert.Equal("NotImplemented", err.GetProperty("code").GetString());

        // The connection must remain usable after a 501 — no partial write occurred.
        var followUp = await fx.Client.GetAsync("/odata/DeepInsertOptInOrders");
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
    }

    [Fact]
    public async Task Post_ODataBindAnnotation_Returns501_EvenWhenDeepInsertDisabled()
    {
        // @odata.bind is rejected regardless of AllowDeepWrites — it is not silently ignored in
        // either mode.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Grace\",\"category@odata.bind\":\"DeepInsertDefaultCategories(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertDefaultOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Post_ODataBindAnnotation_NestedInsideChild_Returns501()
    {
        // The annotation is detected anywhere in the body, not just at the top level.
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertOptInProfile>());

        using var content = new StringContent(
            "{\"customer\":\"Heidi\",\"lines\":[{\"sku\":\"X\",\"product@odata.bind\":\"Products(1)\"}]}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/DeepInsertOptInOrders", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // ── #456: @odata.bind answers 501 on EVERY write verb, not just the collection POST ──

    /// <summary>
    /// #456. The <c>501</c> exists to say <c>@odata.bind</c> is unimplemented; answering
    /// <c>200</c>/<c>201</c> instead and discarding the annotation is the "looks successful but did
    /// nothing" failure mode the rejection was added to prevent. Only the collection <c>POST</c>
    /// ran the check unconditionally — every other write route deferred it into
    /// <c>PrepareWriteBody</c>, which returns early unless the registration's EDM actually declares
    /// an open complex type. On the majority of registrations (this fixture included: no dictionary
    /// member anywhere) the check therefore never ran.
    /// <para>
    /// The handler assertion is load-bearing: before the fix these returned <c>200</c>, so a
    /// status-only test would have to assert the exact wrong status to fail, and a reader could not
    /// tell whether the annotation had been honoured or dropped.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task EntityWrite_ODataBindAnnotation_Returns501_OnARegistrationWithNoOpenType(string method)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        using var request = new HttpRequestMessage(new HttpMethod(method), "/odata/DeepInsertDefaultOrders(1)")
        {
            Content = new StringContent(
                "{\"id\":1,\"customer\":\"Judy\",\"category@odata.bind\":\"DeepInsertDefaultCategories(1)\"}",
                Encoding.UTF8, "application/json"),
        };
        var response = await fx.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NotImplemented", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
    }

    /// <summary>
    /// The nav-POST create route (§11.4.2.1) is the second of the two routes that stream the body
    /// straight into the deserializer rather than materialising a <see cref="JsonElement"/>, so it
    /// could not be fixed by hoisting the check inside <c>PrepareWriteBody</c> alone — it does not
    /// call <c>PrepareWriteBody</c> at all on this path.
    /// </summary>
    [Fact]
    public async Task NavPostCreate_ODataBindAnnotation_Returns501()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertWithNavHandlersProfile>());

        var createResponse = await fx.Client.PostAsJsonAsync("/odata/DeepInsertNavOrders", new { customer = "Ken" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        int orderId = (await createResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("Id").GetInt32();

        using var content = new StringContent(
            "{\"text\":\"note\",\"order@odata.bind\":\"DeepInsertNavOrders(1)\"}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync($"/odata/DeepInsertNavOrders({orderId})/Notes", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        // Nothing was created: the annotation is rejected before the child is bound or handed over.
        var notes = await fx.Client.GetFromJsonAsync<JsonElement>($"/odata/DeepInsertNavOrders({orderId})/Notes");
        Assert.Equal(0, notes.GetProperty("value").GetArrayLength());
    }

    /// <summary>
    /// The structural-property write route rides <c>Patch</c> and replaces one property's value, so
    /// the annotation is looked for inside the <c>value</c> member. It is rejected before the value
    /// is bound to the property's CLR type — which is why an object here, against a string property,
    /// answers <c>501</c> and not the <c>400</c> a type mismatch would give.
    /// </summary>
    [Fact]
    public async Task PropertyWrite_ODataBindAnnotation_Returns501()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        using var content = new StringContent(
            "{\"value\":{\"thing@odata.bind\":\"DeepInsertDefaultCategories(1)\"}}",
            Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)/Customer", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
    }

    /// <summary>
    /// The annotation is found at ANY depth on the streaming routes too — the raw-UTF-8 scan the
    /// two of them use must agree with the <see cref="JsonElement"/> walk every other route uses,
    /// including inside an array. Escaped spellings are the same member name and must not be a
    /// bypass.
    /// </summary>
    [Theory]
    [InlineData("{\"id\":1,\"customer\":\"Leo\",\"lines\":[{\"sku\":\"X\",\"product@odata.bind\":\"Products(1)\"}]}")]
    [InlineData("{\"id\":1,\"customer\":\"Leo\",\"category\\u0040odata.bind\":\"Cats(1)\"}")]
    public async Task Put_ODataBindAnnotation_IsFoundNestedAndWhenEscaped(string body)
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", content);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler);
    }

    /// <summary>
    /// BOUNDING ASSERTIONS for the theories above. A body with no annotation still binds and still
    /// reaches the handler on every one of those routes — so "everything 501s" cannot pass
    /// vacuously — and, specifically for <c>PUT</c>, a malformed body is still worded by
    /// <c>JsonSerializer</c> (which appends <c>Path: $</c>) rather than by <c>JsonDocument</c>
    /// (which does not). That last one is the pin for #456's implementation choice: the two
    /// streaming routes buffer the body to scan it, and buffering must not swap which component
    /// reports a malformed body — the difference is observable, and
    /// <c>OpenTypeDefaultOnIsByteIdenticalTests</c> exists because of it.
    /// </summary>
    [Fact]
    public async Task WritesWithoutTheAnnotation_StillSucceed_AndPutStillWordsAMalformedBodyItself()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var putContent =
            new StringContent("{\"id\":1,\"customer\":\"Mallory\"}", Encoding.UTF8, "application/json");
        var put = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", putContent);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Mallory", DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Customer);

        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var patchRequest = new HttpRequestMessage(new HttpMethod("PATCH"), "/odata/DeepInsertDefaultOrders(1)")
        {
            Content = new StringContent("{\"customer\":\"Niaj\"}", Encoding.UTF8, "application/json"),
        };
        var patch = await fx.Client.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Niaj", DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Customer);

        // PUT's malformed-body message must still come from the deserializer.
        using var malformedContent = new StringContent("{ not json", Encoding.UTF8, "application/json");
        var malformed = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", malformedContent);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        string malformedBody = await malformed.Content.ReadAsStringAsync();
        Assert.Contains("Path: $", malformedBody, StringComparison.Ordinal);

        // ...and the collection POST's must still come from JsonDocument, which words it without a
        // Path. The two readers differ, and that is the difference buffering could have erased.
        using var malformedPostContent = new StringContent("{ not json", Encoding.UTF8, "application/json");
        var malformedPost = await fx.Client.PostAsync("/odata/DeepInsertDefaultOrders", malformedPostContent);
        Assert.Equal(HttpStatusCode.BadRequest, malformedPost.StatusCode);
        Assert.DoesNotContain("Path: $", await malformedPost.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The buffered scan must cover the WHOLE body, not the first chunk of it. The capacity hint
    /// <c>BufferRequestBodyAsync</c> takes from <c>Content-Length</c> is clamped to 81,920 bytes —
    /// deliberately, because <c>Content-Length</c> is a client claim that arrives before any body
    /// byte does, and pre-sizing from it would hand an unauthenticated caller a remote allocation
    /// primitive (declare 30 MB, send one byte, repeat). This body is comfortably past that cap, so
    /// it exercises the growth path, and the annotation sits at the very END of it — after the point
    /// where a scan that stopped at the hint, or a copy that honoured it as a length, would have
    /// stopped looking.
    /// </summary>
    [Fact]
    public async Task Put_BodyLargerThanTheCapacityHint_IsScannedAndBoundInFull()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertDefaultProfile>());

        // ~200 KB of payload — several doublings past the 81,920-byte hint cap.
        string filler = new string('x', 200_000);

        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var annotatedContent = new StringContent(
            $"{{\"id\":1,\"customer\":\"{filler}\",\"category@odata.bind\":\"DeepInsertDefaultCategories(1)\"}}",
            Encoding.UTF8, "application/json");
        var withAnnotation = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", annotatedContent);

        Assert.Equal(HttpStatusCode.NotImplemented, withAnnotation.StatusCode);
        Assert.Null(DeepInsertDefaultProfile.LastReceivedByWriteHandler);

        // BOUNDING ASSERTION: the same oversized body without the annotation still binds, and every
        // byte of it survives the round trip — so "large bodies 501" cannot pass vacuously and the
        // buffer is proven to hold the whole payload rather than a truncated prefix.
        DeepInsertDefaultProfile.LastReceivedByWriteHandler = null;
        using var cleanContent =
            new StringContent($"{{\"id\":1,\"customer\":\"{filler}\"}}", Encoding.UTF8, "application/json");
        var clean = await fx.Client.PutAsync("/odata/DeepInsertDefaultOrders(1)", cleanContent);

        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.Equal(filler, DeepInsertDefaultProfile.LastReceivedByWriteHandler!.Customer);
    }

    // ── Coexists with a profile that also has PostChild / batch nav handlers ────────

    [Fact]
    public async Task Post_CoexistsWithPostChildAndBatchNavHandlersOnSameProfile()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<DeepInsertWithNavHandlersProfile>());

        // Deep insert on the entity-level POST route.
        var createResponse = await fx.Client.PostAsJsonAsync("/odata/DeepInsertNavOrders", new
        {
            customer = "Ivan",
            lines = new[] { new { sku = "WIDGET-1", quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, created.GetProperty("Lines").GetArrayLength());
        int orderId = created.GetProperty("Id").GetInt32();

        // POST-to-nav (PostChild, §11.4.2.1) still works on the same profile.
        var postChildResponse = await fx.Client.PostAsJsonAsync(
            $"/odata/DeepInsertNavOrders({orderId})/Notes", new { text = "Handle with care" });
        Assert.Equal(HttpStatusCode.Created, postChildResponse.StatusCode);

        // Batch-loaded nav route (GET) still works on the same profile.
        var linesResponse = await fx.Client.GetAsync($"/odata/DeepInsertNavOrders({orderId})/Lines");
        Assert.Equal(HttpStatusCode.OK, linesResponse.StatusCode);
        var linesJson = await linesResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, linesJson.GetProperty("value").GetArrayLength());
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    private class DeepInsertLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private class DeepInsertCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class DeepInsertKid
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    private class DeepInsertStamp
    {
        public int Id { get; set; }
        public string By { get; set; } = "";
    }

    private class DeepInsertOrder
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<DeepInsertLine> Lines { get; set; } = new();
        public DeepInsertCategory? Category { get; set; }

        // Deliberately NOT declared via HasMany/HasOptional/HasRequired — a plain collection
        // property, not a navigation. Must survive stripping in every mode.
        public List<string> Tags { get; set; } = new();

        // #506: the standard EF-encapsulation shape — a collection navigation with a PRIVATE
        // setter, initialized by the constructor, and never declared to the profile (the convention
        // model builder discovers it, so it reaches the strip set through #461's EDM union).
        // System.Text.Json cannot bind into a private setter without [JsonInclude], so a request
        // body can only ever leave this exactly as the constructor left it — which makes it the
        // sharpest possible probe: anything other than an empty list at the handler came from the
        // framework, not from the client. PropertyInfo.SetMethod returns the private accessor, so it
        // IS in deepWriteNavPropsToStrip, and before #506 the unconditional strip nulled it.
        public List<DeepInsertKid> Kids { get; private set; } = new();

        // #506: a navigation whose JSON name is not its CLR name. The body-presence gate has to
        // match it the way the BINDER matched it — through [JsonPropertyName] — or a renamed
        // navigation would slip a strip an un-renamed one received. A second, subtly different name
        // comparison beside an existing one is exactly how #454 happened.
        [JsonPropertyName("stamp")]
        public DeepInsertStamp? AuditStamp { get; set; }
    }

    /// <summary>AllowDeepWrites left at its default (false) — nested nav values are stripped.</summary>
    private class DeepInsertDefaultProfile : EntitySetProfile<int, DeepInsertOrder>
    {
        private static int _nextId = 1;
        private readonly List<DeepInsertOrder> _orders = new();

        // Static so the test can observe exactly what the handler received, independent of
        // whatever the framework echoes back in the HTTP response.
        public static DeepInsertOrder? LastReceivedByHandler;

        /// <summary>
        /// #456: the same observation for the UPDATE verbs. A separate field from
        /// <see cref="LastReceivedByHandler"/> so a <c>@odata.bind</c> test can assert "the update
        /// handler never ran" without a stale POST capture answering for it.
        /// </summary>
        public static DeepInsertOrder? LastReceivedByWriteHandler;

        /// <summary>
        /// #457: the names <c>Delta&lt;TModel&gt;.GetChangedPropertyNames()</c> reported at the
        /// handler. A navigation must never be in it — <c>LastReceivedByWriteHandler</c> alone
        /// cannot tell "never entered the delta" from "entered it and applied a null".
        /// </summary>
        public static string[]? LastPatchChangedProperties;

        public DeepInsertDefaultProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertDefaultOrders";

            // Declared as navigations (EDM-only, no route) so they participate in the strip set.
            HasMany(x => x.Lines);
            HasOptional(x => x.Category!);

            GetAll = (_) => Task.FromResult<IEnumerable<DeepInsertOrder>>(_orders);

            Post = (order, _) =>
            {
                LastReceivedByHandler = order;
                order.Id = _nextId++;
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrder?>(order);
            };

            // #456: PUT/PATCH (and, riding PATCH, the structural-property writes) exist on this
            // fixture so the @odata.bind asymmetry can be probed on the verbs that had it. They are
            // deliberately non-persisting — what a test needs is what the handler RECEIVED.
            Put = (id, order, _) =>
            {
                LastReceivedByWriteHandler = order;
                order.Id = id;
                return Task.FromResult(order);
            };

            Patch = (id, delta, _) =>
            {
                LastPatchChangedProperties = delta.GetChangedPropertyNames().ToArray();
                var order = new DeepInsertOrder { Id = id };
                delta.Patch(order);
                LastReceivedByWriteHandler = order;
                return Task.FromResult<DeepInsertOrder?>(order);
            };
        }
    }

    /// <summary>AllowDeepWrites = true — full graph passed through; handler owns persistence.</summary>
    private class DeepInsertOptInProfile : EntitySetProfile<int, DeepInsertOrder>
    {
        private static int _nextId = 1;
        private readonly List<DeepInsertOrder> _orders = new();

        public static DeepInsertOrder? LastReceivedByHandler;

        /// <summary>#457: the opt-in twin of <c>DeepInsertDefaultProfile.LastReceivedByWriteHandler</c>.</summary>
        public static DeepInsertOrder? LastReceivedByWriteHandler;

        /// <summary>#457: the opt-in twin of <c>DeepInsertDefaultProfile.LastPatchChangedProperties</c>.</summary>
        public static string[]? LastPatchChangedProperties;

        public DeepInsertOptInProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertOptInOrders";
            AllowDeepWrites = true;

            HasMany(x => x.Lines);
            HasOptional(x => x.Category!);

            GetAll = (_) => Task.FromResult<IEnumerable<DeepInsertOrder>>(_orders);

            // #457: the opt-in side of deep UPDATE. Non-persisting for the same reason the default
            // profile's are -- what a test needs is what the handler RECEIVED.
            Put = (id, order, _) =>
            {
                LastReceivedByWriteHandler = order;
                order.Id = id;
                return Task.FromResult(order);
            };

            Patch = (id, delta, _) =>
            {
                LastPatchChangedProperties = delta.GetChangedPropertyNames().ToArray();
                var order = new DeepInsertOrder { Id = id };
                delta.Patch(order);
                LastReceivedByWriteHandler = order;
                return Task.FromResult<DeepInsertOrder?>(order);
            };

            Post = (order, _) =>
            {
                LastReceivedByHandler = order;
                order.Id = _nextId++;
                int lineId = 1;
                foreach (var line in order.Lines)
                {
                    line.Id = lineId++;
                    line.OrderId = order.Id;
                }
                if (order.Category is not null) order.Category.Id = 1;
                // "Atomic persistence" stand-in: a single in-memory add representing the whole
                // graph, mirroring the contract of a single EF Core SaveChanges call.
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrder?>(order);
            };
        }
    }

    private class DeepInsertNote
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Text { get; set; } = "";
    }

    private class DeepInsertOrderWithNotes
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public List<DeepInsertLine> Lines { get; set; } = new();
        public List<DeepInsertNote> Notes { get; set; } = new();
    }

    /// <summary>
    /// Deep insert (entity-level POST, opted in) on a profile that ALSO has a batch-loaded nav
    /// route (Lines) and a PostChild nav route (Notes, §11.4.2.1) — verifies the three POST-ish
    /// pipelines (entity POST/deep-insert, POST-to-nav, GET-nav) don't collide or interfere.
    /// </summary>
    private class DeepInsertWithNavHandlersProfile : EntitySetProfile<int, DeepInsertOrderWithNotes>
    {
        // Static: profiles are registered AddScoped, so each HTTP request resolves a fresh
        // profile instance. Backing "storage" must be static (shared) for state written by one
        // request (the deep-insert POST) to be observable by a later request (POST-to-nav, GET).
        private static int _nextId = 1;
        private static int _nextNoteId = 1;
        private static readonly List<DeepInsertOrderWithNotes> _orders = new();
        private static readonly List<DeepInsertNote> _notes = new();

        public DeepInsertWithNavHandlersProfile() : base(x => x.Id)
        {
            EntitySetName = "DeepInsertNavOrders";
            AllowDeepWrites = true;

            HasMany(x => x.Lines, batchGetAll: (orderIds, ct) =>
            {
                var lookup = _orders
                    .Where(o => orderIds.Contains(o.Id))
                    .SelectMany(o => o.Lines)
                    .ToLookup(l => l.OrderId);
                return Task.FromResult(lookup);
            });

            HasMany(
                navigation: x => x.Notes,
                getAll: (orderId, ct) => Task.FromResult<IEnumerable<DeepInsertNote>>(_notes.Where(n => n.OrderId == orderId)),
                post: (orderId, note, ct) =>
                {
                    if (_orders.All(o => o.Id != orderId)) return Task.FromResult<DeepInsertNote?>(null);
                    note.Id = _nextNoteId++;
                    note.OrderId = orderId;
                    _notes.Add(note);
                    return Task.FromResult<DeepInsertNote?>(note);
                });

            GetById = (id, _) => Task.FromResult(_orders.FirstOrDefault(o => o.Id == id));

            Post = (order, _) =>
            {
                order.Id = _nextId++;
                int lineId = 1;
                foreach (var line in order.Lines)
                {
                    line.Id = lineId++;
                    line.OrderId = order.Id;
                }
                _orders.Add(order);
                return Task.FromResult<DeepInsertOrderWithNotes?>(order);
            };
        }
    }
}
