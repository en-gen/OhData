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
// on $expand, and registered structural-PROPERTY routes (reads, and writes when Patch is configured)
// over a navigation. Both share #322's root cause — the profile's navigation set and the EDM's
// disagree — which #322's fix reconciled for the QUERY PLAN only.
//
// BOTH SYMPTOMS ARE FIXED HERE.
//   Symptom 2 — route registration now subtracts the EDM's own navigation names from the set it
//   iterates, exactly as #322 already did for the projection's member set, so no property route is
//   registered over a navigation.
//   Symptom 1 — ExpandLevelAsync's ServeRaw branch separates its two populations. A navigation some
//   candidate DECLARED keeps its raw value (that value is loaded data). One no candidate declares or
//   routes is REMOVED, because nothing ever chose to load it: OData JSON Format v4.01 §8.3 makes an
//   inline navigation value the representation of an EXPANDED navigation, so `"Customer": null`
//   asserts the relationship is empty — which the server never determined. §8.1's non-expanded
//   representation (the navigation link, omitted under metadata=minimal) is the honest one.
// NavigationPropertyNames is untouched by both (see the fix sites for why).
//
// The warning stays, because the disagreement outlives the symptoms and only the developer can close
// it — $metadata still advertises a navigation this entity set will never serve. But it states ONLY
// what is still true, and each retired consequence came out in the same commit as its fix. The
// content test carries an explicit guard against every one of them.
//
// This suite pins three things:
//   1. the SYMPTOMS, now as fixes — with bounding assertions on both, so neither can pass vacuously:
//      a real structural property (and the navigation's own FK) still has its routes, and a $levels
//      expand of an undeclared self-referential navigation — the one shape that IS pushed and loaded
//      — still serves its data,
//   2. the warning's exact CONTENT, and
//   3. its TARGETING: a profile with no undeclared navigation, and a profile on which the remaining
//      consequence is not reachable, stay silent.

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

public sealed class W440DbContext : DbContext
{
    public W440DbContext(DbContextOptions<W440DbContext> options) : base(options) { }

    public DbSet<W440Order> Orders => Set<W440Order>();
    public DbSet<W440Customer> Customers => Set<W440Customer>();
    public DbSet<W440Invoice> Invoices => Set<W440Invoice>();
    public DbSet<W440Plain> Plains => Set<W440Plain>();
    public DbSet<W440Node> Nodes => Set<W440Node>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<W440Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
        b.Entity<W440Invoice>().HasOne(i => i.Payer).WithMany().HasForeignKey(i => i.PayerId);
        b.Entity<W440Node>().HasOne(n => n.Parent).WithMany(n => n.Children).HasForeignKey(n => n.ParentId);
    }
}

/// <summary>Declares nothing; both <c>Parent</c> and <c>Children</c> are convention-discovered.</summary>
public sealed class W440NodeProfile : EntitySetProfile<int, W440Node>
{
    public W440NodeProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Nodes";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Nodes.AsQueryable());
    }
}

/// <summary>
/// The affected shape: $expand enabled, GetById AND Patch configured, PropertyAccessEnabled left at
/// its default of <c>true</c>. Both #440 symptoms were live here before the fixes.
/// </summary>
public sealed class W440OrderProfile : EntitySetProfile<int, W440Order>
{
    public W440OrderProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440Orders";
        ExpandEnabled = true; SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Orders.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.Orders.FirstOrDefault(o => o.Id == id));
        Patch = (id, delta, _) =>
        {
            W440Order? existing = db.Orders.FirstOrDefault(o => o.Id == id);
            if (existing is null) return Task.FromResult<W440Order?>(null);
            delta.Patch(existing);
            db.SaveChanges();
            return Task.FromResult<W440Order?>(existing);
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
        GetQueryable = _ => Task.FromResult(db.Invoices.AsQueryable());
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
        GetQueryable = _ => Task.FromResult(db.Plains.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.Plains.FirstOrDefault(p => p.Id == id));
    }
}

/// <summary>The remedy, applied: the same navigation, DECLARED. Must stay silent.</summary>
public sealed class W440DeclaredOrderProfile : EntitySetProfile<int, W440Order>
{
    public W440DeclaredOrderProfile(W440DbContext db) : base(x => x.Id)
    {
        EntitySetName = "W440DeclaredOrders";
        ExpandEnabled = true; SelectEnabled = true;
        GetQueryable = _ => Task.FromResult(db.Orders.AsQueryable());
        GetById = (id, _) => Task.FromResult(db.Orders.FirstOrDefault(o => o.Id == id));
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
