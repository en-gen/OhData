using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #508 — model.FindDeclaredType(clrType.FullName) survived at four read-path sites after #491
// re-keyed the nav-suppression map off ClrTypeAnnotation. FindDeclaredType matches on the EDM type's
// FULL NAME, so a schema whose type names do not equal the CLR FullName — reachable through
// AdvancedConfigure's full EDM control — makes every one of those lookups return null and every
// caller take its "not in the EDM" branch. Nothing throws, nothing is logged: $expand pushdown
// disengages for the whole model.
//
// The fixture renames ONE type's EDM namespace, which is the smallest configuration that separates
// the two lookups: the annotation still resolves it, the name convention no longer does.

public sealed class NmParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<NmChild> Children { get; set; } = new();
}

public sealed class NmChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Label { get; set; } = "";
    public List<NmTag> Tags { get; set; } = new();
}

public sealed class NmTag
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class NmDbContext : DbContext
{
    public NmDbContext(DbContextOptions<NmDbContext> options) : base(options) { }

    public DbSet<NmParent> NmParents => Set<NmParent>();
    public DbSet<NmChild> NmChildren => Set<NmChild>();
    public DbSet<NmTag> NmTags => Set<NmTag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NmParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        modelBuilder.Entity<NmChild>().HasMany(c => c.Tags).WithOne().HasForeignKey(t => t.ChildId);
    }
}

public sealed class NmParentProfile : EntitySetProfile<int, NmParent>
{
    private readonly NmDbContext _db;

    public NmParentProfile(NmDbContext db) : base(x => x.Id)
    {
        _db = db;
        EntitySetName = "NmParents";
        ExpandEnabled = true;
        SelectEnabled = true;
        OrderByEnabled = true;
        HasMany(x => x.Children); // delegate-LESS, so the pushdown gate is the only thing in play
        GetQueryable = () => _db.NmParents.AsQueryable();
        GetById = async (id, ct) => OhDataResult.Success<NmParent?>(await _db.NmParents.FirstOrDefaultAsync(p => p.Id == id, ct));
    }
}

/// <summary>
/// The renamer. <c>AdvancedConfigure</c> hands over full EDM control, and one line of it is enough
/// to make <c>NmChild</c>'s EDM full name stop matching <c>typeof(NmChild).FullName</c>.
/// </summary>
public sealed class NmChildProfile : EntitySetProfile<int, NmChild>
{
    internal const string RenamedNamespace = "Nm.Custom";

    private readonly NmDbContext _db;

    public NmChildProfile(NmDbContext db) : base(x => x.Id)
    {
        _db = db;
        EntitySetName = "NmChildren";
        HasMany(x => x.Tags);
        GetQueryable = () => _db.NmChildren.AsQueryable();
    }

    protected override void AdvancedConfigure(EntitySetConfiguration<NmChild> configuration)
    {
        configuration.EntityType.Namespace = RenamedNamespace;
        // AdvancedConfigure ejects from OhData's own EDM configuration, so the model-bound
        // expandability this type would otherwise get has to be written here.
        configuration.EntityType.Expand();
    }
}

public sealed class RenamedSchemaPushdownTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();

        _fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                b.AddEntitySetProfile<NmParentProfile>();
                b.AddEntitySetProfile<NmChildProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(_sink);
                services.AddDbContext<NmDbContext>(o =>
                {
                    o.UseSqlite(_connection);
                    o.LogTo(
                        message => _sink.Add(message),
                        (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics
                            .RelationalEventId.CommandExecuted);
                });
            });

        using IServiceScope scope = _fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmDbContext>();
        db.Database.EnsureCreated();
        db.NmParents.AddRange(new NmParent { Id = 1, Name = "P1" }, new NmParent { Id = 2, Name = "P2" });
        db.NmChildren.AddRange(
            new NmChild { Id = 10, ParentId = 1, Label = "C1a" },
            new NmChild { Id = 11, ParentId = 1, Label = "C1b" },
            new NmChild { Id = 20, ParentId = 2, Label = "C2a" });
        db.NmTags.AddRange(
            new NmTag { Id = 100, ChildId = 10, Text = "T100" },
            new NmTag { Id = 101, ChildId = 11, Text = "T101" });
        db.SaveChanges();
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    // ── The premise, measured rather than assumed ───────────────────────────────────────────────

    /// <summary>
    /// The rename really happened, and it is exactly the configuration that separates the two
    /// lookups: the model builder's own annotation still resolves <see cref="NmChild"/>, while the
    /// full-name convention <c>FindDeclaredType</c> relies on no longer does.
    /// </summary>
    [Fact]
    public async Task Premise_TheRenamedTypeIsInvisibleToFindDeclaredType_ButNotToTheAnnotation()
    {
        string xml = await _fx.Client.GetStringAsync("/odata/$metadata");
        Assert.Contains($"<Schema Namespace=\"{NmChildProfile.RenamedNamespace}\"", xml);

        IEdmModel model = Registration().EdmModel;
        Assert.Null(model.FindDeclaredType(typeof(NmChild).FullName!));
        Assert.Contains(
            model.SchemaElements.OfType<IEdmEntityType>(),
            e => model.GetAnnotationValue<ClrTypeAnnotation>(e)?.ClrType == typeof(NmChild));
    }

    // ── The four sites, individually ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>IsMemberInitProjectable</c> and <c>ScalarStructuralClrProps</c> are consulted for the
    /// navigation's ELEMENT type, and <c>TryGetKeyClrProperty</c> for its key. Pre-fix all three
    /// answered "this type is not in the EDM": false / empty / null.
    /// </summary>
    [Fact]
    public void TheProjectionHelpers_ResolveTheRenamedElementType()
    {
        IEdmModel model = Registration().EdmModel;

        Assert.True(Invoke<bool>("IsMemberInitProjectable", typeof(NmChild), model));

        var scalars = (IEnumerable<PropertyInfo>)Invoke<object>(
            "ScalarStructuralClrProps", typeof(NmChild), model)!;
        Assert.Contains(scalars, p => p.Name == "Label");

        var key = (PropertyInfo?)Invoke<object?>("TryGetKeyClrProperty", model, typeof(NmChild));
        Assert.Equal("Id", key?.Name);
    }

    /// <summary>
    /// <c>ResolveProfilesForClrType</c> is the pushdown gate's candidate resolution. Pre-fix it
    /// returned an EMPTY candidate set for the renamed element type, so <c>ResolveNavTreatment</c>
    /// saw nobody with an opinion.
    /// </summary>
    [Fact]
    public void TheModelBGate_ResolvesCandidatesForTheRenamedElementType()
    {
        OhDataRegistration registration = Registration();

        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Static;
        Type factory = typeof(OhDataRegistration).Assembly.GetType("OhData.OhDataEndpointFactory", true)!;
        object? result = factory.GetMethod("ResolveProfilesForClrType", Any)!
            .Invoke(null, new object?[] { typeof(NmChild), registration.EdmModel, registration });

        var candidates = (System.Collections.IEnumerable)result!;
        Assert.Single(candidates.Cast<object>());
    }

    // -- End to end: the behaviour downgrade the four sites add up to ---------------------------

    /// <summary>
    /// THE MEASURED CONSEQUENCE. A MULTI-LEVEL <c>$expand</c> - an intermediate level, which is one
    /// of the two branches <c>TryBuildEngagedExpand</c> gates on
    /// <c>IsMemberInitProjectable(binding.ElementType, model)</c> - over delegate-less navigations.
    /// <para>
    /// PRE-FIX, on this renamed schema (captured verbatim):
    /// <code>
    /// 200 {"@odata.context":"...#NmParents","value":[{"Id":1,"Name":"P1","Children":[]},
    ///                                                {"Id":2,"Name":"P2","Children":[]}]}
    /// SELECT "n"."Id", "n"."Name" FROM "NmParents" AS "n" ORDER BY "n"."Id" LIMIT @p
    /// </code>
    /// Every parent's children silently empty, the child table never touched, HTTP 200, nothing
    /// logged above Debug. The identical model without the namespace rename serves the whole graph.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MultiLevelExpand_OnARenamedSchema_StillPushesDownAndServesTheWholeGraph()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/NmParents?$expand=Children($expand=Tags)&$orderby=Id");
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The data the client asked for, at both levels.
        Assert.Contains("\"Label\":\"C1a\"", body);
        Assert.Contains("\"Text\":\"T100\"", body);
        Assert.DoesNotContain("\"Children\":[]", body);

        // And it came from ONE query that JOINed both levels, not from nothing at all.
        string sql = LastSelectAgainst("NmParents");
        Assert.Contains("\"NmChildren\"", sql);
        Assert.Contains("\"NmTags\"", sql);
    }

    /// <summary>
    /// CONTROL, not a reproduction: a SINGLE-level expand of the same navigation was already pushed
    /// down pre-fix - <c>TryBuildEngagedExpand</c> consults <c>IsMemberInitProjectable</c> only for a
    /// <c>$levels</c> item and for an intermediate level, and <c>ApplyNavShape</c> falls back to a
    /// bare navigation access when the element type is not projectable. Kept so a future change to
    /// the projection helpers cannot quietly break the level that used to work.
    /// </summary>
    [Fact]
    public async Task SingleLevelExpand_OnARenamedSchema_StillJoins()
    {
        _sink.Clear();
        var resp = await _fx.Client.GetAsync("/odata/NmParents?$expand=Children&$orderby=Id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains("\"NmChildren\"", LastSelectAgainst("NmParents"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The most recent executed SELECT against <paramref name="table"/>.</summary>
    private string LastSelectAgainst(string table) => _sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal)
                    && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();

    private OhDataRegistration Registration() =>
        _fx.App.Services.GetRequiredKeyedService<OhDataRegistration>("__default__");

    private static T Invoke<T>(string name, params object?[] args)
    {
        const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Static;
        Type factory = typeof(OhDataRegistration).Assembly.GetType("OhData.OhDataEndpointFactory", true)!;
        return (T)factory.GetMethod(name, Any)!.Invoke(null, args)!;
    }
}
