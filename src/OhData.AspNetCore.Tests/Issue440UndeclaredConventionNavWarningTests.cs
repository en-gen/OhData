using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// #440: a convention-discovered navigation the profile never declared produced WRONG DATA UNDER 200
// on $expand, and registered structural-property routes over a navigation. Both share #322's root
// cause -- the profile's navigation set and the EDM's disagree -- which #322 reconciled for the query
// plan only.
//
// Both symptoms are fixed. Route registration subtracts the EDM's navigation names. ExpandLevelAsync's
// ServeRaw branch separates its two populations: a DECLARED navigation keeps its raw value (that value
// is loaded data), an undeclared one is REMOVED, because §8.3 makes an inline value the representation
// of an EXPANDED navigation, so `"Customer": null` asserts an emptiness the server never determined.
//
// The warning stays because the disagreement outlives the symptoms -- $metadata still advertises a
// navigation this set will never serve -- but it states only what is still true, and the content test
// guards against every retired consequence.
//
// Pins three things: the symptoms as fixes, with bounding assertions so neither passes vacuously; the
// warning's exact content; and its targeting.

#region fixtures

public sealed class W440Order
{
    public int Id { get; set; }
    public string Note { get; set; } = "";
    public int? CustomerId { get; set; }
    public W440Customer? Customer { get; set; } // convention-discovered, NEVER declared
}

public sealed class W440Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class W440Invoice
{
    public int Id { get; set; }
    public string Ref { get; set; } = "";
    public int? PayerId { get; set; }
    public W440Customer? Payer { get; set; } // convention-discovered, NEVER declared
}

/// <summary>A model with no navigation at all — the control that must stay silent.</summary>
public sealed class W440Plain
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>
/// A SELF-REFERENTIAL undeclared navigation. This is the one shape that #440's symptom-1 fix must
/// NOT touch: the root pushdown loop resolves a <c>$levels</c> expand through
/// <c>BuildLevelsNavBinding</c>, which does not consult <c>NavigationPropertyNames</c>, so
/// <c>?$expand=Children($levels=2)</c> really is pushed to SQL and really does load — even though
/// the profile declares nothing.
/// </summary>
public sealed class W440Node
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public W440Node? Parent { get; set; }              // convention-discovered, NEVER declared
    public List<W440Node> Children { get; set; } = new(); // convention-discovered, NEVER declared
}

/// <summary>
/// #466 (PR #477 review, F2): the CROSS-LEVEL NAME COLLISION shape. A root whose navigation
/// <c>Children</c> is convention-discovered and never declared — so #440 must omit it — reached in
/// the same request as a DIFFERENT level whose own <c>Children</c> IS declared and delegate-less.
/// The two are the same NAME at two different LEVELS, which is the whole point: the raw
/// <c>$levels</c> budget is a flat name set while "does any candidate have an opinion" is resolved
/// per level, so a set built for the deep one must never be consulted for the shallow one.
/// <para>
/// <c>Other</c> is declared, so the walk descends through it; <c>Children</c> is not declared here,
/// so the hub can never serve it.
/// </para>
/// </summary>
public sealed class W440Hub
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<W440Branch> Children { get; set; } = new(); // convention-discovered, NEVER declared
    public List<W440Branch> Other { get; set; } = new();    // declared, delegate-less
}

/// <summary>Self-referential and DECLARED delegate-less — so its own <c>Children</c> is ServeRaw
/// with an opinion, which is what puts the name into the raw <c>$levels</c> budget.</summary>
public sealed class W440Branch
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public int? ChildHubId { get; set; }
    public int? OtherHubId { get; set; }
    public int? ParentId { get; set; }
    public List<W440Branch> Children { get; set; } = new();
}

public sealed class W440DbContext : DbContext
{
    public W440DbContext(DbContextOptions<W440DbContext> options) : base(options) { }

    public DbSet<W440Order> Orders => Set<W440Order>();
    public DbSet<W440Customer> Customers => Set<W440Customer>();
    public DbSet<W440Invoice> Invoices => Set<W440Invoice>();
    public DbSet<W440Plain> Plains => Set<W440Plain>();
    public DbSet<W440Node> Nodes => Set<W440Node>();
    public DbSet<W440Hub> Hubs => Set<W440Hub>();
    public DbSet<W440Branch> Branches => Set<W440Branch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<W440Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
        b.Entity<W440Invoice>().HasOne(i => i.Payer).WithMany().HasForeignKey(i => i.PayerId);
        b.Entity<W440Node>().HasOne(n => n.Parent).WithMany(n => n.Children).HasForeignKey(n => n.ParentId);
        b.Entity<W440Hub>().HasMany(h => h.Children).WithOne().HasForeignKey(x => x.ChildHubId);
        b.Entity<W440Hub>().HasMany(h => h.Other).WithOne().HasForeignKey(x => x.OtherHubId);
        b.Entity<W440Branch>().HasMany(n => n.Children).WithOne().HasForeignKey(n => n.ParentId);
    }
}

/// <summary>#466/F2: declares <c>Other</c> and deliberately NOT <c>Children</c>.</summary>
public sealed class W440HubProfile : EntitySetProfile<int, W440Hub>
{
    public W440HubProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Hubs";
        ExpandEnabled = true;
        SelectEnabled = true;
        // GetAll, not GetQueryable: the raw substrate is where the #466 levels budget applies, and
        // the eager load is what puts three levels of branches into the graph it reads.
        GetAll = _ => OhDataResult.SuccessTask<IEnumerable<W440Hub>>(
            db.Hubs.Include(h => h.Other).ThenInclude(x => x.Children).ThenInclude(x => x.Children).ToList());
        HasMany(x => x.Other); // declared, delegate-less — the branch the walk descends through
        // x.Children is deliberately NOT declared: it is the #440 no-opinion navigation whose name
        // collides with the declared one a level down.
    }
}

/// <summary>#466/F2: declares the self-referential <c>Children</c>, delegate-less.</summary>
public sealed class W440BranchProfile : EntitySetProfile<int, W440Branch>
{
    public W440BranchProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Branches";
        ExpandEnabled = true;
        SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Branches.AsQueryable());
        HasMany(x => x.Children);
    }
}

/// <summary>Declares nothing; both <c>Parent</c> and <c>Children</c> are convention-discovered.</summary>
public sealed class W440NodeProfile : EntitySetProfile<int, W440Node>
{
    public W440NodeProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Nodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Nodes.AsQueryable());
    }
}

/// <summary>
/// The affected shape: $expand enabled, GetById AND Patch configured, PropertyAccessEnabled left at
/// its default of <c>true</c>. Both #440 symptoms were live here before the fixes.
/// </summary>
public sealed class W440OrderProfile : EntitySetProfile<int, W440Order>
{
    /// <summary>
    /// #461: exactly what the <c>Post</c> handler was handed, so a test can assert the deep-insert
    /// strip ran — the HTTP echo cannot answer that question, because #240 omits every EDM
    /// navigation from it either way. Static: profiles are registered <c>AddScoped</c>.
    /// </summary>
    public static W440Order? LastPosted;

    /// <summary>
    /// #457: the same observation for <c>PUT</c>, which reaches the SAME strip set now that deep
    /// UPDATE (§11.4.3.1) is enforced rather than only documented out of scope.
    /// </summary>
    public static W440Order? LastPut;

    /// <summary>
    /// #457: the names the <c>PATCH</c> delta reported at the handler. An undeclared convention
    /// navigation must not be among them.
    /// </summary>
    public static string[]? LastPatchChangedProperties;

    public W440OrderProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Orders";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Orders.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.Orders.FirstOrDefault(o => o.Id == id));
        // #461: deliberately does NOT persist. The defect is what the handler RECEIVES; a handler
        // that called SaveChanges() here is the adopter this protects, not the observation point.
        Post = (order, _) =>
        {
            LastPosted = order;
            return OhDataResult.SuccessTask<W440Order>(order);
        };
        // #457: PUT, added for the deep-UPDATE half of the same defect class. Non-persisting for
        // exactly the reason Post is.
        Put = (id, order, _) =>
        {
            LastPut = order;
            order.Id = id;
            return OhDataResult.SuccessTask(order);
        };
        Patch = (id, delta, _) =>
        {
            // #457: captured BEFORE Patch(existing) — the question is what the delta contained,
            // not what survived being applied to a tracked entity.
            LastPatchChangedProperties = delta.GetChangedPropertyNames().ToArray();
            W440Order? existing = db.Orders.FirstOrDefault(o => o.Id == id);
            if (existing is null) return OhDataResult.SuccessTask<W440Order>(null);
            delta.Patch(existing);
            db.SaveChanges();
            return OhDataResult.SuccessTask<W440Order>(existing);
        };
        // Customer deliberately NOT declared.
    }
}

/// <summary>
/// The SAME undeclared navigation, on a profile where the remaining consequence is not reachable:
/// <c>$expand</c> off. (Property access is off and there is no GetById/Patch here too, which used to
/// be half the gate; after #440 symptom 2 those no longer enter into it — <c>ExpandEnabled</c> is
/// the whole gate.) The disagreement is still in $metadata, but there is no defect to report, so
/// this profile must stay silent.
/// </summary>
public sealed class W440InvoiceProfile : EntitySetProfile<int, W440Invoice>
{
    public W440InvoiceProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Invoices";
        ExpandEnabled = false;
        PropertyAccessEnabled = false;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Invoices.AsQueryable());
        // No GetById, no Patch, no declaration of Payer.
    }
}

/// <summary>The no-navigation control.</summary>
public sealed class W440PlainProfile : EntitySetProfile<int, W440Plain>
{
    public W440PlainProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Plains";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Plains.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.Plains.FirstOrDefault(p => p.Id == id));
    }
}

/// <summary>The remedy, applied: the same navigation, DECLARED. Must stay silent.</summary>
public sealed class W440DeclaredOrderProfile : EntitySetProfile<int, W440Order>
{
    /// <summary>#461: the declared control's own capture — see <see cref="W440OrderProfile.LastPosted"/>.</summary>
    public static W440Order? LastPosted;

    public W440DeclaredOrderProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440DeclaredOrders";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => OhDataResult.SuccessTask(db.Orders.AsQueryable());
        GetById = (id, _) => OhDataResult.SuccessTask(db.Orders.FirstOrDefault(o => o.Id == id));
        Post = (order, _) =>
        {
            LastPosted = order;
            return OhDataResult.SuccessTask<W440Order>(order);
        };
        HasOptional<W440Customer>(x => x.Customer!);
    }
}

internal static class W440Harness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, WarningCapture capture, Action<OhDataBuilder> configure)
    {
        var fx = await TestHostBuilder.BuildAsync(
            configure,
            configureServices: services =>
            {
                services.AddSingleton<ILoggerProvider>(capture);
                services.AddDbContext<W440DbContext>(o => o.UseSqlite(connection));
            });

        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<W440DbContext>();
        db.Database.EnsureCreated();
        db.Customers.Add(new W440Customer { Id = 7, Name = "C7" });
        db.Orders.Add(new W440Order { Id = 1, Note = "N1", CustomerId = 7 });
        db.Invoices.Add(new W440Invoice { Id = 1, Ref = "R1", PayerId = 7 });
        db.Plains.Add(new W440Plain { Id = 1, Label = "L1" });
        db.Nodes.Add(new W440Node { Id = 1, Name = "root" });
        db.Nodes.Add(new W440Node { Id = 2, Name = "child", ParentId = 1 });
        // #466/F2: hub 1 -Other-> B1 -Children-> B2 -Children-> B3. Three branch levels, so a
        // $levels=2 under Other has a second level to actually reach — without which the regression
        // test's bounding half would pass vacuously.
        db.Hubs.Add(new W440Hub { Id = 1, Name = "H1" });
        db.Branches.Add(new W440Branch { Id = 1, Label = "B1", OtherHubId = 1 });
        db.Branches.Add(new W440Branch { Id = 2, Label = "B2", ParentId = 1 });
        db.Branches.Add(new W440Branch { Id = 3, Label = "B3", ParentId = 2 });
        db.SaveChanges();
        return fx;
    }
}

#endregion

public sealed class Issue440UndeclaredConventionNavWarningTests
{
    private readonly ITestOutputHelper _out;
    public Issue440UndeclaredConventionNavWarningTests(ITestOutputHelper output) => _out = output;

    /// <summary>The full registration used by the content and symptom tests.</summary>
    private static void ConfigureAll(OhDataBuilder b)
    {
        b.AddEntitySetProfile<W440OrderProfile>();
        b.AddEntitySetProfile<W440InvoiceProfile>();
        b.AddEntitySetProfile<W440PlainProfile>();
        b.AddEntitySetProfile<W440DeclaredOrderProfile>();
    }

    private static IEnumerable<string> UndeclaredNavWarnings(WarningCapture capture) =>
        capture.Warnings.Where(w => w.Contains(
            "that the OData convention builder discovered on", StringComparison.Ordinal));

    // ------------------------------------------------------------------ symptom 1: $expand

    /// <summary>
    /// #440 symptom 1, FIXED: <c>$expand</c> of the undeclared navigation used to answer
    /// <b>200 with null</b> for a row whose related entity exists. The navigation is still absent
    /// from <c>NavigationPropertyNames</c> — which is what <c>pushdownExpandNavs</c> is built from,
    /// so nothing ever loads it — but it is no longer EMITTED. <c>ExpandLevelAsync</c>'s
    /// <c>ServeRaw</c> branch now separates its two populations: a navigation some candidate
    /// DECLARED (the raw value is loaded data, keep it) from one no candidate declares or routes
    /// (#293's "has no opinion" category — nothing chose to load it, so there is no value to serve
    /// and the member is removed).
    /// <para>
    /// The spec line is OData JSON Format v4.01 §8.3: an inline navigation value <i>is</i> the
    /// representation of an EXPANDED navigation, so a null single-valued one is the positive claim
    /// that the relationship is empty — a claim the server never evaluated. §8.1 covers the honest
    /// alternative: a non-expanded navigation is represented by its navigation link (computed, and
    /// omitted under metadata=minimal), not inline. Omission is therefore the payload that asserts
    /// only true things, and it is what <c>OmitUnexpandedNavigations</c> already does for every
    /// navigation a request did not expand.
    /// </para>
    /// <para>
    /// NOT a 400. The request is valid against the <c>$metadata</c> this server published; the gap
    /// is the server's own configuration. The loud channel for that is startup, and the warning
    /// below is it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Symptom1_ExpandOfAnUndeclaredNavigation_OmitsIt_InsteadOfEmittingNull()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/W440Orders?$expand=Customer");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"undeclared (collection): {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement row = doc.RootElement.GetProperty("value")[0];
        Assert.False(row.TryGetProperty("Customer", out _));
        // The row is otherwise intact — the omission is one member, not a broken projection.
        Assert.Equal("N1", row.GetProperty("Note").GetString());
        Assert.Equal(7, row.GetProperty("CustomerId").GetInt32());

        // The single-entity read goes through the same pipeline and must agree; before the fix it
        // emitted "Customer":null too.
        HttpResponseMessage byId = await fx.Client.GetAsync("/odata/W440Orders(1)?$expand=Customer");
        string byIdBody = await byId.Content.ReadAsStringAsync();
        _out.WriteLine($"undeclared (by key):     {(int)byId.StatusCode} {byIdBody}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        using JsonDocument byIdDoc = JsonDocument.Parse(byIdBody);
        Assert.False(byIdDoc.RootElement.TryGetProperty("Customer", out _));

        // THE DECLARED CONTROL, over the SAME CLR model and the SAME row: unchanged, still loads
        // the related entity. Declaring the navigation is the whole difference, which is what makes
        // the startup warning's remedy actionable rather than advisory.
        HttpResponseMessage ok = await fx.Client.GetAsync("/odata/W440DeclaredOrders?$expand=Customer");
        string okBody = await ok.Content.ReadAsStringAsync();
        _out.WriteLine($"declared:                {(int)ok.StatusCode} {okBody}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"C7\"", okBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE EXCLUSION, and it is not hypothetical. A <c>$levels</c> expand of an undeclared
    /// SELF-REFERENTIAL navigation is resolved by <c>BuildLevelsNavBinding</c>, which does not
    /// consult <c>NavigationPropertyNames</c> — so that one shape really is pushed to SQL and really
    /// does load, undeclared or not. Omitting it would delete data the server had actually fetched,
    /// which is the mistake #440 symptom 1 exists to prevent, pointed the other way. The fix
    /// therefore keeps any navigation named in <c>pushedLevelsNavNames</c>.
    /// <para>
    /// Verified to bite: with the <c>pushedLevelsNavNames</c> clause removed, this test fails and
    /// its siblings above still pass — so it is guarding a live branch, not dead code.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Symptom1Fix_KeepsALevelsExpandOfAnUndeclaredSelfReferentialNav_BecauseThatOneIsActuallyLoaded()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        // Its own registration: W440Nodes carries TWO undeclared navigations, so folding it into
        // ConfigureAll would change the warning-count pins below for no benefit to them.
        await using TestFixture fx = await W440Harness.BuildAsync(
            connection, capture, b => b.AddEntitySetProfile<W440NodeProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync(
            "/odata/W440Nodes?$filter=Id eq 1&$expand=Children($levels=2)");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"$levels: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement.GetProperty("value")[0];
        JsonElement children = root.GetProperty("Children");
        Assert.Equal(1, children.GetArrayLength());
        Assert.Equal("child", children[0].GetProperty("Name").GetString());

        // The SAME entity set, the SAME undeclared navigation, WITHOUT $levels: not pushed, not
        // loaded, so omitted. The two halves of the rule in one assertion pair.
        HttpResponseMessage bare = await fx.Client.GetAsync("/odata/W440Nodes?$filter=Id eq 1&$expand=Parent");
        string bareBody = await bare.Content.ReadAsStringAsync();
        _out.WriteLine($"bare:    {(int)bare.StatusCode} {bareBody}");
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        using JsonDocument bareDoc = JsonDocument.Parse(bareBody);
        Assert.False(bareDoc.RootElement.GetProperty("value")[0].TryGetProperty("Parent", out _));
    }

    /// <summary>
    /// The omission is scoped to the navigation the request named: a plain read with no
    /// <c>$expand</c> at all is byte-identical to before (the undeclared navigation was already
    /// stripped by <c>OmitUnexpandedNavigations</c>, so there is nothing for this fix to change),
    /// and a request that expands NOTHING still returns every structural member.
    /// </summary>
    [Fact]
    public async Task Symptom1Fix_DoesNotTouchAReadThatNeverAskedToExpand()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/W440Orders");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine(body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            "{\"@odata.context\":\"http://localhost/odata/$metadata#W440Orders\"," +
            "\"value\":[{\"Id\":1,\"Note\":\"N1\",\"CustomerId\":7}]}",
            body);
    }

    // ------------------------------------------------------- symptom 2: property routes

    /// <summary>
    /// #440 symptom 2, FIXED: no structural-property route is registered over a
    /// convention-discovered navigation any more.
    /// <para>
    /// <c>BuildStructuralProperties</c> still subtracts only the PROFILE-DECLARED navigations — it
    /// runs while the EDM is being built and has no EDM to consult — so the undeclared navigation
    /// still survives in <c>StructuralProperties</c>. What changed is that route registration now
    /// subtracts the EDM's own navigation names from the set it iterates, exactly as #322 already
    /// did for the projection's member set. All seven templates that used to exist over the
    /// navigation are gone, and the undeclared profile is now byte-identical to the DECLARED
    /// control on every one of them (404 on both), which is the shape #440 called correct.
    /// </para>
    /// <para>
    /// The 404s here are ROUTE-ABSENCE 404s (no endpoint matches the template), not handler 404s:
    /// entity 1 exists, so a registered read route would answer 204/200 and a registered write
    /// route 204/400. Its sibling assertion below — a real structural property on the SAME entity
    /// set still answering non-404 on the same verbs — is what keeps that reading honest, and what
    /// would fail if this fix had emptied the property-route surface wholesale.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Symptom2_NoStructuralPropertyRouteIsRegisteredOverAnUndeclaredNavigation()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        async Task<HttpStatusCode> Send(HttpMethod method, string url, string? json = null)
        {
            using var req = new HttpRequestMessage(method, url);
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage r = await fx.Client.SendAsync(req);
            _out.WriteLine($"{method} {url} -> {(int)r.StatusCode}");
            return r.StatusCode;
        }

        // The seven templates #440 tabulated, over the UNDECLARED navigation. All gone.
        Assert.Equal(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Customer"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Customer/$value"));
        Assert.Equal(HttpStatusCode.NotFound,
            await Send(HttpMethod.Put, "/odata/W440Orders(1)/Customer", "{\"value\":null}"));
        Assert.Equal(HttpStatusCode.NotFound,
            await Send(HttpMethod.Patch, "/odata/W440Orders(1)/Customer", "{\"value\":null}"));
        Assert.Equal(HttpStatusCode.NotFound, await Send(HttpMethod.Delete, "/odata/W440Orders(1)/Customer"));

        // The DECLARED control, same CLR member, same row: unchanged, and now indistinguishable.
        Assert.Equal(HttpStatusCode.NotFound,
            await Send(HttpMethod.Get, "/odata/W440DeclaredOrders(1)/Customer"));

        // BOUNDING ASSERTION: a genuine structural property on the same entity set still has its
        // full route surface. Without this, "everything 404s" would pass vacuously if the fix had
        // subtracted too much.
        Assert.NotEqual(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Note"));
        Assert.NotEqual(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Note/$value"));
        Assert.NotEqual(HttpStatusCode.NotFound,
            await Send(HttpMethod.Patch, "/odata/W440Orders(1)/Note", "{\"value\":\"N2\"}"));

        // ...including the navigation's own FOREIGN KEY, which is a structural property and must
        // keep its routes. The EDM subtraction is by navigation NAME, so a name-adjacent scalar
        // ('CustomerId' vs 'Customer') must not be caught by it.
        Assert.NotEqual(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/CustomerId"));
    }

    // ------------------------------------------------------- #461: the write-side twin

    /// <summary>
    /// #461, the write-side twin of #446. <c>deepInsertNavPropsToStrip</c> was built from the
    /// profile-DECLARED navigation names, so a navigation the convention builder discovered and the
    /// profile never declared was not in the strip set — System.Text.Json bound it and it reached the
    /// <c>Post</c> handler intact, with <c>AllowDeepWrites</c> at its default of <c>false</c>. A
    /// handler doing <c>db.Add(model); SaveChanges();</c> persists those nested rows: the exact
    /// silent-partial-graph hazard the strip exists to prevent, on the most ordinary shape there is —
    /// <b>a profile that declares no navigations at all</b>, which is what <c>W440OrderProfile</c> is.
    /// <para>
    /// Asserted at the HANDLER, not on the response: #240 omits every EDM navigation from the POST
    /// echo whether it was stripped or not, so the wire says nothing either way. That is also why
    /// this went unnoticed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Symptom3_DeepInsertStrip_AlsoStripsAnUndeclaredConventionNavigation()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        W440OrderProfile.LastPosted = null;
        W440DeclaredOrderProfile.LastPosted = null;

        const string body = """
            { "Id": 0, "Note": "posted", "CustomerId": 7, "Customer": { "Id": 9, "Name": "C9" } }
            """;

        using var undeclaredContent = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage undeclared = await fx.Client.PostAsync(
            "/odata/W440Orders", undeclaredContent);
        _out.WriteLine($"undeclared: {(int)undeclared.StatusCode} {await undeclared.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Created, undeclared.StatusCode);

        Assert.NotNull(W440OrderProfile.LastPosted);
        // Before the fix this was a populated W440Customer.
        Assert.Null(W440OrderProfile.LastPosted!.Customer);

        // BOUNDING ASSERTION: the strip is one member, not a wiped graph — the scalars, including
        // the navigation's own foreign key, are untouched.
        Assert.Equal("posted", W440OrderProfile.LastPosted.Note);
        Assert.Equal(7, W440OrderProfile.LastPosted.CustomerId);

        // THE DECLARED CONTROL, same CLR model and same body: already correct before the fix, and
        // still correct. Declaration provenance no longer changes what the handler receives.
        using var declaredContent = new StringContent(body, Encoding.UTF8, "application/json");
        HttpResponseMessage declared = await fx.Client.PostAsync(
            "/odata/W440DeclaredOrders", declaredContent);
        _out.WriteLine($"declared:   {(int)declared.StatusCode} {await declared.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Created, declared.StatusCode);
        Assert.NotNull(W440DeclaredOrderProfile.LastPosted);
        Assert.Null(W440DeclaredOrderProfile.LastPosted!.Customer);
        Assert.Equal(7, W440DeclaredOrderProfile.LastPosted.CustomerId);
    }

    /// <summary>
    /// #457, the deep-UPDATE half of the same thing. Deep update (§11.4.3.1) has been documented
    /// out of scope since 1.0.0 (<c>docs/deep-insert.md</c>) but was never enforced, so <c>PUT</c>
    /// forwarded the nested graph to the handler and <c>PATCH</c> bound it into the
    /// <c>Delta&lt;TModel&gt;</c>. This asserts the enforcement reuses the SAME strip set — the
    /// profile-declared navigations UNIONED with the EDM's (#461) — rather than a second one
    /// derived from the declared names alone, which is the bug #461 fixed on POST.
    /// <para>
    /// At the handler, not on the wire: #240 omits every EDM navigation from the echo either way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Symptom3_DeepUpdateStrip_AlsoStripsAnUndeclaredConventionNavigation_OnPutAndPatch()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        W440OrderProfile.LastPut = null;
        W440OrderProfile.LastPatchChangedProperties = null;

        const string putBody = """
            { "Id": 1, "Note": "put", "CustomerId": 7, "Customer": { "Id": 9, "Name": "C9" } }
            """;

        using var putContent = new StringContent(putBody, Encoding.UTF8, "application/json");
        HttpResponseMessage put = await fx.Client.PutAsync("/odata/W440Orders(1)", putContent);
        _out.WriteLine($"put:   {(int)put.StatusCode} {await put.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.NotNull(W440OrderProfile.LastPut);
        // Before the fix this was a populated W440Customer.
        Assert.Null(W440OrderProfile.LastPut!.Customer);
        // BOUNDING: one member, not a wiped graph — the scalars and the navigation's own foreign
        // key are untouched.
        Assert.Equal("put", W440OrderProfile.LastPut.Note);
        Assert.Equal(7, W440OrderProfile.LastPut.CustomerId);

        const string patchBody = """
            { "Note": "patched", "CustomerId": 7, "Customer": { "Id": 9, "Name": "C9" } }
            """;

        using var patchContent = new StringContent(patchBody, Encoding.UTF8, "application/json");
        HttpResponseMessage patch = await fx.Client.PatchAsync("/odata/W440Orders(1)", patchContent);
        _out.WriteLine($"patch: {(int)patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.NotNull(W440OrderProfile.LastPatchChangedProperties);
        // Before the fix 'Customer' was in this list, so delta.Patch(existing) wrote it.
        Assert.DoesNotContain("Customer", W440OrderProfile.LastPatchChangedProperties!);
        // BOUNDING: the scalars the same body carried are still in the delta, foreign key included.
        Assert.Contains("Note", W440OrderProfile.LastPatchChangedProperties!);
        Assert.Contains("CustomerId", W440OrderProfile.LastPatchChangedProperties!);
    }

    // --------------------------------------------------------------- warning content

    /// <summary>
    /// The message content, pinned the way the <c>$expand</c> diagnostic
    /// (<c>WarnUnboundedBareExpand</c>) is: it must name the entity set, the navigation, the model,
    /// the declaration it is missing, BOTH surviving consequences, and BOTH remedies.
    /// </summary>
    [Fact]
    public async Task Warning_NamesTheEntitySet_TheNavigation_TheConsequences_AndBothRemedies()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        string warning = Assert.Single(UndeclaredNavWarnings(capture));
        _out.WriteLine(warning);

        // WHO and WHAT.
        Assert.Contains("'W440Orders'", warning, StringComparison.Ordinal);
        Assert.Contains("'Customer'", warning, StringComparison.Ordinal);
        Assert.Contains("'W440Order'", warning, StringComparison.Ordinal);
        Assert.Contains("convention builder discovered", warning, StringComparison.Ordinal);
        Assert.Contains("never declared with HasOptional/HasRequired/HasMany", warning, StringComparison.Ordinal);

        // THE SURVIVING STATEMENT: $metadata advertises a navigation the entity set will never
        // serve. It is no longer a defect report — both symptoms are fixed — it is the
        // advertise/serve disagreement itself, which only the developer can close.
        Assert.Contains("will never serve it", warning, StringComparison.Ordinal);
        Assert.Contains("'?$expand=Customer' is accepted and answers 200 with the navigation OMITTED",
            warning, StringComparison.Ordinal);
        Assert.Contains("no 'GET /W440Orders({key})/Customer' behind it", warning, StringComparison.Ordinal);

        // #461: the WRITE half. "will never serve it" speaks only of reads, and the message was
        // incomplete in both directions — before #461 the write path did not merely fail to serve
        // the navigation, it quietly accepted a nested value for it and forwarded that to Post. The
        // sentence naming the (now correct) write behaviour is added in the commit that made it true.
        Assert.Contains("will never accept a value for it", warning, StringComparison.Ordinal);
        // #457: widened from "a POST body ... before the Post handler runs". Deep update
        // (§11.4.3.1) is enforced now, so naming POST alone would understate the behaviour on
        // exactly the two verbs that used to get it wrong.
        Assert.Contains(
            "nested value for it in a POST, PUT or PATCH body is discarded before the write handler runs",
            warning, StringComparison.Ordinal);
        Assert.Contains("AllowDeepWrites", warning, StringComparison.Ordinal);

        // BOTH remedies.
        Assert.Contains("Declare it with HasOptional/HasRequired/HasMany", warning, StringComparison.Ordinal);
        Assert.Contains("Ignore()", warning, StringComparison.Ordinal);

        // NOT a consequence any more, and the message must not say otherwise. Three generations of
        // this list have now been retired, each in the commit that closed the behaviour:
        //   #322            — pushdown disqualification.
        //   #440 symptom 2  — structural-property routes over the navigation, reads and writes.
        //   #440 symptom 1  — "$expand answers 200 with null". It answers with the navigation
        //                     OMITTED now, which asserts nothing about the relationship.
        // #313 stage 3 shipped a diagnostic that outlived what it described; these are the guards
        // that stop this one from doing the same.
        Assert.DoesNotContain("pushdown", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$filter", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Include", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyAccessEnabled", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("PUT/PATCH/DELETE", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("/$value", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("with null", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("empty array", warning, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- targeting

    /// <summary>
    /// One warning per (entity set, navigation) hit and no more. The registration under test has
    /// FOUR profiles and only ONE qualifies: a model with no navigation is silent; the same
    /// undeclared navigation on a profile where the remaining consequence is not reachable ($expand
    /// off) is silent; and the declared control is silent.
    /// </summary>
    [Fact]
    public async Task Warning_FiresOncePerHit_AndIsSilentOnEveryProfileWithNoReachableSymptom()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        string[] hits = UndeclaredNavWarnings(capture).ToArray();
        foreach (string w in hits) _out.WriteLine(w);

        Assert.Single(hits);
        Assert.Contains("'W440Orders'", hits[0], StringComparison.Ordinal);
        Assert.DoesNotContain(hits, w => w.Contains("W440Invoices", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, w => w.Contains("W440Plains", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, w => w.Contains("W440DeclaredOrders", StringComparison.Ordinal));
    }

    /// <summary>
    /// A registration whose every navigation is declared emits nothing at all — the diagnostic is
    /// off by construction for a profile that already did the right thing.
    /// </summary>
    [Fact]
    public async Task Warning_IsSilentForARegistrationThatDeclaresEveryNavigation()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(
            connection, capture,
            b =>
            {
                b.AddEntitySetProfile<W440DeclaredOrderProfile>();
                b.AddEntitySetProfile<W440PlainProfile>();
            });

        Assert.Empty(UndeclaredNavWarnings(capture));
    }

    /// <summary>
    /// It is a WARNING, not a throw: <c>MapOhData()</c> completes and the affected entity set still
    /// serves. Startup must not break for the ordinary EF Core reference navigation.
    /// </summary>
    [Fact]
    public async Task Warning_DoesNotThrow_AndTheEntitySetStillServes()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/W440Orders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"N1\"", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

// #466 (PR #477 review F2): the raw-$levels budget must never reach #440's omission arm.
//
// #466 unioned the raw substrate's $levels navigation names onto the pushed set and passed the union
// to all three stages -- including ExpandLevelAsync, where the set has exactly one use: keeping an
// undeclared navigation ONLY when it was pushed as a $levels expand and is therefore genuinely loaded.
//
// Membership is decided PER LEVEL; the set is FLAT and keyed by name. So a `Children` that is
// ServeRaw-with-an-opinion at depth 2 also matched the UNDECLARED `Children` at the ROOT, bypassed the
// omission arm and emitted "Children": [] there -- under a 200, on a default configuration, since the
// union is built whenever the clause carries a $levels anywhere.
//
// Fixed by keeping the union for the two SERIALIZATION stages and passing the PUSHED set to
// ExpandLevelAsync. The raw set never needed to reach it: a raw name enters only where some candidate
// has an opinion, and a navigation with an opinion never reaches the no-opinion arm.
public sealed class Issue466NavOmissionRegressionTests
{
    private static void ConfigureCollision(OhDataBuilder b)
    {
        b.AddEntitySetProfile<W440HubProfile>();
        b.AddEntitySetProfile<W440BranchProfile>();
    }

    private static async Task<JsonElement> RootAsync(TestFixture fx, string query, JsonDocument[] keep)
    {
        HttpResponseMessage resp = await fx.Client.GetAsync($"/odata/W440Hubs?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        keep[0] = doc;
        return doc.RootElement.GetProperty("value")[0];
    }

    /// <summary>
    /// The control. With no <c>$levels</c> anywhere in the request the union is never even built
    /// (ClauseHasLevels gates it), so this passed on the pre-fix head too — it is here to prove the
    /// regression test below is about the collision and not about #440 being broken generally.
    /// </summary>
    [Fact]
    public async Task Control_UndeclaredNavAlone_IsOmitted()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureCollision);

        var keep = new JsonDocument[1];
        JsonElement root = await RootAsync(fx, "$expand=Children", keep);
        Assert.False(root.TryGetProperty("Children", out _));
        keep[0].Dispose();
    }

    /// <summary>
    /// FAILS WITHOUT THE FIX: the root's undeclared <c>Children</c> comes back as <c>[]</c> because a
    /// <c>$levels</c> two levels away put the same NAME into the flat budget set.
    /// </summary>
    [Fact]
    public async Task DeepLevelsOnACollidingName_DoesNotResurrectTheUndeclaredRootNav()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureCollision);

        var keep = new JsonDocument[1];
        JsonElement root = await RootAsync(fx, "$expand=Children,Other($expand=Children($levels=2))", keep);

        Assert.False(
            root.TryGetProperty("Children", out JsonElement leaked),
            $"the undeclared root navigation must stay omitted; got {leaked}");
        keep[0].Dispose();
    }

    /// <summary>
    /// The bounding half, so the assertion above cannot be satisfied by disabling #466 altogether:
    /// the SAME request must still serve both levels of the deep <c>$levels</c>, off the raw
    /// substrate, through the union that no longer reaches the omission arm.
    /// FAILS WITHOUT #466: the second level is stripped.
    /// </summary>
    [Fact]
    public async Task DeepLevelsOnACollidingName_StillServesEveryLevel()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureCollision);

        var keep = new JsonDocument[1];
        JsonElement root = await RootAsync(fx, "$expand=Children,Other($expand=Children($levels=2))", keep);

        JsonElement b1 = root.GetProperty("Other")[0];
        Assert.Equal("B1", b1.GetProperty("Label").GetString());
        JsonElement b2 = b1.GetProperty("Children")[0];
        Assert.Equal("B2", b2.GetProperty("Label").GetString());
        JsonElement b3 = b2.GetProperty("Children")[0];
        Assert.Equal("B3", b3.GetProperty("Label").GetString());
        // The budget is a budget: level 3 terminates the recursion.
        Assert.False(b3.TryGetProperty("Children", out _));
        keep[0].Dispose();
    }
}
