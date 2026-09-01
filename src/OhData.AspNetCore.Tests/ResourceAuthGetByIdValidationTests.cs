using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #486: <c>.RequireResource()</c> on the <b>Create</b> or <b>Invoke</b> category, on a profile with
/// no <c>GetById</c>, passed startup validation and then failed 100% of requests with a 500.
///
/// <para>
/// The #199 Layer B guard threw only for Read/Update/Delete, but <c>AttachResourceFilter</c> also
/// attaches for Create — on the key-based navigation-POST route — and for Invoke, on entity-bound
/// functions and actions. The filter calls <c>InvokeGetByIdAsync</c>, i.e. <c>GetById!.Invoke(...)</c>,
/// so a null handler is a <c>NullReferenceException</c> on every request, surfacing as the generic
/// 500 envelope. It fails <i>closed</i> — nothing is exposed — but it contradicts the guard's own
/// fail-fast intent: the guard exists precisely so this configuration cannot reach runtime, and it
/// covered three of the five categories that can trigger it.
/// </para>
/// </summary>
public class ResourceAuthGetByIdValidationTests
{
    [Fact]
    public async Task ResourceCreate_OnAKeyBasedNavigationPostRoute_WithoutGetById_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<RagNavCreateNoGetByIdProfile>()));

        Assert.Contains("RagNavCreate", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetById", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResourceInvoke_OnAnEntityBoundFunction_WithoutGetById_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<RagInvokeFnNoGetByIdProfile>()));

        Assert.Contains("RagInvokeFn", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetById", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule scoped to ONE bound operation (<c>Invoke("Name", …)</c>) reaches the same filter, so
    /// the guard has to resolve the rule per operation name rather than only asking the generic
    /// category question.
    /// </summary>
    [Fact]
    public async Task ResourceInvoke_NamedRuleOnAnEntityBoundAction_WithoutGetById_ThrowsAtStartup()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ResourceAuthTestHost.BuildAsync(o => o.AddEntitySetProfile<RagInvokeNamedNoGetByIdProfile>()));

        Assert.Contains("RagInvokeNamed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stamp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetById", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Control. The COLLECTION POST is not key-based: its Create resource check runs inline against
    /// the deserialized model, never through <c>InvokeGetByIdAsync</c>. So Create + RequireResource
    /// with no key-based create route and no GetById is a working configuration and must keep
    /// starting — the guard must key off the routes that actually attach the filter.
    /// </summary>
    [Fact]
    public async Task ResourceCreate_OnTheCollectionPostOnly_WithoutGetById_StillStarts()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<RagCollectionCreateNoGetByIdProfile>());

        using HttpResponseMessage metadata = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }

    /// <summary>
    /// Control. A COLLECTION-level bound operation is not key-based either, so Invoke +
    /// RequireResource without GetById is legal when no entity-bound operation exists.
    /// </summary>
    [Fact]
    public async Task ResourceInvoke_OnCollectionLevelOperationsOnly_WithoutGetById_StillStarts()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<RagCollectionInvokeNoGetByIdProfile>());

        using HttpResponseMessage metadata = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }

    /// <summary>Control: the same two shapes WITH a GetById handler are exactly what the feature is
    /// for, and must keep starting.</summary>
    [Fact]
    public async Task ResourceCreateAndInvoke_WithGetById_StillStart()
    {
        await using TestFixture fx = await ResourceAuthTestHost.BuildAsync(
            o => o.AddEntitySetProfile<RagKeyedWithGetByIdProfile>());

        using HttpResponseMessage metadata = await fx.Client.GetAsync("/odata/$metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

internal class RagParent
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    public List<RagChild> Notes { get; set; } = new();
}

internal class RagChild
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>#486: Create + RequireResource on the key-based navigation-POST route, no GetById.</summary>
internal sealed class RagNavCreateNoGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagNavCreateNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagNavCreate";
        HasMany(x => x.Notes,
            getAll: null,
            post: (key, child, ct) => Task.FromResult<RagChild?>(child));
        ConfigureAuthorization(a => a.Create(c => c.RequireResource()));
    }
}

/// <summary>#486: Invoke + RequireResource on an entity-bound function, no GetById.</summary>
internal sealed class RagInvokeFnNoGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagInvokeFnNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagInvokeFn";
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Tag(int key) => Task.FromResult("tag");
}

/// <summary>#486: a NAME-scoped Invoke rule on an entity-bound action, no GetById.</summary>
internal sealed class RagInvokeNamedNoGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagInvokeNamedNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagInvokeNamed";
        BindEntityAction(Stamp);
        ConfigureAuthorization(a => a.Invoke("Stamp", i => i.RequireResource()));
    }

    private Task<string> Stamp(int key) => Task.FromResult("stamped");
}

/// <summary>Control: Create + RequireResource with only the collection POST, no GetById.</summary>
internal sealed class RagCollectionCreateNoGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagCollectionCreateNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagCollectionCreate";
        Post = (m, ct) => Task.FromResult<RagParent?>(m);
        ConfigureAuthorization(a => a.Create(c => c.RequireResource()));
    }
}

/// <summary>Control: Invoke + RequireResource with only collection-level operations, no GetById.</summary>
internal sealed class RagCollectionInvokeNoGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagCollectionInvokeNoGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagCollectionInvoke";
        BindFunction(Summary);
        ConfigureAuthorization(a => a.Invoke(i => i.RequireResource()));
    }

    private Task<string> Summary() => Task.FromResult("ok");
}

/// <summary>Control: both key-based shapes, WITH the GetById the filter needs.</summary>
internal sealed class RagKeyedWithGetByIdProfile : EntitySetProfile<int, RagParent>
{
    public RagKeyedWithGetByIdProfile() : base(x => x.Id)
    {
        EntitySetName = "RagKeyed";
        GetById = (id, ct) => Task.FromResult<RagParent?>(new RagParent { Id = id });
        HasMany(x => x.Notes,
            getAll: null,
            post: (key, child, ct) => Task.FromResult<RagChild?>(child));
        BindEntityFunction(Tag);
        ConfigureAuthorization(a => a
            .Create(c => c.RequireResource())
            .Invoke(i => i.RequireResource()));
    }

    private Task<string> Tag(int key) => Task.FromResult("tag");
}
