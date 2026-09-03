using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #458 — two profiles over the same CLR model type silently UNIONED their model-bound allowlists.
///
/// <c>EntitySetProfile.VisitModelBuilder</c> applies <c>FilterProperties</c>/<c>OrderByProperties</c>/
/// <c>SelectProperties</c>/<c>ExpandProperties</c> through <c>entityType.Filter/OrderBy/Select/Expand</c>,
/// and <c>entityType</c> is the shared per-CLR-TYPE <c>EntityTypeConfiguration&lt;TModel&gt;</c>.
/// <c>ModelBoundQuerySettings</c> is keyed by type; OhData's configuration surface is keyed by
/// entity set. Each set therefore accepted properties its own profile withheld, with responses
/// byte-identical to the correctly-gated case.
///
/// Per-entity-set model-bound settings do not exist in <c>Microsoft.OData.ModelBuilder</c> 2.x — the
/// fluent API is declared only on <c>StructuralTypeConfiguration&lt;T&gt;</c> and
/// <c>PropertyConfiguration</c>, and every <c>GetModelBoundQuerySettings</c> overload in
/// <c>Microsoft.AspNetCore.OData</c> resolves off an <c>IEdmStructuredType</c>, never a navigation
/// source. So the fix refuses the ambiguous configuration at <c>MapOhData()</c>, following the
/// precedent <c>IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap</c> set for <c>Ignore()</c>.
///
/// All four options were measured to dissolve on the pre-fix tree, in both registration orders —
/// the issue had measured only <c>$filter</c> and called the other three plausible.
/// </summary>
public class SiblingEntitySetAllowlistTests
{
    private static async Task<InvalidOperationException> AssertThrowsAtStartup(Action<OhDataBuilder> configure)
    {
        return await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await TestHostBuilder.BuildAsync(configure));
    }

    // ── The control: one profile, the allowlist really gates ─────────────────────

    [Fact]
    public async Task SingleProfile_AllowlistGatesAllFourOptions()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<SibNameOnlyProfile>());

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibNameOnly?$filter=Name eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibNameOnly?$orderby=Name")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibNameOnly?$select=Name")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/odata/SibNameOnly?$filter=Secret eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/odata/SibNameOnly?$orderby=Secret")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/odata/SibNameOnly?$select=Secret")).StatusCode);
    }

    // ── Divergent declarations are refused at startup, in both orders ─────────────

    [Fact]
    public async Task DivergentAllowlists_SameModelType_ThrowsAtStartup()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibNameOnlyProfile>();
            o.AddEntitySetProfile<SibSecretOnlyProfile>();
        });

        Assert.Contains("SibNameOnly", ex.Message);
        Assert.Contains("SibSecretOnly", ex.Message);
        Assert.Contains(nameof(SibEntity), ex.Message);
    }

    [Fact]
    public async Task DivergentAllowlists_ReverseRegistrationOrder_ThrowsAtStartup()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibSecretOnlyProfile>();
            o.AddEntitySetProfile<SibNameOnlyProfile>();
        });

        Assert.Contains("SibNameOnly", ex.Message);
        Assert.Contains("SibSecretOnly", ex.Message);
    }

    // ── Each option is checked independently ─────────────────────────────────────

    [Fact]
    public async Task FilterProperties_DivergentAllowlist_ThrowsNamingThatOption()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibFilterOnlyAProfile>();
            o.AddEntitySetProfile<SibFilterOnlyBProfile>();
        });

        Assert.Contains("$filter", ex.Message);
        Assert.Contains("FilterProperties", ex.Message);
    }

    [Fact]
    public async Task OrderByProperties_DivergentAllowlist_ThrowsNamingThatOption()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibOrderByOnlyAProfile>();
            o.AddEntitySetProfile<SibOrderByOnlyBProfile>();
        });

        Assert.Contains("$orderby", ex.Message);
        Assert.Contains("OrderByProperties", ex.Message);
    }

    [Fact]
    public async Task SelectProperties_DivergentAllowlist_ThrowsNamingThatOption()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibSelectOnlyAProfile>();
            o.AddEntitySetProfile<SibSelectOnlyBProfile>();
        });

        Assert.Contains("$select", ex.Message);
        Assert.Contains("SelectProperties", ex.Message);
    }

    [Fact]
    public async Task ExpandProperties_DivergentAllowlist_ThrowsAtStartup()
    {
        var ex = await AssertThrowsAtStartup(o =>
        {
            o.AddEntitySetProfile<SibExpandAlphaProfile>();
            o.AddEntitySetProfile<SibExpandBetaProfile>();
        });

        Assert.Contains("$expand", ex.Message);
        Assert.Contains("ExpandProperties", ex.Message);
    }

    // ── What must keep working ───────────────────────────────────────────────────

    [Fact]
    public async Task ShippedFixtures_OneModelTypeBehindTwoProfiles_StillRegister()
    {
        // WidgetProfile + QueryableWidgetProfile both expose Widget with the same (unset =
        // permissive) allowlists. Multi-set-per-type is a supported, exercised configuration and
        // the check must not touch it.
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<WidgetProfile>();
            o.AddEntitySetProfile<QueryableWidgetProfile>();
        });

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/Widgets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/QueryableWidgets")).StatusCode);
    }

    [Fact]
    public async Task IdenticalAllowlists_SameModelType_RegisterAndStillGate()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<SibNameOnlyProfile>();
            o.AddEntitySetProfile<SibNameOnlyTwinProfile>();
        });

        foreach (string set in new[] { "SibNameOnly", "SibNameOnlyTwin" })
        {
            Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync($"/odata/{set}?$filter=Name eq 'a'")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync($"/odata/{set}?$filter=Secret eq 'a'")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync($"/odata/{set}?$orderby=Secret")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync($"/odata/{set}?$select=Secret")).StatusCode);
        }
    }

    [Fact]
    public async Task SiblingWithCapabilityDisabled_DoesNotConflict_AndRestrictedSetStillGates()
    {
        // The sibling never calls the model builder for those options, so it contributes nothing to
        // the shared settings and cannot dissolve anything. Its own requests are refused by the
        // capability-flag gate before the EDM is consulted. Flagging this pair would be a false
        // positive that breaks a legitimate configuration.
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<SibNameOnlyProfile>();
            o.AddEntitySetProfile<SibNoQueryOptionsProfile>();
        });

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibNameOnly?$filter=Name eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/odata/SibNameOnly?$filter=Secret eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/odata/SibNoQueryOptions?$filter=Name eq 'a'")).StatusCode);
    }

    [Fact]
    public async Task AdvancedConfigureSibling_DoesNotConflict()
    {
        // AdvancedConfigure ejects before the four call sites and owns the EDM outright, so the
        // framework has no declaration it can honestly compare.
        await using var fx = await TestHostBuilder.BuildAsync(o =>
        {
            o.AddEntitySetProfile<SibNameOnlyProfile>();
            o.AddEntitySetProfile<SibAdvancedConfigureProfile>();
        });

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibNameOnly")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/odata/SibAdvanced")).StatusCode);
    }

    [Fact]
    public async Task SeparateRegistrations_AreNotAffected()
    {
        // Each registration builds its own ODataConventionModelBuilder, so divergent allowlists
        // across registrations were never unioned and must not be rejected.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddOhData("v1", o => o.WithPrefix("/v1").AddEntitySetProfile<SibNameOnlyProfile>());
        builder.Services.AddOhData("v2", o => o.WithPrefix("/v2").AddEntitySetProfile<SibSecretOnlyProfile>());
        await using var app = builder.Build();
        app.MapOhData("v1");
        app.MapOhData("v2");
        await app.StartAsync();
        using var client = ((Microsoft.Extensions.Hosting.IHost)app).GetTestClient();
        var fx = new { Client = client };

        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/v1/SibNameOnly?$filter=Name eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/v1/SibNameOnly?$filter=Secret eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client.GetAsync("/v2/SibSecretOnly?$filter=Secret eq 'a'")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.GetAsync("/v2/SibSecretOnly?$filter=Name eq 'a'")).StatusCode);
    }
}

// ── Fixtures ─────────────────────────────────────────────────────────────────────

internal class SibEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
}

internal abstract class SibProfileBase : EntitySetProfile<int, SibEntity>
{
    private static readonly List<SibEntity> Data = new() { new() { Id = 1, Name = "a", Secret = "s" } };

    protected SibProfileBase() : base(x => x.Id)
    {
        GetQueryable = ct => OhDataResult.SuccessTask(Data.AsQueryable());
    }
}

internal class SibNameOnlyProfile : SibProfileBase
{
    public SibNameOnlyProfile()
    {
        EntitySetName = "SibNameOnly";
        FilterEnabled = true; OrderByEnabled = true; SelectEnabled = true;
        FilterProperties(x => x.Name);
        OrderByProperties(x => x.Name);
        SelectProperties(x => x.Name);
    }
}

internal class SibNameOnlyTwinProfile : SibProfileBase
{
    public SibNameOnlyTwinProfile()
    {
        EntitySetName = "SibNameOnlyTwin";
        FilterEnabled = true; OrderByEnabled = true; SelectEnabled = true;
        FilterProperties(x => x.Name);
        OrderByProperties(x => x.Name);
        SelectProperties(x => x.Name);
    }
}

internal class SibSecretOnlyProfile : SibProfileBase
{
    public SibSecretOnlyProfile()
    {
        EntitySetName = "SibSecretOnly";
        FilterEnabled = true; OrderByEnabled = true; SelectEnabled = true;
        FilterProperties(x => x.Secret);
        OrderByProperties(x => x.Secret);
        SelectProperties(x => x.Secret);
    }
}

internal class SibNoQueryOptionsProfile : SibProfileBase
{
    public SibNoQueryOptionsProfile() => EntitySetName = "SibNoQueryOptions";
}

internal class SibAdvancedConfigureProfile : SibProfileBase
{
    public SibAdvancedConfigureProfile() => EntitySetName = "SibAdvanced";

    protected override void AdvancedConfigure(Microsoft.OData.ModelBuilder.EntitySetConfiguration<SibEntity> config)
    {
        config.EntityType.HasKey(x => x.Id);
    }
}

// One option each, so the message can be asserted to name the right one.

internal class SibFilterOnlyAProfile : SibProfileBase
{
    public SibFilterOnlyAProfile()
    {
        EntitySetName = "SibFilterA";
        FilterEnabled = true;
        FilterProperties(x => x.Name);
    }
}

internal class SibFilterOnlyBProfile : SibProfileBase
{
    public SibFilterOnlyBProfile()
    {
        EntitySetName = "SibFilterB";
        FilterEnabled = true;
        FilterProperties(x => x.Secret);
    }
}

internal class SibOrderByOnlyAProfile : SibProfileBase
{
    public SibOrderByOnlyAProfile()
    {
        EntitySetName = "SibOrderByA";
        OrderByEnabled = true;
        OrderByProperties(x => x.Name);
    }
}

internal class SibOrderByOnlyBProfile : SibProfileBase
{
    public SibOrderByOnlyBProfile()
    {
        EntitySetName = "SibOrderByB";
        OrderByEnabled = true;
        OrderByProperties(x => x.Secret);
    }
}

internal class SibSelectOnlyAProfile : SibProfileBase
{
    public SibSelectOnlyAProfile()
    {
        EntitySetName = "SibSelectA";
        SelectEnabled = true;
        SelectProperties(x => x.Name);
    }
}

internal class SibSelectOnlyBProfile : SibProfileBase
{
    public SibSelectOnlyBProfile()
    {
        EntitySetName = "SibSelectB";
        SelectEnabled = true;
        SelectProperties(x => x.Secret);
    }
}

internal class SibKid { public int Id { get; set; } }

internal class SibExpandEntity
{
    public int Id { get; set; }
    public IEnumerable<SibKid>? Alpha { get; set; }
    public IEnumerable<SibKid>? Beta { get; set; }
}

internal abstract class SibExpandProfileBase : EntitySetProfile<int, SibExpandEntity>
{
    private static readonly List<SibExpandEntity> Data = new() { new() { Id = 1 } };

    protected SibExpandProfileBase() : base(x => x.Id)
    {
        ExpandEnabled = true;
        GetQueryable = ct => OhDataResult.SuccessTask(Data.AsQueryable());
        HasMany(x => x.Alpha!, getAll: (id, ct) => Task.FromResult<IEnumerable<SibKid>>(new List<SibKid>()));
        HasMany(x => x.Beta!, getAll: (id, ct) => Task.FromResult<IEnumerable<SibKid>>(new List<SibKid>()));
    }
}

internal class SibExpandAlphaProfile : SibExpandProfileBase
{
    public SibExpandAlphaProfile()
    {
        EntitySetName = "SibExpandAlpha";
        ExpandProperties(x => x.Alpha!);
    }
}

internal class SibExpandBetaProfile : SibExpandProfileBase
{
    public SibExpandBetaProfile()
    {
        EntitySetName = "SibExpandBeta";
        ExpandProperties(x => x.Beta!);
    }
}
