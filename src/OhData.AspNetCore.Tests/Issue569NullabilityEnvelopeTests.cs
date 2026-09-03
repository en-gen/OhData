using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #569 / #558 — one condition, one envelope.
/// <para>
/// #355 made the EDM the single authority on nullability for all five write-route families, which
/// made the CONDITION the same everywhere. It did not unify the ANSWER. Three wordings and two
/// <c>code</c> values shipped:
/// </para>
/// <list type="table">
/// <item><term><c>POST</c>/<c>PUT</c>/nav-<c>POST</c></term>
///   <description><c>InvalidBody</c> — "…and cannot be null."</description></item>
/// <item><term><c>PATCH</c></term>
///   <description><c>InvalidBody</c> — "…and cannot be <b>set to</b> null."</description></item>
/// <item><term>property writes</term>
///   <description><b><c>BadRequest</c></b> — "…is <b>not nullable</b> and cannot be set to null."</description></item>
/// </list>
/// <para>
/// A client moving from <c>PATCH /Set(1)</c> to <c>PUT /Set(1)/Prop</c> got a different error
/// <c>code</c> for the same rejection — against the rule #543 states and #357/#467 both cite.
/// <b>Nothing pinned any of the three</b>, which is why they were free to diverge: this suite is that
/// missing pin, and it asserts the five families against EACH OTHER rather than against a literal, so
/// the envelope can be reworded but cannot be forked.
/// </para>
/// <para>
/// #558 is why the unified wording is not simply PATCH's. Three different arrivals reach this one
/// condition, and "cannot be null" is false of the second:
/// </para>
/// <list type="bullet">
/// <item>the body named the property with an explicit <c>null</c>;</item>
/// <item>the body sent a value under a spelling the binder IGNORED — reachable under a
///   non-case-preserving <c>PropertyNamingPolicy</c>, because the body-name table carries EDM and CLR
///   aliases the binder does not honour. Those aliases must STAY (dropping them fails OPEN, per
///   #511), so the message is what gets fixed;</item>
/// <item><c>DELETE /Set(key)/Prop</c>, which supplies no value at all.</item>
/// </list>
/// </summary>
public class Issue569NullabilityEnvelopeTests
{
    private static StringContent Json(string raw) =>
        new(raw, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, string Code, string Message, string? Target)>
        EnvelopeOf(HttpResponseMessage resp)
    {
        using JsonDocument d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement e = d.RootElement.GetProperty("error");
        return (resp.StatusCode,
                e.GetProperty("code").GetString()!,
                e.GetProperty("message").GetString()!,
                e.TryGetProperty("target", out JsonElement t) ? t.GetString() : null);
    }

    // ── the five families must agree ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AllFiveWriteRouteFamilies_AnswerOneIdenticalEnvelope()
    {
        // Asserted as equality against the FIRST family rather than against a literal string: the
        // point of #569 is that they must not diverge, not that any particular sentence is right.
        // A future reword stays green; a future fork does not.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<E569Profile>().AddEntitySetProfile<E569PartProfile>());

        var post = await EnvelopeOf(await fx.Client.PostAsJsonAsync(
            "/odata/E569Things", new Dictionary<string, object?> { ["Id"] = 1, ["Title"] = null }));

        var put = await EnvelopeOf(await fx.Client.PutAsJsonAsync(
            "/odata/E569Things(1)", new Dictionary<string, object?> { ["Id"] = 1, ["Title"] = null }));

        var patch = await EnvelopeOf(await fx.Client.PatchAsync(
            "/odata/E569Things(1)", Json("{\"Title\":null}")));

        var propPut = await EnvelopeOf(await fx.Client.PutAsync(
            "/odata/E569Things(1)/Title", Json("{\"value\":null}")));

        var propDelete = await EnvelopeOf(await fx.Client.DeleteAsync(
            "/odata/E569Things(1)/Title"));

        foreach (var other in new[] { put, patch, propPut, propDelete })
        {
            Assert.Equal(post.Status, other.Status);
            Assert.Equal(post.Code, other.Code);
            Assert.Equal(post.Message, other.Message);
            Assert.Equal(post.Target, other.Target);
        }

        // And the vocabulary itself: every other body-validation failure in the framework uses
        // InvalidBody, which is the half the property routes were out of line on.
        Assert.Equal(HttpStatusCode.BadRequest, post.Status);
        Assert.Equal("InvalidBody", post.Code);
        Assert.Equal(nameof(E569Thing.Title), post.Target);
    }

    [Fact]
    public async Task NavigationPostCreateRoute_AnswersTheSameEnvelope()
    {
        // The fifth family, separated only because it needs a navigation to post through.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<E569Profile>().AddEntitySetProfile<E569PartProfile>());

        var direct = await EnvelopeOf(await fx.Client.PostAsJsonAsync(
            "/odata/E569Parts", new Dictionary<string, object?> { ["Id"] = 1, ["Label"] = null }));

        var viaNav = await EnvelopeOf(await fx.Client.PostAsJsonAsync(
            "/odata/E569Things(1)/Parts", new Dictionary<string, object?> { ["Id"] = 1, ["Label"] = null }));

        Assert.Equal(direct.Status, viaNav.Status);
        Assert.Equal(direct.Code, viaNav.Code);
        Assert.Equal(direct.Message, viaNav.Message);
        Assert.Equal(direct.Target, viaNav.Target);
    }

    // ── #558: the wording has to be true when the binder ignored the key ─────────────────────

    [Theory]
    [InlineData("created_by")]
    [InlineData("created-by")]
    public async Task BinderIgnoredSpelling_DoesNotClaimTheClientSentNull(string boundSpelling)
    {
        // #558. Under a non-case-preserving policy the binder wants `created_by`; the table also
        // carries the CLR alias `CreatedBy`, so a body using the CLR spelling is reported as "named"
        // while the binder ignored it and left the property at its `null!` default.
        //
        // The 400 is CORRECT and unchanged — the property really is null after binding, and the
        // aliases must stay, because dropping them would make this body UNNAMED, skip the gate, and
        // hand the handler a null under a 201 (#511's fail-open). What was wrong is the message:
        // "cannot be null" describes a request that sent null, and this one sent "x".
        JsonNamingPolicy policy = boundSpelling.Contains('_')
            ? JsonNamingPolicy.SnakeCaseLower
            : JsonNamingPolicy.KebabCaseLower;

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.WithJsonPropertyNamingPolicy(policy).AddEntitySetProfile<E569SnakeProfile>());

        var ignored = await EnvelopeOf(await fx.Client.PostAsJsonAsync(
            "/odata/E569Snakes", new Dictionary<string, object?> { ["Id"] = 1, ["CreatedBy"] = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, ignored.Status);
        Assert.Equal("InvalidBody", ignored.Code);

        // The load-bearing assertion: the message must not assert something the request did not do.
        Assert.DoesNotContain("cannot be null", ignored.Message);
        Assert.DoesNotContain("set to null", ignored.Message);

        // …and it must be the SAME envelope the explicit-null arrival produces, because it is the
        // same condition — that is #569's half of this test.
        var explicitNull = await EnvelopeOf(await fx.Client.PostAsJsonAsync(
            "/odata/E569Snakes",
            new Dictionary<string, object?> { ["Id"] = 1, [boundSpelling] = null }));

        Assert.Equal(explicitNull.Code, ignored.Code);
        Assert.Equal(explicitNull.Message, ignored.Message);
    }

    [Theory]
    [InlineData("created_by")]
    [InlineData("created-by")]
    public async Task TheSpellingTheBinderHonours_IsAccepted(string boundSpelling)
    {
        // The bound control: the fix is about wording, not about narrowing the table, so the
        // spelling the binder DOES honour must still succeed. Without this, a message change that
        // accidentally became a refusal would pass the test above.
        JsonNamingPolicy policy = boundSpelling.Contains('_')
            ? JsonNamingPolicy.SnakeCaseLower
            : JsonNamingPolicy.KebabCaseLower;

        E569SnakeProfile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.WithJsonPropertyNamingPolicy(policy).AddEntitySetProfile<E569SnakeProfile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/E569Snakes",
            new Dictionary<string, object?> { ["id"] = 1, [boundSpelling] = "x" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("x", E569SnakeProfile.LastPosted!.CreatedBy);
    }

    // ── the surrounding behaviour that must not move ─────────────────────────────────────────

    [Fact]
    public async Task ANullableProperty_IsStillAccepted()
    {
        E569Profile.Reset();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<E569Profile>().AddEntitySetProfile<E569PartProfile>());

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/E569Things",
            new Dictionary<string, object?> { ["Id"] = 1, ["Title"] = "t", ["Note"] = null });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(E569Profile.PostReached);
    }

    [Fact]
    public async Task PropertyWrite_OfANullableProperty_IsStillAccepted()
    {
        // The property route's own gate is EDM-sourced since #355; this pins that the unified
        // envelope did not turn it into a blanket refusal.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<E569Profile>().AddEntitySetProfile<E569PartProfile>());

        using var resp = await fx.Client.PutAsync(
            "/odata/E569Things(1)/Note", Json("{\"value\":null}"));

        Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

// ── fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class E569Part
{
    public int Id { get; set; }
    public string Label { get; set; } = null!;
}

internal class E569Thing
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Note { get; set; }
    public List<E569Part> Parts { get; set; } = new();
}

internal class E569Profile : EntitySetProfile<int, E569Thing>
{
    public static bool PostReached;

    public static void Reset() => PostReached = false;

    public E569Profile() : base(x => x.Id)
    {
        EntitySetName = "E569Things";

        HasMany(
            x => x.Parts,
            getAll: (_, _) => Task.FromResult<IEnumerable<E569Part>>(System.Array.Empty<E569Part>()),
            post: (_, part, _) => Task.FromResult<E569Part?>(part));

        GetById = (id, _) => OhDataResult.SuccessTask<E569Thing>(
            new E569Thing { Id = id, Title = "t", Note = "n" });

        Post = (thing, _) =>
        {
            PostReached = true;
            return OhDataResult.SuccessTask<E569Thing>(thing);
        };

        Put = (id, thing, _) => { thing.Id = id; return OhDataResult.SuccessTask(thing); };

        Patch = (id, delta, _) =>
        {
            var thing = new E569Thing { Id = id, Title = "t", Note = "n" };
            delta.Patch(thing);
            return OhDataResult.SuccessTask<E569Thing>(thing);
        };
    }
}

internal class E569PartProfile : EntitySetProfile<int, E569Part>
{
    public E569PartProfile() : base(x => x.Id)
    {
        EntitySetName = "E569Parts";
        Post = (part, _) => OhDataResult.SuccessTask<E569Part>(part);
    }
}

/// <summary>
/// Multi-word on purpose: a single-word property's snake_case spelling differs from its CLR name
/// only by case, which the table's <c>OrdinalIgnoreCase</c> comparer already covers — which is
/// exactly why camelCase hides #558 and <c>SnakeCaseLower</c> does not.
/// </summary>
internal class E569Snake
{
    public int Id { get; set; }
    public string CreatedBy { get; set; } = null!;
}

internal class E569SnakeProfile : EntitySetProfile<int, E569Snake>
{
    public static E569Snake? LastPosted;

    public static void Reset() => LastPosted = null;

    public E569SnakeProfile() : base(x => x.Id)
    {
        EntitySetName = "E569Snakes";

        Post = (thing, _) =>
        {
            LastPosted = thing;
            return OhDataResult.SuccessTask<E569Snake>(thing);
        };
    }
}
