using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #313 stage 3: the ExpandPagingEnabled knob and the startup diagnostic that replaces the arbitrary
// MaxExpandTop default stage 1 removed.
//
// NOTHING reads the flag yet except the diagnostic — no route registration, no annotation, no change
// to any request's behaviour. What this file pins is (a) that the flag resolves across all three of
// its states, including the per-profile opt-out that is the entire reason it is bool? and not a second
// int?, (b) that the diagnostic fires for exactly the navigations that are actually exposed, and (c)
// that setting the flag either way changes no response byte.

// ── Resolution ───────────────────────────────────────────────────────────────────────────────────

public sealed class EpeModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class ExpandPagingEnabledResolutionTests
{
    private sealed class SilentProfile : EntitySetProfile<int, EpeModel>
    {
        public SilentProfile() : base(x => x.Id) { }
    }

    private sealed class OptInProfile : EntitySetProfile<int, EpeModel>
    {
        public OptInProfile() : base(x => x.Id) { ExpandPagingEnabled = true; }
    }

    private sealed class OptOutProfile : EntitySetProfile<int, EpeModel>
    {
        public OptOutProfile() : base(x => x.Id) { ExpandPagingEnabled = false; }
    }

    private static void Seal(IVisitModelBuilder profile, EntitySetDefaults? defaults = null) =>
        profile.VisitModelBuilder(
            new Microsoft.OData.ModelBuilder.ODataConventionModelBuilder(),
            defaults ?? new EntitySetDefaults());

    [Fact]
    public void ExpandPagingEnabled_DefaultsToFalse()
    {
        // A continuation link is WORSE than a 400 for a client that does not read nested annotations —
        // it turns a loud failure into a silently truncated collection that looks complete. So paging
        // is opt-in even once MaxExpandTop is set, and this is the default that makes that true.
        Assert.False(new EntitySetDefaults().ExpandPagingEnabled);

        var profile = new SilentProfile();
        Seal(profile);
        Assert.False(((IEntitySetEndpointSource)profile).ExpandPagingEnabled);
    }

    [Fact]
    public void ExpandPagingEnabled_ProfileTrue_WinsOverFalseDefault()
    {
        var profile = new OptInProfile();
        Seal(profile, new EntitySetDefaults { ExpandPagingEnabled = false });
        Assert.True(((IEntitySetEndpointSource)profile).ExpandPagingEnabled);
    }

    [Fact]
    public void ExpandPagingEnabled_WithDefaultsOverride_AppliesWhenProfileSilent()
    {
        var profile = new SilentProfile();
        Seal(profile, new EntitySetDefaults { ExpandPagingEnabled = true });
        Assert.True(((IEntitySetEndpointSource)profile).ExpandPagingEnabled);
    }

    /// <summary>
    /// The case the <c>bool?</c> exists for, and the one that decided K3 over K2 (two numbers).
    /// A profile-level <c>false</c> opts OUT of a server-wide <c>ExpandPagingEnabled = true</c>.
    /// </summary>
    [Fact]
    public void ExpandPagingEnabled_ProfileFalse_OptsOutOfAnEnablingDefault()
    {
        var profile = new OptOutProfile();
        Seal(profile, new EntitySetDefaults { ExpandPagingEnabled = true });
        Assert.False(((IEntitySetEndpointSource)profile).ExpandPagingEnabled);
    }

    /// <summary>
    /// The contrast that makes the previous test meaningful: a profile that says nothing INHERITS the
    /// enabling default. Silence and an explicit <c>false</c> are therefore distinguishable, which is
    /// precisely what <c>MaxExpandTop</c>'s <c>int?</c> cannot do — there a profile-level <c>null</c>
    /// means "inherit", so no profile can opt out of a ceiling set in the defaults
    /// (<see cref="MaxExpandTopResolutionTests.MaxExpandTop_NullProfileValue_InheritsDefault_NotUncapped"/>).
    /// A second page-size <c>int?</c> would have inherited that trap; a <c>bool?</c> does not.
    /// </summary>
    [Fact]
    public void ExpandPagingEnabled_ProfileSilent_IsDistinguishableFromProfileFalse()
    {
        var silent = new SilentProfile();
        Seal(silent, new EntitySetDefaults { ExpandPagingEnabled = true });

        var optedOut = new OptOutProfile();
        Seal(optedOut, new EntitySetDefaults { ExpandPagingEnabled = true });

        Assert.True(((IEntitySetEndpointSource)silent).ExpandPagingEnabled);
        Assert.False(((IEntitySetEndpointSource)optedOut).ExpandPagingEnabled);
    }

    /// <summary>
    /// The premise the startup diagnostic's targeting claim rests on: a registration that never opts
    /// into <c>$expand</c> cannot expose an unbounded child collection, and gets no warning at all.
    /// Asserted here rather than assumed, because if this default ever moved the diagnostic would go
    /// from near-silent to firing on essentially every model with a collection navigation.
    /// </summary>
    [Fact]
    public void ExpandEnabled_DefaultsToFalse_WhichIsWhatKeepsTheDiagnosticQuiet()
    {
        Assert.False(new EntitySetDefaults().ExpandEnabled);

        var profile = new SilentProfile();
        Seal(profile);
        Assert.False(((IEntitySetEndpointSource)profile).ExpandEnabled);
    }
}

// ── The startup diagnostic ───────────────────────────────────────────────────────────────────────

public sealed class EpdParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<EpdChild> Children { get; set; } = new();
    public EpdOwner? Owner { get; set; }
    public int? OwnerId { get; set; }
}

public sealed class EpdChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class EpdOwner
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class EpdDbContext : DbContext
{
    public EpdDbContext(DbContextOptions<EpdDbContext> options) : base(options) { }

    public DbSet<EpdParent> Parents => Set<EpdParent>();
    public DbSet<EpdChild> Children => Set<EpdChild>();
    public DbSet<EpdOwner> Owners => Set<EpdOwner>();
}

/// <summary>Every condition met: the diagnostic must fire, for <c>Children</c> and only for it.</summary>
public sealed class EpdExposedProfile : EntitySetProfile<int, EpdParent>
{
    public EpdExposedProfile(EpdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "EpdParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Parents.AsQueryable());
        HasMany(x => x.Children);      // collection, delegate-less → warns
        HasOptional(x => x.Owner!);    // single-valued, delegate-less → at most one row, never warns
    }
}

/// <summary>Identical but with <c>$expand</c> off — the condition that silences the whole registration.</summary>
public sealed class EpdNoExpandProfile : EntitySetProfile<int, EpdParent>
{
    public EpdNoExpandProfile(EpdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "EpdParents";
        GetQueryable = _ => Task.FromResult(db.Parents.AsQueryable());
        HasMany(x => x.Children);
    }
}

/// <summary>Identical but served from <c>GetAll</c>, not <c>GetQueryable</c> — nothing pushes down.</summary>
public sealed class EpdGetAllProfile : EntitySetProfile<int, EpdParent>
{
    public EpdGetAllProfile(EpdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "EpdParents";
        ExpandEnabled = true;
        GetAll = _ => Task.FromResult<IEnumerable<EpdParent>>(db.Parents.ToList());
        HasMany(x => x.Children);
    }
}

/// <summary>Identical but the collection navigation carries its own delegate — never in the engaged tree.</summary>
public sealed class EpdDelegateBackedProfile : EntitySetProfile<int, EpdParent>
{
    public EpdDelegateBackedProfile(EpdDbContext db) : base(x => x.Id)
    {
        EntitySetName = "EpdParents";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Parents.AsQueryable());
        HasMany(x => x.Children, (key, _) =>
            Task.FromResult<IEnumerable<EpdChild>>(db.Children.Where(c => c.ParentId == key).ToList()));
    }
}

public class ExpandPagingStartupDiagnosticTests
{
    private static async Task<(TestFixture Fixture, WarningCapture Logs)> BuildAsync(
        Action<OhDataBuilder> configure)
    {
        var capture = new WarningCapture();
        TestFixture fx = await TestHostBuilder.BuildAsync(
            configure,
            configureServices: s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                s.AddDbContext<EpdDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        return (fx, capture);
    }

    private static IReadOnlyList<string> BareExpandWarnings(WarningCapture logs) =>
        logs.Warnings.Where(w => w.Contains("MaxExpandTop resolves to null", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Fires_OncePerCollectionNavigation_NamingTheSetTheNavAndBothKnobs()
    {
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdExposedProfile>());
        await using TestFixture _ = fx;

        string warning = Assert.Single(BareExpandWarnings(logs));

        // The entity set and the navigation, so the reader knows which line of which profile to change.
        Assert.Contains("'EpdParents'", warning, StringComparison.Ordinal);
        Assert.Contains("'Children'", warning, StringComparison.Ordinal);

        // BOTH knobs, because they are two separate decisions: MaxExpandTop bounds the shape (and, on
        // its own, turns the over-ceiling case into a 400), and ExpandPagingEnabled is the further
        // opt-in to serving a continuation instead of that 400.
        Assert.Contains("MaxExpandTop", warning, StringComparison.Ordinal);
        Assert.Contains("ExpandPagingEnabled", warning, StringComparison.Ordinal);

        // The framing the owner asked for: it informs the decision without making it. A diagnostic
        // that prescribed a number would reintroduce exactly what stage 1 removed.
        Assert.Contains("does not guess", warning, StringComparison.Ordinal);

        // Single-valued Owner is at most one related row, so it is not the DoS and must not be named.
        Assert.DoesNotContain("'Owner'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Silent_WhenExpandIsDisabled()
    {
        // The load-bearing condition. ExpandEnabled is false by DEFAULT, so this is the state almost
        // every registration is in, and it is why the diagnostic is targeted rather than noisy.
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdNoExpandProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(BareExpandWarnings(logs));
    }

    [Fact]
    public async Task Silent_WhenMaxExpandTopIsSet()
    {
        // With a ceiling in force there IS a bound, and #313 stage 2 already answers the over-ceiling
        // shape with a 400. There is nothing left to warn about.
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o =>
        {
            o.WithDefaults(d => d.MaxExpandTop = 50);
            o.AddEntitySetProfile<EpdExposedProfile>();
        });
        await using TestFixture _ = fx;

        Assert.Empty(BareExpandWarnings(logs));
    }

    [Fact]
    public async Task Silent_WhenTheSetIsServedFromGetAll()
    {
        // GetAll materializes through the handler, not through a pushed-down projection; the bare
        // $expand SQL bound #313 is about does not exist on that path at all.
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdGetAllProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(BareExpandWarnings(logs));
    }

    [Fact]
    public async Task Silent_WhenTheNavigationIsDelegateBacked()
    {
        // A delegate-backed navigation is never in the engaged tree, so it is never pushed down and
        // never bounded by MaxExpandTop. Its own unboundedness is issue #313's O6, a separate contract
        // question — warning about it here would point at a knob that does not govern it.
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdDelegateBackedProfile>());
        await using TestFixture _ = fx;

        Assert.Empty(BareExpandWarnings(logs));
    }

    /// <summary>
    /// Not one of the five conditions #313's design lists — added because it is measurable and the
    /// design's own rule is "every condition under which the exposure is live". With expand pushdown
    /// off no <c>EngagedExpand</c> is built at all, so nothing loads the child collection and the
    /// response carries an empty array; there is no materialization for <c>MaxExpandTop</c> to bound,
    /// and naming it would point at a knob that changes nothing for that registration.
    /// <para>
    /// The first assertion is the measurement the second one's silence rests on — without it this
    /// test would pin the silence without establishing that the silence is correct.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Silent_WhenExpandPushdownIsDisabled_BecauseNothingIsMaterialized()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var capture = new WarningCapture();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection,
            sink: null,
            defaults: d => d.ExpandPushdownEnabled = false,
            configureExtraServices: s => s.AddSingleton<ILoggerProvider>(capture));

        // The measurement: author 1 has five books, and the bare $expand returns none of them.
        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BeAuthors?$expand=Books");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Books\":[]", body, StringComparison.Ordinal);

        // Therefore: nothing to warn about.
        Assert.Empty(BareExpandWarnings(capture));
    }

    /// <summary>
    /// Brownfield: the fixture, model and profile here were authored by #313 stage 2, not by this
    /// change. <c>BeAuthors</c> enables <c>$expand</c>, is served from <c>GetQueryable</c>, and declares
    /// one delegate-less collection navigation (<c>Books</c>) plus one delegate-less single-valued one
    /// (<c>Publisher</c>) — so it is the exposed shape, and exactly one warning is the right answer.
    /// </summary>
    [Fact]
    public async Task Fires_OnAPreExistingFixtureItDidNotAuthor()
    {
        var capture = new WarningCapture();
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BeAuthorProfile>(),
            configureServices: s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                s.AddDbContext<BareExpandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });

        string warning = Assert.Single(BareExpandWarnings(capture));
        Assert.Contains("'BeAuthors'", warning, StringComparison.Ordinal);
        Assert.Contains("'Books'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("'Publisher'", warning, StringComparison.Ordinal);
    }
}

// ── The flag changes no behaviour ────────────────────────────────────────────────────────────────

public class ExpandPagingEnabledIsInertTests
{
    private static async Task<string> BodyAsync(Action<EntitySetDefaults> defaults, string url)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(connection, sink: null, defaults: defaults);
        HttpResponseMessage resp = await fx.Client.GetAsync(url);
        return $"{(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}";
    }

    /// <summary>
    /// Stage 3's whole contract: the flag is readable and nothing acts on it. Run over the four shapes
    /// stage 2's ceiling actually discriminates between — under, at and over the ceiling, and the
    /// uncapped case — because those are where an accidental early read of the flag would show up.
    /// </summary>
    [Theory]
    [InlineData("/odata/BeAuthors?$expand=Books", 10)]  // under the ceiling
    [InlineData("/odata/BeAuthors?$expand=Books", 5)]   // exactly at it
    [InlineData("/odata/BeAuthors?$expand=Books", 3)]   // over it — stage 2's 400
    [InlineData("/odata/BeAuthors?$expand=Books($top=2)", 3)]
    [InlineData("/odata/BeAuthors?$expand=Publisher", 3)]
    public async Task SettingTheFlag_ChangesNoResponseByte_WhenAceilingIsInForce(string url, int ceiling)
    {
        string off = await BodyAsync(d => d.MaxExpandTop = ceiling, url);
        string on = await BodyAsync(d => { d.MaxExpandTop = ceiling; d.ExpandPagingEnabled = true; }, url);
        Assert.Equal(off, on);
    }

    /// <summary>
    /// And with no ceiling — the shipping default after stage 1 — where the flag is doubly inert:
    /// there is no boundary for a continuation to begin at even once stage 5 lands.
    /// </summary>
    [Theory]
    [InlineData("/odata/BeAuthors?$expand=Books")]
    [InlineData("/odata/BeAuthors?$expand=Books($expand=Chapters)")]
    public async Task SettingTheFlag_ChangesNoResponseByte_WhenUncapped(string url)
    {
        string off = await BodyAsync(_ => { }, url);
        string on = await BodyAsync(d => d.ExpandPagingEnabled = true, url);
        Assert.Equal(off, on);
    }
}
