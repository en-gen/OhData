using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OhData.AspNetCore.Tests;

internal sealed class ActRetItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// #539/#543 fixture. <c>MaxTop = 10</c> over a 25-row store, with a bound ACTION and a bound
/// FUNCTION declared over the same shapes so the two can be compared on the wire.
///
/// <para>Every <c>BindAction</c> here declaring <see cref="ActRetItem"/> or a collection of
/// it is #539: before the fix <c>MapOhData()</c> threw from
/// <c>Microsoft.OData.ModelBuilder</c>'s <c>ActionConfiguration.Returns</c>/<c>.ReturnsCollection</c>
/// — "The EDM type '…' is already declared as an entity type. Use the method 'ReturnsFromEntitySet'
/// …" — naming a method OhData never called and did not expose, so this whole fixture failed to
/// build.</para>
/// </summary>
/// <summary>
/// #539 floor: a second entity type, so a bound operation can declare a return of an entity type
/// that IS in the registration but is NOT the declaring profile's own model. That is the one shape
/// the #539 fix deliberately does not make work — OhData can only bind an operation's entity return
/// to the entity set of the profile that declares it — so it must fail with OhData's own message
/// rather than Microsoft's, which names a method OhData does not expose.
/// </summary>
internal sealed class ActRetOther
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

internal sealed class ActRetOtherProfile : EntitySetProfile<int, ActRetOther>
{
    public ActRetOtherProfile() : base(x => x.Id)
    {
        EntitySetName = "ActRetOthers";
        GetAll = _ => Task.FromResult<IEnumerable<ActRetOther>>(new List<ActRetOther>());
    }
}

/// <summary>Its bound action returns the OTHER profile's model type.</summary>
internal sealed class ActRetForeignReturnProfile : EntitySetProfile<int, ActRetItem>
{
    public ActRetForeignReturnProfile() : base(x => x.Id)
    {
        EntitySetName = "ActRetForeign";
        GetAll = _ => Task.FromResult<IEnumerable<ActRetItem>>(new List<ActRetItem>());
        BindAction(Borrow);
    }

    private Task<ActRetOther> Borrow() => Task.FromResult(new ActRetOther());
}

internal sealed class ActRetProfile : EntitySetProfile<int, ActRetItem>
{
    internal static readonly List<ActRetItem> Store =
        Enumerable.Range(1, 25).Select(i => new ActRetItem { Id = i, Name = $"item-{i}" }).ToList();

    public ActRetProfile() : base(x => x.Id)
    {
        EntitySetName = "ActRetItems";
        MaxTop = 10;

        GetAll = ct => Task.FromResult<IEnumerable<ActRetItem>>(Store);
        GetById = (id, ct) => Task.FromResult(Store.FirstOrDefault(x => x.Id == id));

        // #539: a bound ACTION declaring the entity set's own type, collection and single.
        BindAction(Dump);           // POST /ActRetItems/Dump         -> IEnumerable<TModel>
        BindAction(Head);           // POST /ActRetItems/Head         -> TModel
        BindEntityAction(Siblings); // POST /ActRetItems(1)/Siblings  -> IEnumerable<TModel>

        // #543: declared Task<object>, returns List<TModel> at runtime — the shape the issue
        // measured, and the one that registered even before #539.
        BindAction(DumpAsObject);   // POST /ActRetItems/DumpAsObject -> object

        // Control: the FUNCTION twins, whose behaviour must not move.
        BindFunction(DumpFn);       // GET  /ActRetItems/DumpFn       -> IEnumerable<TModel>
        BindFunction(HeadFn);       // GET  /ActRetItems/HeadFn       -> TModel
    }

    private Task<IEnumerable<ActRetItem>> Dump() => Task.FromResult<IEnumerable<ActRetItem>>(Store);
    private Task<ActRetItem?> Head() => Task.FromResult<ActRetItem?>(Store[0]);
    private Task<IEnumerable<ActRetItem>> Siblings(int key) =>
        Task.FromResult<IEnumerable<ActRetItem>>(Store.Where(x => x.Id != key));
    private Task<object> DumpAsObject() => Task.FromResult<object>(Store.ToList());
    private Task<IEnumerable<ActRetItem>> DumpFn() => Task.FromResult<IEnumerable<ActRetItem>>(Store);
    private Task<ActRetItem?> HeadFn() => Task.FromResult<ActRetItem?>(Store[0]);
}

/// <summary>#543 opt-out fixture: <c>MaxTop = null</c> is the documented "this set has no ceiling"
/// declaration, and it must opt an action out exactly as it opts a function out.</summary>
internal sealed class ActRetUnboundedProfile : EntitySetProfile<int, ActRetItem>
{
    private static readonly List<ActRetItem> _store =
        Enumerable.Range(1, 25).Select(i => new ActRetItem { Id = i, Name = $"item-{i}" }).ToList();

    public ActRetUnboundedProfile() : base(x => x.Id)
    {
        EntitySetName = "ActRetUnbounded";
        MaxTop = null;

        GetAll = ct => Task.FromResult<IEnumerable<ActRetItem>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
        BindAction(Dump);
    }

    private Task<IEnumerable<ActRetItem>> Dump() => Task.FromResult<IEnumerable<ActRetItem>>(_store);
}

/// <summary>#543 byte-identity control: three rows, well under the cap, so the response must not
/// move by one byte and no header may appear that was not there before.</summary>
internal sealed class ActRetTinyProfile : EntitySetProfile<int, ActRetItem>
{
    private static readonly List<ActRetItem> _store =
        Enumerable.Range(1, 3).Select(i => new ActRetItem { Id = i, Name = $"item-{i}" }).ToList();

    public ActRetTinyProfile() : base(x => x.Id)
    {
        EntitySetName = "ActRetTiny";
        MaxTop = 10;

        GetAll = ct => Task.FromResult<IEnumerable<ActRetItem>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(x => x.Id == id));
        BindAction(Dump);
        BindFunction(DumpFn);
    }

    private Task<IEnumerable<ActRetItem>> Dump() => Task.FromResult<IEnumerable<ActRetItem>>(_store);
    private Task<IEnumerable<ActRetItem>> DumpFn() => Task.FromResult<IEnumerable<ActRetItem>>(_store);
}

/// <summary>
/// <b>#539</b> — <c>BindAction</c> could not return <c>TModel</c> or <c>IEnumerable&lt;TModel&gt;</c>
/// at all: <c>Microsoft.OData.ModelBuilder</c>'s <c>ActionConfiguration.Returns</c> /
/// <c>.ReturnsCollection</c> refuse a CLR type already declared as an entity type and direct the
/// caller to <c>ReturnsFromEntitySet</c> / <c>ReturnsCollectionFromEntitySet</c>, which OhData never
/// called and does not expose. The <c>FunctionConfiguration</c> twins accept the same type.
///
/// <para><b>#543</b> — and because a bound action could still reach the collection branch of
/// <c>WrapBoundOpResult</c> through a <c>Task&lt;object&gt;</c> declaration, <c>MaxTop</c> was fully
/// bypassable: 25 rows served against a ceiling of 10, with <c>$top</c> neither applied nor
/// rejected. Fixing #539 turns that from an odd corner into the ordinary shape.</para>
/// </summary>
public class BoundActionEntityReturnTests
{
    private static Task<TestFixture> BuildAsync() =>
        TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ActRetProfile>());

    private static StringContent EmptyBody() => new("{}", Encoding.UTF8, "application/json");

    private static int[] Ids(JsonElement json) =>
        json.GetProperty("value").EnumerateArray().Select(e => e.GetProperty("Id").GetInt32()).ToArray();

    private static async Task<JsonElement> PostJsonAsync(TestFixture fx, string url)
    {
        HttpResponseMessage r = await fx.Client.PostAsync(url, EmptyBody());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
    }

    // ── #539: the action can be registered at all ────────────────────────────────

    /// <summary>
    /// The whole fixture failing to build IS #539: before the fix this threw
    /// <c>InvalidOperationException</c> out of <c>MapOhData()</c> with Microsoft's message.
    /// </summary>
    [Fact]
    public async Task BindAction_ReturningTheSetsOwnType_Registers()
    {
        await using TestFixture fx = await BuildAsync();

        HttpResponseMessage collection = await fx.Client.PostAsync("/odata/ActRetItems/Dump?$top=3", EmptyBody());
        Assert.Equal(HttpStatusCode.OK, collection.StatusCode);

        HttpResponseMessage single = await fx.Client.PostAsync("/odata/ActRetItems/Head", EmptyBody());
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);

        HttpResponseMessage entityLevel = await fx.Client.PostAsync("/odata/ActRetItems(1)/Siblings?$top=3", EmptyBody());
        Assert.Equal(HttpStatusCode.OK, entityLevel.StatusCode);
    }

    /// <summary>
    /// The EDM really carries the declared return type — the point of calling
    /// <c>ReturnsCollectionFromEntitySet</c> rather than merely swallowing the failure.
    /// </summary>
    [Fact]
    public async Task BindAction_ReturningTheSetsOwnType_IsDeclaredInMetadata()
    {
        await using TestFixture fx = await BuildAsync();
        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");

        Assert.Contains("<Action Name=\"Dump\"", csdl);
        Assert.Contains("<Action Name=\"Head\"", csdl);
        Assert.Contains("<Action Name=\"Siblings\"", csdl);
        // The collection action declares Collection(<model>), the single action declares <model>.
        string model = typeof(ActRetItem).FullName!;
        Assert.Contains($"<ReturnType Type=\"Collection({model})\" />", csdl);
        Assert.Contains($"<ReturnType Type=\"{model}\" />", csdl);
    }

    /// <summary>
    /// The single-entity action rides the same branch as a single-entity function: bare entity, no
    /// collection envelope, no continuation.
    /// </summary>
    [Fact]
    public async Task BindAction_ReturningASingleEntity_IsServedAsAnEntity()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync("/odata/ActRetItems/Head", EmptyBody());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        Assert.Equal(1, json.GetProperty("Id").GetInt32());
        Assert.False(json.TryGetProperty("value", out _));
    }

    // ── #543: the ceiling is enforced on a bound ACTION ──────────────────────────

    /// <summary>
    /// The headline. 25 rows, <c>MaxTop = 10</c>, no <c>$top</c>: the framework cannot truncate
    /// silently (M1) and cannot offer a <c>@odata.nextLink</c> (a nextLink is a URL the client
    /// GETs, §11.2.5.7, while an action is invoked by POST to its action URL, §11.5.4.1 — there
    /// is no GET-addressable continuation of an action invocation), so it refuses. A handler whose result cannot be
    /// served within the ceiling the profile itself declared is a server-side contract violation,
    /// which #496 already settled is a logged <c>500</c> and not a <c>400</c> blaming the client.
    /// </summary>
    [Fact]
    public async Task BoundAction_CollectionOverTheCeiling_IsRefused_NotSilentlyServed()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync("/odata/ActRetItems/Dump", EmptyBody());

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        Assert.Equal("InternalServerError", json.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>The <c>Task&lt;object&gt;</c> declaration the issue measured — the runtime branch is
    /// what decides, so it is bounded identically.</summary>
    [Fact]
    public async Task BoundAction_DeclaredAsObject_IsBoundedToo()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync("/odata/ActRetItems/DumpAsObject", EmptyBody());
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    /// <summary>Entity-level bound actions take the same route through the same wrapper.</summary>
    [Fact]
    public async Task EntityLevelBoundAction_CollectionOverTheCeiling_IsRefused()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync("/odata/ActRetItems(1)/Siblings", EmptyBody());
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    /// <summary>An explicit <c>$top</c> above <c>MaxTop</c> is the same 400, with the same message
    /// byte for byte, that the collection GET and the bound function already produce.</summary>
    [Fact]
    public async Task BoundAction_ExplicitTopAboveMaxTop_Is400_WithTheSharedMessage()
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync("/odata/ActRetItems/Dump?$top=999", EmptyBody());

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        JsonElement error = json.GetProperty("error");
        Assert.Equal("InvalidQueryOption", error.GetProperty("code").GetString());
        Assert.Equal(
            "The value of '$top' (999) exceeds the maximum allowed value (10).",
            error.GetProperty("message").GetString());

        // The identical condition on the collection GET produces the identical envelope.
        HttpResponseMessage viaGet = await fx.Client.GetAsync("/odata/ActRetItems?$top=999");
        Assert.Equal(HttpStatusCode.BadRequest, viaGet.StatusCode);
        Assert.Equal(
            error.GetProperty("message").GetString(),
            JsonSerializer.Deserialize<JsonElement>(await viaGet.Content.ReadAsStringAsync())
                .GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary>An explicit <c>$top</c> at or below the ceiling is applied: the client set the bound
    /// itself, so there is nothing silent about the truncation and nothing to continue.</summary>
    [Fact]
    public async Task BoundAction_ExplicitTopWithinMaxTop_IsApplied_AndCarriesNoNextLink()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await PostJsonAsync(fx, "/odata/ActRetItems/Dump?$top=5");

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Ids(json));
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary><c>$skip</c> is applied, and a page the skip brings under the ceiling is served.</summary>
    [Fact]
    public async Task BoundAction_Skip_IsApplied()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await PostJsonAsync(fx, "/odata/ActRetItems/Dump?$skip=20");

        Assert.Equal(new[] { 21, 22, 23, 24, 25 }, Ids(json));
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary>A malformed <c>$top</c>/<c>$skip</c> is rejected rather than dropped, with the same
    /// wording the bound function and the navigation-collection route use.</summary>
    [Theory]
    [InlineData("$top=abc", "$top")]
    [InlineData("$top=-1", "$top")]
    [InlineData("$skip=abc", "$skip")]
    [InlineData("$skip=-1", "$skip")]
    public async Task BoundAction_MalformedTopOrSkip_Is400(string query, string option)
    {
        await using TestFixture fx = await BuildAsync();
        HttpResponseMessage r = await fx.Client.PostAsync($"/odata/ActRetItems/Dump?{query}", EmptyBody());

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        Assert.Equal("InvalidQueryOption", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains($"The value of '{option}'", json.GetProperty("error").GetProperty("message").GetString());
    }

    /// <summary><c>MaxTop = null</c> opts an action out exactly as it opts a function out.</summary>
    [Fact]
    public async Task MaxTopNull_OptsTheActionOut()
    {
        await using TestFixture fx =
            await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ActRetUnboundedProfile>());

        JsonElement json = await PostJsonAsync(fx, "/odata/ActRetUnbounded/Dump");
        Assert.Equal(25, json.GetProperty("value").GetArrayLength());
        Assert.False(json.TryGetProperty("@odata.nextLink", out _));
    }

    /// <summary>
    /// A result already under the ceiling does not move by one byte, and no <c>Preference-Applied</c>
    /// appears — <c>Prefer: maxpagesize</c> is a server-driven-paging preference (RFC 7240 advisory)
    /// and an action has no paging to drive, so it is deliberately not honoured.
    /// </summary>
    [Fact]
    public async Task ResultUnderTheCeiling_IsUnchanged_AndPreferMaxPageSizeIsNotHonoured()
    {
        await using TestFixture fx =
            await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ActRetTinyProfile>());

        HttpResponseMessage plain = await fx.Client.PostAsync("/odata/ActRetTiny/Dump", EmptyBody());
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        string plainBody = await plain.Content.ReadAsStringAsync();

        using var withPrefer = new HttpRequestMessage(HttpMethod.Post, "/odata/ActRetTiny/Dump")
        {
            Content = EmptyBody(),
        };
        withPrefer.Headers.TryAddWithoutValidation("Prefer", "maxpagesize=2");
        HttpResponseMessage preferred = await fx.Client.SendAsync(withPrefer);

        Assert.Equal(HttpStatusCode.OK, preferred.StatusCode);
        Assert.Equal(plainBody, await preferred.Content.ReadAsStringAsync());
        Assert.False(preferred.Headers.Contains("Preference-Applied"));
        Assert.DoesNotContain("@odata.nextLink", plainBody);
    }


    // ── #539 floor: the shape the fix deliberately does NOT make work ────────────────────────────
    //
    // Codecov flagged this throw as uncovered, and it is the one path in this PR that produces a
    // message rather than a behaviour. An untested throw in a PR about throws is the gap #528 had.

    [Fact]
    public async Task BoundAction_ReturningAnotherProfilesModelType_ThrowsOhDatasOwnMessage()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(o =>
            {
                o.AddEntitySetProfile<ActRetOtherProfile>();
                o.AddEntitySetProfile<ActRetForeignReturnProfile>();
            }));

        // The whole point is that the developer is not handed ModelBuilder's vocabulary.
        string message = ex.ToString();
        Assert.Contains("ActRetForeign", message, StringComparison.Ordinal);
        Assert.Contains("Borrow", message, StringComparison.Ordinal);
        Assert.Contains("ActRetOther", message, StringComparison.Ordinal);
        Assert.Contains("this profile's own model type", message, StringComparison.Ordinal);

        // Microsoft's original is kept as the inner exception rather than discarded.
        Assert.Contains("already declared as an entity type", message, StringComparison.Ordinal);
    }

    // ── The FUNCTION half must not move ──────────────────────────────────────────

    /// <summary>#357's function behaviour is unchanged: capped at <c>MaxTop</c> with a
    /// <c>@odata.nextLink</c> continuation, which is exactly what an action cannot have.</summary>
    [Fact]
    public async Task BoundFunction_StillCapsWithANextLink()
    {
        await using TestFixture fx = await BuildAsync();
        JsonElement json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/ActRetItems/DumpFn");

        Assert.Equal(10, json.GetProperty("value").GetArrayLength());
        Assert.Contains("%24skip=10", json.GetProperty("@odata.nextLink").GetString());
    }

    /// <summary>A bound function's <c>$metadata</c> declaration is byte-identical after the switch to
    /// <c>ReturnsFromEntitySet</c>: the CSDL a bound operation emits is the same either way, so the
    /// unification that made actions work moves nothing on the function side.</summary>
    [Fact]
    public async Task BoundFunction_MetadataDeclarationIsUnchanged()
    {
        await using TestFixture fx = await BuildAsync();
        string csdl = await fx.Client.GetStringAsync("/odata/$metadata");
        string model = typeof(ActRetItem).FullName!;

        Assert.Contains(
            $"<Function Name=\"DumpFn\" IsBound=\"true\">\r\n        <Parameter Name=\"bindingParameter\" Type=\"Collection({model})\" />\r\n        <ReturnType Type=\"Collection({model})\" />\r\n      </Function>"
                .Replace("\r\n", "\n"),
            csdl.Replace("\r\n", "\n"));
        Assert.Contains(
            $"<Function Name=\"HeadFn\" IsBound=\"true\">\r\n        <Parameter Name=\"bindingParameter\" Type=\"Collection({model})\" />\r\n        <ReturnType Type=\"{model}\" />\r\n      </Function>"
                .Replace("\r\n", "\n"),
            csdl.Replace("\r\n", "\n"));
    }
}
