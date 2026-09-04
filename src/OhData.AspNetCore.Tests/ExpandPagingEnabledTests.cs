using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
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
        GetQueryable = () => db.Parents.AsQueryable();
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
        GetQueryable = () => db.Parents.AsQueryable();
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
        GetAll = _ => OhDataResult.Success<IEnumerable<EpdParent>>(db.Parents.ToList());
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
        GetQueryable = () => db.Parents.AsQueryable();
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

        // #451: ONE name per fixture, evaluated HERE. The AddDbContext options lambda below runs on
        // every DbContext instantiation, so a Guid.NewGuid() inside it handed each scope a different
        // database name — and therefore a fresh EMPTY database. See
        // ServesTheRowsItSeeded_SoTheFixtureIsNotAFreshEmptyDatabasePerScope.
        string dbName = Guid.NewGuid().ToString();

        TestFixture fx = await TestHostBuilder.BuildAsync(
            configure,
            configureServices: s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                s.AddDbContext<EpdDbContext>(o => o.UseInMemoryDatabase(dbName));
            });

        SeedOnce(fx);
        return (fx, capture);
    }

    /// <summary>
    /// Two parents, one owner and two children, written through the host's own scope — the same
    /// seed-here / serve-there shape <c>BareExpandSqliteHarness</c> uses, and the shape that makes
    /// the database NAME a fixture-wide value rather than a per-scope one.
    /// </summary>
    private static void SeedOnce(TestFixture fx)
    {
        using IServiceScope scope = fx.App.Services.CreateScope();
        EpdDbContext db = scope.ServiceProvider.GetRequiredService<EpdDbContext>();

        db.Owners.Add(new EpdOwner { Id = 100, Name = "Own1" });
        db.Parents.AddRange(
            new EpdParent { Id = 1, Name = "P1", OwnerId = 100 },
            new EpdParent { Id = 2, Name = "P2" });
        db.Children.AddRange(
            new EpdChild { Id = 1, ParentId = 1, Label = "C1" },
            new EpdChild { Id = 2, ParentId = 1, Label = "C2" });
        db.SaveChanges();
    }

    private static IReadOnlyList<string> BareExpandWarnings(WarningCapture logs) =>
        logs.Warnings.Where(w => w.Contains("MaxExpandTop resolves to null", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// #451: THE FIXTURE ITSELF, ASSERTED. <c>BuildAsync</c> configured EF InMemory as
    /// <c>UseInMemoryDatabase(Guid.NewGuid().ToString())</c> <b>inside</b> the <c>AddDbContext</c>
    /// options lambda. That lambda runs once per <c>DbContext</c> <i>instantiation</i>, not once at
    /// registration, so the seeding scope and every request scope each got their own database name
    /// and therefore their own empty database.
    /// <para>
    /// It was latent only because every other test in this class asserts on startup LOG OUTPUT, which
    /// an empty database cannot disturb. This test is the one that cannot pass vacuously: it seeds
    /// two parents and then reads them back over HTTP. Measured against the unfixed fixture it fails
    /// with <c>Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 0</c> — every row gone,
    /// under a <c>200</c>.
    /// </para>
    /// <para>
    /// It exists so nobody has to notice the bug again: a fixture named for a paging knob will
    /// eventually get a row-serving test, and this is it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ServesTheRowsItSeeded_SoTheFixtureIsNotAFreshEmptyDatabasePerScope()
    {
        (TestFixture fx, WarningCapture _) = await BuildAsync(o => o.AddEntitySetProfile<EpdExposedProfile>());
        await using TestFixture owned = fx;

        System.Text.Json.JsonElement root = await fx.Client
            .GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/EpdParents");
        System.Text.Json.JsonElement value = root.GetProperty("value");

        Assert.Equal(2, value.GetArrayLength());
        List<string?> names = value.EnumerateArray()
            .Select(e => e.GetProperty("Name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new List<string?> { "P1", "P2" }, names);
    }

    [Fact]
    public async Task Fires_OncePerCollectionNavigation_NamingTheSetTheNavAndMaxExpandTop()
    {
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdExposedProfile>());
        await using TestFixture _ = fx;

        string warning = Assert.Single(BareExpandWarnings(logs));

        // The entity set and the navigation, so the reader knows which line of which profile to change.
        Assert.Contains("'EpdParents'", warning, StringComparison.Ordinal);
        Assert.Contains("'Children'", warning, StringComparison.Ordinal);

        // The one knob that actually does something today.
        Assert.Contains("MaxExpandTop", warning, StringComparison.Ordinal);

        // The framing the owner asked for: it informs the decision without making it. A diagnostic
        // that prescribed a number would reintroduce exactly what stage 1 removed.
        Assert.Contains("does not guess", warning, StringComparison.Ordinal);

        // Single-valued Owner is at most one related row, so it is not the DoS and must not be named.
        Assert.DoesNotContain("'Owner'", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// #313 stage 5 REPLACES stage 3's <c>DoesNotName_ExpandPagingEnabled_WhileNothingActsOnIt</c>.
    /// That assertion existed to stop the warning naming a flag nothing acted on, and its own doc
    /// comment said stage 5 would extend the message "when the flag starts meaning something" — which
    /// is now. Deleting it is therefore the deliberate edit it was written to force, and this test is
    /// what keeps the extension honest in the other direction: the message must name BOTH knobs, and
    /// must still prescribe no number.
    /// </summary>
    [Fact]
    public async Task Names_ExpandPagingEnabled_NowThatItRegistersARouteAndEmitsALink()
    {
        (TestFixture fx, WarningCapture logs) = await BuildAsync(o => o.AddEntitySetProfile<EpdExposedProfile>());
        await using TestFixture _ = fx;

        string warning = Assert.Single(BareExpandWarnings(logs));
        Assert.Contains("ExpandPagingEnabled", warning, StringComparison.Ordinal);
        Assert.Contains("Nav@odata.nextLink", warning, StringComparison.Ordinal);

        // MaxExpandTop is still named FIRST: it is a complete answer on its own, and the second knob
        // is inert without it. Ordering is the whole advice here, so it is asserted rather than assumed.
        Assert.True(
            warning.IndexOf("MaxExpandTop", StringComparison.Ordinal)
                < warning.IndexOf("ExpandPagingEnabled", StringComparison.Ordinal),
            "the warning must name MaxExpandTop before ExpandPagingEnabled — the second knob does " +
            "nothing without the first.");

        // Still no prescribed number. That is what stage 1 removed and what this diagnostic replaces.
        Assert.DoesNotContain("1000", warning, StringComparison.Ordinal);
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
    /// #421: a delegate-backed SIBLING over the same EDM entity type no longer silences the
    /// diagnostic on the set that still serves the navigation raw and unbounded.
    /// <para>
    /// WHAT THIS MEASURED BEFORE. The diagnostic resolved <c>ServeRaw</c> through
    /// <c>ResolveProfilesForEdmType</c> — the sibling union — so with both profiles registered
    /// <c>ResolveNavTreatment</c> answered <c>Blank</c> and NEITHER set warned. Measured on the
    /// pre-fix tree: <b>zero</b> bare-expand warnings, while <c>/BeAuthors?$expand=Books</c> returned
    /// all five of author 1's books with no ceiling at all. The profile that most needed the warning
    /// was the one that did not get it.
    /// </para>
    /// <para>
    /// BROWNFIELD, and it has to be: both profiles and the whole SQLite fixture predate #421, and the
    /// raw serve is already pinned independently by
    /// <c>BareExpandContinuationDelegateSafetyTests.RootExpand_WithASiblingDelegate_StillServesTheDeclaringSetsOwnRawRows</c>.
    /// The first assertion here is that same measurement restated in this file, so the warning is not
    /// pinned without establishing that what it claims is true. An earlier draft used the
    /// <c>EpdParent</c> fixtures above and would have been VACUOUS: under EF InMemory that model's
    /// <c>Children</c> comes back <c>[]</c> whether a sibling is registered or not, so the warning
    /// would have been "correct" about an exposure that fixture does not have.
    /// </para>
    /// <para>
    /// The <c>Single</c> is the bound: <c>BeDelegatedAuthors</c> routes <c>Books</c> through its own
    /// delegate, so on its own candidate set the treatment is <c>RunDelegate</c> and it stays silent.
    /// This is not "the diagnostic got louder everywhere".
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fires_ForTheSetThatServesRaw_EvenWhenADelegateBackedSiblingSharesTheEdmType()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        var capture = new WarningCapture();
        await using TestFixture fx = await BareExpandSqliteHarness.BuildAsync(
            connection,
            sink: null,
            defaults: null, // MaxExpandTop stays null — the condition the diagnostic is about
            configureExtraServices: s => s.AddSingleton<ILoggerProvider>(capture),
            configureExtraProfiles: b => b.AddEntitySetProfile<BeDelegatedAuthorProfile>());

        // THE MEASUREMENT: the exposure the warning describes is live on BeAuthors. All five books
        // come back from the delegate-less set with no ceiling, which is the whole content of the
        // warning — and the sibling's delegate is not what served them.
        // #484: the count belongs to THIS host, so there is nothing to reset and nothing another
        // test class running in parallel can reset underneath this assertion.
        BeDelegateInvocationCounter counter =
            fx.App.Services.GetRequiredService<BeDelegateInvocationCounter>();
        System.Text.Json.JsonElement root = await fx.Client
            .GetFromJsonAsync<System.Text.Json.JsonElement>("/odata/BeAuthors?$filter=Id eq 1&$expand=Books");
        Assert.Equal(5, root.GetProperty("value")[0].GetProperty("Books").GetArrayLength());
        Assert.Equal(0, counter.Invocations);

        // Therefore the warning fires, exactly once, naming the set that serves raw.
        string warning = Assert.Single(BareExpandWarnings(capture));
        Assert.Contains("'BeAuthors'", warning, StringComparison.Ordinal);
        Assert.Contains("'Books'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("'BeDelegatedAuthors'", warning, StringComparison.Ordinal);
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

        // #451: same hoist as BuildAsync above — one name for the whole fixture, not one per
        // DbContext instantiation. This host is never seeded (the assertion is on the startup
        // diagnostic, which runs before any request), but a per-scope database is wrong here for the
        // same reason and is exactly the trap the next person to add a row assertion would hit.
        string dbName = Guid.NewGuid().ToString();

        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<BeAuthorProfile>(),
            configureServices: s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                s.AddDbContext<BareExpandDbContext>(o => o.UseInMemoryDatabase(dbName));
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
    /// Stage 3's contract was "the flag is readable and NOTHING acts on it", asserted over four shapes
    /// including the over-ceiling bare one. Stage 5 narrows it, deliberately: the over-ceiling BARE
    /// row is exactly the one shape the flag now changes (400 → 200 + <c>Books@odata.nextLink</c>),
    /// so it has moved to
    /// <see cref="BareExpandContinuationInertnessTests.Capped_TheTrulyBareSubsetIsExactlyWhereTheFlagChangesTheAnswer"/>
    /// where it is asserted as a REQUIRED difference. Everything left here is a shape stage 5 does not
    /// touch, and it stays byte-identical — which is what keeps the blast radius the truly-bare subset
    /// and nothing wider.
    /// </summary>
    [Theory]
    [InlineData("/odata/BeAuthors?$expand=Books", 10)]  // under the ceiling — no boundary to continue from
    [InlineData("/odata/BeAuthors?$expand=Books", 5)]   // exactly at it — the rows % pageSize == 0 case
    [InlineData("/odata/BeAuthors?$expand=Books($top=2)", 3)]
    [InlineData("/odata/BeAuthors?$expand=Publisher", 3)]
    public async Task SettingTheFlag_ChangesNoResponseByte_OutsideTheTrulyBareOverCeilingShape(string url, int ceiling)
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
