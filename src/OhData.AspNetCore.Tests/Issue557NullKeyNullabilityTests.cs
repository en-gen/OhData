using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #557 — an explicit <c>null</c> for a REFERENCE-TYPED key bypassed the nullability gate, ran the
/// handler, and then died in the framework's own response construction.
/// <para>
/// The pre-fix answer depended on the HANDLER, which is the part #557's report does not say. Both
/// were measured here by ablation (restoring the key exclusion and re-running):
/// </para>
/// <list type="bullet">
/// <item>a handler that does NOT supply the key — <c>500</c>, from
/// <c>ODataEntityKeyUrlFormatter.Format</c> ("OData key value must not be null."), <b>after the
/// handler had already run</b>. That is what #557 reports, and it is worse than #355's original
/// symptom: a <c>Post</c> that persists gets to persist, so the <c>500</c> can arrive with the write
/// already committed.</item>
/// <item>a handler that DOES supply it (<c>thing.Code ??= "generated"</c>, which
/// <c>K557Profile.Post</c> does) — <c>201 Created</c>. So this change is not purely
/// <c>500 → 400</c>; it also refuses a request that used to succeed.</item>
/// </list>
/// <para>
/// Either way <c>POST {"Code":"a","Name":null}</c> answered <c>400</c> with the handler not reached —
/// two properties <c>$metadata</c> describes identically, answering differently.
/// </para>
/// <para>
/// #355 excluded the key because a service-generated key is routinely OMITTED (§11.4.2). That was
/// correct while the gate also rejected omission. #544 removed the omission leg, which left the
/// exclusion doing nothing for its stated purpose and hiding this case. The exclusion is gone; the
/// gate's own <c>namedByBody</c> intersection now provides the omission exemption.
/// </para>
/// </summary>
public class Issue557NullKeyNullabilityTests
{
    // ── the contract the two properties share ────────────────────────────────────────────────

    [Fact]
    public async Task Metadata_DescribesTheKeyAndTheOrdinaryPropertyIdentically()
    {
        // The whole argument rests on this: if $metadata distinguished them, two answers would be
        // defensible. It does not.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");
        XElement entity = XDocument.Parse(csdl)
            .Descendants().Single(e => e.Name.LocalName == "EntityType"
                                       && (string?)e.Attribute("Name") == nameof(K557Thing));

        string?[] Describe(string name) => entity.Elements()
            .Where(e => e.Name.LocalName == "Property" && (string?)e.Attribute("Name") == name)
            .Select(e => (string?)e.Attribute("Type") + "|" + ((string?)e.Attribute("Nullable") ?? "<absent>"))
            .ToArray();

        Assert.Equal(Describe(nameof(K557Thing.Name)), Describe(nameof(K557Thing.Code)));
    }

    // ── the defect ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ExplicitNullKey_Is400_AndTheHandlerNeverRuns()
    {
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Code"] = null, ["Name"] = "x" });

        // Handler non-execution is the load-bearing half: the 500 this replaces arrived AFTER the
        // handler had run, so a persisting Post had already persisted.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(K557Profile.PostReached);

        using JsonDocument body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement error = body.RootElement.GetProperty("error");
        Assert.Equal("InvalidBody", error.GetProperty("code").GetString());
        Assert.Equal(nameof(K557Thing.Code), error.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Post_ExplicitNullKey_AnswersExactlyAsAnOrdinaryNonNullableProperty()
    {
        // #557's core claim, asserted as an equality rather than as two separate expectations: two
        // properties the published contract describes identically must answer identically.
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var keyResp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Code"] = null, ["Name"] = "x" });
        using var ordinaryResp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Code"] = "a", ["Name"] = null });

        Assert.Equal(ordinaryResp.StatusCode, keyResp.StatusCode);

        static async Task<string> CodeOf(HttpResponseMessage r)
        {
            using JsonDocument d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return d.RootElement.GetProperty("error").GetProperty("code").GetString()!;
        }

        Assert.Equal(await CodeOf(ordinaryResp), await CodeOf(keyResp));
        Assert.False(K557Profile.PostReached);
    }

    [Fact]
    public async Task Put_ExplicitNullKey_Is400_AndTheHandlerNeverRuns()
    {
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var resp = await fx.Client.PutAsJsonAsync(
            "/odata/K557Things('a')",
            new Dictionary<string, object?> { ["Code"] = null, ["Name"] = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(K557Profile.PutReached);
    }

    // ── the exemptions that must survive ─────────────────────────────────────────────────────

    [Fact]
    public async Task Post_OmittedKey_Still201_BecauseAServiceGeneratedKeyIsRoutinelyOmitted()
    {
        // §11.4.2's permission is about OMISSION, and that is the exemption #355 actually needed.
        // It now comes from the gate's own namedByBody intersection rather than from a blanket key
        // exclusion — this is the test that would fail if the fix had removed the exemption too.
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Name"] = "x" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(K557Profile.PostReached);
    }

    [Fact]
    public async Task Post_KeyWithAValue_Still201()
    {
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Code"] = "a", ["Name"] = "x" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(K557Profile.PostReached);
    }

    [Fact]
    public async Task Post_NullValueTypeKey_StaysTheDeserializersOwn400()
    {
        // The value-type exclusion is untouched and still correct: `int Id` cannot hold null, so the
        // deserializer rejects it before the gate is consulted. Asserted so a future widening of the
        // gate cannot silently start double-handling this.
        K557IntKeyProfile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557IntKeyProfile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557IntThings", new Dictionary<string, object?> { ["Id"] = null, ["Name"] = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(K557IntKeyProfile.PostReached);
    }

    [Fact]
    public async Task Post_ExplicitNullKey_NowRefusesAHandlerThatWouldHaveGeneratedTheKey()
    {
        // THE BREAKING HALF, pinned deliberately. #557 reports the 500 that follows a handler which
        // does NOT generate the key. A handler that DOES -- `thing.Code ??= "generated"`, which is
        // what K557Profile.Post does -- previously answered 201 for the very same body. Measured by
        // ablation: restoring the key exclusion turns this request back into `Created`.
        //
        // So the change is not purely 500 -> 400: it also refuses a request that used to succeed.
        // That is intended. `{"Code": null}` is the client asserting the key IS null, which has no
        // valid reading, and the way to ask the service to supply one is to OMIT the property --
        // which still works, and is what the test above pins.
        K557Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557Profile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Code"] = null, ["Name"] = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(K557Profile.PostReached);

        // The remedy, in the same test so it cannot drift from the refusal it documents.
        K557Profile.Reset();
        using var omitted = await fx.Client.PostAsJsonAsync(
            "/odata/K557Things", new Dictionary<string, object?> { ["Name"] = "x" });
        Assert.Equal(HttpStatusCode.Created, omitted.StatusCode);
        Assert.True(K557Profile.PostReached);
    }

    [Fact]
    public async Task Post_ExplicitNullKey_IsAllowedWhenTheProfileOptsOutOfTheGate()
    {
        // RequestBodyNullabilityValidationEnabled = false must still opt the KEY out, exactly as it
        // opts out every other property — the fix must not create a check that ignores the flag.
        // The handler runs and the 500 that follows is the pre-#557 behaviour, deliberately kept for
        // an entity set that has switched the gate off.
        K557OptedOutProfile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<K557OptedOutProfile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/K557OptedOut", new Dictionary<string, object?> { ["Code"] = null, ["Name"] = "x" });

        Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True(K557OptedOutProfile.PostReached);
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class K557Thing
{
    // A REFERENCE-typed key, which is what makes an explicit null representable at all. Every
    // pre-existing nullability fixture keys on `int`, which the value-type exclusion covers — which
    // is why this case survived #355 and #544.
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
}

internal class K557Profile : EntitySetProfile<string, K557Thing>
{
    public static bool PostReached;
    public static bool PutReached;

    public static void Reset()
    {
        PostReached = false;
        PutReached = false;
    }

    public K557Profile() : base(x => x.Code)
    {
        EntitySetName = "K557Things";

        GetById = (code, _) => OhDataResult.SuccessTask<K557Thing>(new K557Thing { Code = code, Name = "n" });

        Post = (thing, _) =>
        {
            PostReached = true;
            thing.Code ??= "generated";
            return OhDataResult.SuccessTask<K557Thing>(thing);
        };

        Put = (code, thing, _) =>
        {
            PutReached = true;
            thing.Code = code;
            return OhDataResult.SuccessTask(thing);
        };
    }
}

internal class K557IntThing
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

internal class K557IntKeyProfile : EntitySetProfile<int, K557IntThing>
{
    public static bool PostReached;

    public static void Reset() => PostReached = false;

    public K557IntKeyProfile() : base(x => x.Id)
    {
        EntitySetName = "K557IntThings";

        Post = (thing, _) =>
        {
            PostReached = true;
            return OhDataResult.SuccessTask<K557IntThing>(thing);
        };
    }
}

internal class K557OptedOutThing
{
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
}

internal class K557OptedOutProfile : EntitySetProfile<string, K557OptedOutThing>
{
    public static bool PostReached;

    public static void Reset() => PostReached = false;

    public K557OptedOutProfile() : base(x => x.Code)
    {
        EntitySetName = "K557OptedOut";
        RequestBodyNullabilityValidationEnabled = false;

        Post = (thing, _) =>
        {
            PostReached = true;
            return OhDataResult.SuccessTask<K557OptedOutThing>(thing);
        };
    }
}
