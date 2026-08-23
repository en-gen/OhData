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

// #440: a convention-discovered navigation the profile never declared produces WRONG DATA UNDER 200
// on $expand, and registers structural-PROPERTY routes (reads, and writes when Patch is configured)
// over a navigation. Both share #322's root cause — the profile's navigation set and the EDM's
// disagree — but #322's fix reconciles them for the QUERY PLAN only, so both symptoms survive it.
//
// The framework can detect the disagreement but must not decide it: declaring the navigation and
// hiding it are both valid, and only the developer knows which. So the remedy is a startup WARNING,
// not a throw — a throw would break startup for every adopter with a plain EF Core reference
// navigation on a profiled entity, which is the common case, with no migration but editing every
// profile.
//
// This suite pins three things:
//   1. the two symptoms PERSIST after #322's projection fix (which is why the warning exists — if
//      they ever stop persisting, the warning is lying and must be re-scoped),
//   2. the warning's exact CONTENT, and
//   3. its TARGETING: a profile with no undeclared navigation, and a profile on which neither
//      symptom is reachable, stay silent.

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

public sealed class W440DbContext : DbContext
{
    public W440DbContext(DbContextOptions<W440DbContext> options) : base(options) { }

    public DbSet<W440Order> Orders => Set<W440Order>();
    public DbSet<W440Customer> Customers => Set<W440Customer>();
    public DbSet<W440Invoice> Invoices => Set<W440Invoice>();
    public DbSet<W440Plain> Plains => Set<W440Plain>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<W440Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
        b.Entity<W440Invoice>().HasOne(i => i.Payer).WithMany().HasForeignKey(i => i.PayerId);
    }
}

/// <summary>
/// The affected shape: $expand enabled, GetById AND Patch configured, PropertyAccessEnabled left at
/// its default of <c>true</c>. Both #440 symptoms are live here.
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
/// The SAME undeclared navigation, on a profile where NEITHER symptom is reachable: $expand off,
/// property access off, no GetById and no Patch. The disagreement is still in $metadata, but there
/// is no defect to report, so this profile must stay silent.
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
    /// #440 symptom 1, and the reason the warning exists: <c>$expand</c> of the undeclared
    /// navigation answers <b>200 with null</b> for a row whose related entity exists. #322's
    /// projection fix does NOT change this — the navigation is still absent from
    /// <c>NavigationPropertyNames</c>, which is what <c>pushdownExpandNavs</c> is built from, so
    /// nothing ever loads it. If this test ever starts failing, the warning is claiming a
    /// consequence that no longer holds and must be re-scoped.
    /// </summary>
    [Fact]
    public async Task Symptom1_ExpandOfAnUndeclaredNavigation_StillReturnsNullUnder200()
    {
        var capture = new WarningCapture();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using TestFixture fx = await W440Harness.BuildAsync(connection, capture, ConfigureAll);

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/W440Orders?$expand=Customer");
        string body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"undeclared: {(int)resp.StatusCode} {body}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement row = doc.RootElement.GetProperty("value")[0];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("Customer").ValueKind);

        // The declared control over the SAME CLR model and the SAME row loads it. The difference
        // is provenance and nothing else.
        HttpResponseMessage ok = await fx.Client.GetAsync("/odata/W440DeclaredOrders?$expand=Customer");
        string okBody = await ok.Content.ReadAsStringAsync();
        _out.WriteLine($"declared:   {(int)ok.StatusCode} {okBody}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"C7\"", okBody, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- symptom 2: property routes

    /// <summary>
    /// #440 symptom 2: <c>BuildStructuralProperties</c> subtracts only the PROFILE-DECLARED
    /// navigations, so the undeclared one survives as a structural property and
    /// <c>PropertyAccessEnabled</c> (default <c>true</c>) registers property routes over it — reads
    /// alongside <c>GetById</c>, and PUT/PATCH/DELETE alongside <c>Patch</c>. #322's fix touches
    /// only the projection's member set, not <c>StructuralProperties</c>, so this is unchanged.
    /// The declared control has no such routes (404), which is the correct shape.
    /// </summary>
    [Fact]
    public async Task Symptom2_StructuralPropertyRoutesStillRegisterOverAnUndeclaredNavigation()
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

        // READ routes exist over the navigation (they would 404 if they were not registered).
        Assert.NotEqual(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Customer"));
        Assert.NotEqual(HttpStatusCode.NotFound, await Send(HttpMethod.Get, "/odata/W440Orders(1)/Customer/$value"));

        // WRITE routes exist too, because Patch is configured — a structural-property write aimed
        // at a navigation.
        foreach (HttpMethod method in new[] { HttpMethod.Put, HttpMethod.Patch })
        {
            HttpStatusCode code = await Send(method, "/odata/W440Orders(1)/Customer", "{\"value\":null}");
            Assert.NotEqual(HttpStatusCode.NotFound, code);
            Assert.NotEqual(HttpStatusCode.MethodNotAllowed, code);
        }
        HttpStatusCode del = await Send(HttpMethod.Delete, "/odata/W440Orders(1)/Customer");
        Assert.NotEqual(HttpStatusCode.NotFound, del);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, del);

        // The DECLARED control: no property route over a navigation, which is correct.
        Assert.Equal(HttpStatusCode.NotFound,
            await Send(HttpMethod.Get, "/odata/W440DeclaredOrders(1)/Customer"));
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

        // CONSEQUENCE 1 — wrong data under a success status.
        Assert.Contains("'?$expand=Customer' answers 200 with null", warning, StringComparison.Ordinal);

        // CONSEQUENCE 2 — property routes, reads and writes.
        Assert.Contains("PropertyAccessEnabled", warning, StringComparison.Ordinal);
        Assert.Contains("'GET /W440Orders({key})/Customer' and '/$value'", warning, StringComparison.Ordinal);
        Assert.Contains("PUT/PATCH/DELETE", warning, StringComparison.Ordinal);

        // BOTH remedies.
        Assert.Contains("Declare it with HasOptional/HasRequired/HasMany", warning, StringComparison.Ordinal);
        Assert.Contains("Ignore()", warning, StringComparison.Ordinal);

        // NOT a consequence any more (#322): pushdown disqualification. Naming a consequence the
        // framework already fixed is how a diagnostic starts lying.
        Assert.DoesNotContain("pushdown", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$filter", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Include", warning, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------- targeting

    /// <summary>
    /// One warning per (entity set, navigation) hit and no more. The registration under test has
    /// FOUR profiles and only ONE qualifies: a model with no navigation is silent; the same
    /// undeclared navigation on a profile where neither symptom is reachable ($expand off, property
    /// access off, no GetById/Patch) is silent; and the declared control is silent.
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
