using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

// #323: "$expand pushdown silently returns [] for standard bidirectional EF models". Architecture-pass
// SPEC (frozen, probe-backed): a related type is materialized through the SAME member-init projection
// intermediate levels and $levels already used, at EVERY level INCLUDING leaves (Change A,
// BuildShapedNavAccess). No entity is then materialized inside a pushed projection unless it is a
// fresh POCO, so a serialization cycle is structurally impossible on this path regardless of
// back-references, tracked-entity fixup, or model shape — the static guard (Change B,
// BuildExpandNavBinding) is narrowed accordingly to defer only a related type that is BOTH cyclic AND
// NOT member-init-projectable. This suite proves the fix against a range of bidirectional/cyclic
// shapes that used to silently defer to EDM-only (or, for some shapes, 500) and now genuinely push
// down and JOIN. See also IncludeFallbackSqliteTests.cs (Change C — the #305 Include-fallback path
// keeps its OWN conservative guard) and ExpandPushdownSqliteTests.cs
// (BidirectionalNav_Expand_PushesDown_WithJoin — the simplest single-hop bidirectional pin).

// ── T1-T2: Author/Book, bidirectional, expanded back-reference at 3 and 5 levels ──────────────────

public sealed class BxAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<BxBook> Books { get; set; } = new();
}

public sealed class BxBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
    public BxAuthor? Author { get; set; } // back-reference — bidirectional
}

// A SEPARATE CLR type pair for the 5-level chain (T2), deliberately NOT also exposed by a
// delegate-backed sibling profile: BxAuthor (above) intentionally IS exposed twice (BxAuthorProfile /
// BxSecureAuthorProfile, for T20/T21's delegate-safety pin), and the Model B declaring-set-authority
// guard (#292/#293, untouched by #323) correctly BLANKS a nested intermediate level whose element type
// is exposed by disagreeing sets — exactly as it must. Reusing BxAuthor/BxBook for the 5-level chain
// would trip that (correct, intentional) guard at the "Author" intermediate level and defer the whole
// branch, which would test Model B, not Change A. BxDeepAuthor/BxDeepBook isolates the two concerns.
public sealed class BxDeepAuthor
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<BxDeepBook> Books { get; set; } = new();
}

public sealed class BxDeepBook
{
    public int Id { get; set; }
    public int AuthorId { get; set; }
    public string Title { get; set; } = "";
    public BxDeepAuthor? Author { get; set; } // back-reference — bidirectional
}

// ── T3: Order/OrderLine, bidirectional, nested $top/$count ────────────────────────────────────────

public sealed class BxOrder
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public List<BxOrderLine> Lines { get; set; } = new();
}

public sealed class BxOrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string Sku { get; set; } = "";
    public BxOrder? Order { get; set; } // back-reference — bidirectional
}

// ── T4/T6: root-typed AND mid-ancestor-typed audit columns (neither is the real parent FK) ────────

public sealed class AcRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<AcMid> Mids { get; set; } = new();
}

public sealed class AcMid
{
    public int Id { get; set; }
    public int RootId { get; set; }
    public string Name { get; set; } = "";
    public List<AcLeaf> Leaves { get; set; } = new();
}

public sealed class AcLeaf
{
    public int Id { get; set; }
    public int MidId { get; set; }
    public string Name { get; set; } = "";

    // Audit-style secondary references — REAL EF navigations (their own FK), distinct from the actual
    // parent link (MidId/the Mids->Leaves relationship). AuditRoot is root-typed (T4); AuditMid is
    // mid-ancestor-typed (T6) — the static guard must be defeated by EITHER, at EITHER hop distance,
    // the SAME way: Change B never even evaluates TypeHasNavigationTo once the element type is
    // member-init-projectable.
    public int? AuditRootId { get; set; }
    public AcRoot? AuditRoot { get; set; }
    public int? AuditMidId { get; set; }
    public AcMid? AuditMid { get; set; }
}

// ── T5: grandparent-skip, back-reference EXPANDED — harvested from branch
// fix/315-harden-typehasnavigationto (GrandparentSkipCycleExpandPushdownTests.cs). #315 is SUPERSEDED
// (spec-frozen decision): its target shape already returns correct data once Change A/B land, so its
// widened ancestor-chain guard is unnecessary — only its fixtures are harvested here, as a regression
// pin asserting CORRECT DATA (never deferral, never 500), not the widened-guard behavior #315 itself
// would have produced.

public sealed class GsRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<GsMid> Mids { get; set; } = new();
}

public sealed class GsMid
{
    public int Id { get; set; }
    public int RootId { get; set; }
    public string Name { get; set; } = "";
    public List<GsLeaf> Leaves { get; set; } = new();
}

public sealed class GsLeaf
{
    public int Id { get; set; }
    public int MidId { get; set; }
    public string Name { get; set; } = "";

    // Back-reference SKIPS the immediate parent (GsMid) and points straight at the GRANDPARENT
    // (GsRoot) instead.
    public int RootId2 { get; set; }
    public GsRoot? Root { get; set; }
}

// ── T7: interface-typed audit member, combined with [NotMapped]/get-only/computed members (T8) ────

public interface IBxAuditable
{
    int Id { get; }
}

public sealed class IfRoot : IBxAuditable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<IfChild> Children { get; set; } = new();
}

public sealed class IfChild
{
    public int Id { get; set; }
    public int RootId { get; set; }
    public string Name { get; set; } = "";

    // Interface-typed audit reference: IfRoot implements IBxAuditable, so TypeHasNavigationTo's
    // broadened (base/interface-aware) assignability check would flag this as a back-reference too —
    // NotMapped because EF Core cannot map an interface-typed navigation without extra configuration,
    // which ALSO exercises the "public CLR property that is not an EDM structural property" wire
    // change (#323): it must not be materialized on the projected leaf.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IBxAuditable? AuditRef { get; set; }

    // Get-only computed property derived purely from a bound scalar (Name) — must still serialize
    // correctly on a leaf projected through the fresh-POCO member-init (the getter runs against the
    // projected instance, which DOES have Name bound).
    public string Shout => Name.ToUpperInvariant();
}

// ── P1: sibling cross-reference — the cycle closes entirely BETWEEN TWO LEAVES, neither of which is
// the root (adversarial review, #323 fold-in). CyInvoice expands Customer and Orders as two SIBLING
// leaves (no nesting between them in the $expand tree); Customer.Orders <-> Order.Customer is a
// self-contained bidirectional pair that never references CyInvoice at all. With Change A reverted,
// BuildExpandNavBinding's guard (Change B) checks each leaf's element type against the OWNER
// (CyInvoice) only — CyCustomer and CyOrder have no navigation back to CyInvoice, so the guard admits
// both regardless, and both would be materialized BARE. EF Core's own relationship fixup then wires
// Order.Customer and Customer.Orders to each other (both rows tracked in the same DbContext), closing
// a cycle System.Text.Json cannot serialize — with the root never involved.
public sealed class CyInvoice
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int CustomerId { get; set; }
    public CyCustomer? Customer { get; set; }
    public List<CyOrder> Orders { get; set; } = new();
}

public sealed class CyCustomer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CyOrder> Orders { get; set; } = new(); // <-> CyOrder.Customer — NEVER references CyInvoice
}

public sealed class CyOrder
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int CustomerId { get; set; }
    public string Sku { get; set; } = "";
    public CyCustomer? Customer { get; set; } // back-reference to CyCustomer — closes the cycle WITHOUT CyInvoice
}

// ── P2: self-referential leaf element type — the cycle closes ENTIRELY within the expanded leaf's own
// element type, with no reference to the root at all (adversarial review, #323 fold-in). CyOrg expands
// Employees as a single leaf (no nested $expand); CyEmployee.Manager/CyEmployee.Reports are a
// self-referential pair (a manager's Reports collection <-> a report's Manager reference) that never
// mentions CyOrg. With Change A reverted, the leaf is materialized bare, and EF's relationship fixup
// wires Manager/Reports among the tracked CyEmployee rows, closing a self-referential cycle entirely
// inside the leaf element type.
public sealed class CyOrg
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<CyEmployee> Employees { get; set; } = new();
}

public sealed class CyEmployee
{
    public int Id { get; set; }
    public int OrgId { get; set; }
    public string Name { get; set; } = "";
    public int? ManagerId { get; set; }
    public CyEmployee? Manager { get; set; }
    public List<CyEmployee> Reports { get; set; } = new(); // <-> Manager — NEVER references CyOrg
}

public sealed class BidirectionalExpandDbContext : DbContext
{
    public BidirectionalExpandDbContext(DbContextOptions<BidirectionalExpandDbContext> options) : base(options) { }

    public DbSet<BxAuthor> BxAuthors => Set<BxAuthor>();
    public DbSet<BxBook> BxBooks => Set<BxBook>();
    public DbSet<BxDeepAuthor> BxDeepAuthors => Set<BxDeepAuthor>();
    public DbSet<BxDeepBook> BxDeepBooks => Set<BxDeepBook>();
    public DbSet<BxOrder> BxOrders => Set<BxOrder>();
    public DbSet<BxOrderLine> BxOrderLines => Set<BxOrderLine>();
    public DbSet<AcRoot> AcRoots => Set<AcRoot>();
    public DbSet<AcMid> AcMids => Set<AcMid>();
    public DbSet<AcLeaf> AcLeaves => Set<AcLeaf>();
    public DbSet<GsRoot> GsRoots => Set<GsRoot>();
    public DbSet<GsMid> GsMids => Set<GsMid>();
    public DbSet<GsLeaf> GsLeaves => Set<GsLeaf>();
    public DbSet<IfRoot> IfRoots => Set<IfRoot>();
    public DbSet<IfChild> IfChildren => Set<IfChild>();
    public DbSet<CyInvoice> CyInvoices => Set<CyInvoice>();
    public DbSet<CyCustomer> CyCustomers => Set<CyCustomer>();
    public DbSet<CyOrder> CyOrders => Set<CyOrder>();
    public DbSet<CyOrg> CyOrgs => Set<CyOrg>();
    public DbSet<CyEmployee> CyEmployees => Set<CyEmployee>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BxAuthor>().HasMany(a => a.Books).WithOne(bk => bk.Author!).HasForeignKey(bk => bk.AuthorId);
        b.Entity<BxDeepAuthor>().HasMany(a => a.Books).WithOne(bk => bk.Author!).HasForeignKey(bk => bk.AuthorId);
        b.Entity<BxOrder>().HasMany(o => o.Lines).WithOne(l => l.Order!).HasForeignKey(l => l.OrderId);

        b.Entity<AcRoot>().HasMany(r => r.Mids).WithOne().HasForeignKey(m => m.RootId);
        b.Entity<AcMid>().HasMany(m => m.Leaves).WithOne().HasForeignKey(l => l.MidId);
        b.Entity<AcLeaf>().HasOne(l => l.AuditRoot).WithMany().HasForeignKey(l => l.AuditRootId);
        b.Entity<AcLeaf>().HasOne(l => l.AuditMid).WithMany().HasForeignKey(l => l.AuditMidId);

        b.Entity<GsRoot>().HasMany(r => r.Mids).WithOne().HasForeignKey(m => m.RootId);
        b.Entity<GsMid>().HasMany(m => m.Leaves).WithOne().HasForeignKey(l => l.MidId);
        b.Entity<GsLeaf>().HasOne(l => l.Root).WithMany().HasForeignKey(l => l.RootId2);

        b.Entity<IfRoot>().HasMany(r => r.Children).WithOne().HasForeignKey(c => c.RootId);
        // AuditRef is [NotMapped]; nothing to configure.

        // P1: CyInvoice -> Customer (single) and -> Orders (collection) are two SIBLING navigations;
        // the cycle lives entirely in Customer <-> Order (neither references CyInvoice).
        b.Entity<CyInvoice>().HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId);
        b.Entity<CyInvoice>().HasMany(i => i.Orders).WithOne().HasForeignKey(o => o.InvoiceId);
        b.Entity<CyCustomer>().HasMany(c => c.Orders).WithOne(o => o.Customer!).HasForeignKey(o => o.CustomerId);

        // P2: CyOrg -> Employees; the cycle lives entirely in Manager <-> Reports (self-referential on
        // CyEmployee, never referencing CyOrg).
        b.Entity<CyOrg>().HasMany(o => o.Employees).WithOne().HasForeignKey(e => e.OrgId);
        b.Entity<CyEmployee>().HasOne(e => e.Manager).WithMany(m => m.Reports).HasForeignKey(e => e.ManagerId);
    }
}

// ── Profiles ────────────────────────────────────────────────────────────────────────────────────

public sealed class BxAuthorProfile : EntitySetProfile<int, BxAuthor>
{
    public BxAuthorProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BxAuthors";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.BxAuthors.AsQueryable());
        HasMany(x => x.Books); // delegate-less, bidirectional
    }
}

public sealed class BxDeepAuthorProfile : EntitySetProfile<int, BxDeepAuthor>
{
    public BxDeepAuthorProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BxDeepAuthors";
        ExpandEnabled = true;
        OrderByEnabled = true;
        MaxExpansionDepth = 5; // room for the 5-level (4-nesting) chain, T2
        GetQueryable = _ => Task.FromResult(db.BxDeepAuthors.AsQueryable());
        HasMany(x => x.Books); // delegate-less, bidirectional — NOT exposed by any other profile
    }
}

public sealed class BxOrderProfile : EntitySetProfile<int, BxOrder>
{
    public BxOrderProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "BxOrders";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.BxOrders.AsQueryable());
        HasMany(x => x.Lines); // delegate-less, bidirectional
    }
}

public sealed class AcRootProfile : EntitySetProfile<int, AcRoot>
{
    public AcRootProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "AcRoots";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.AcRoots.AsQueryable());
        HasMany(x => x.Mids); // delegate-less → level 1 pushable, folds Leaves in at level 2
    }
}

public sealed class GsRootProfile : EntitySetProfile<int, GsRoot>
{
    public GsRootProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "GsRoots";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.GsRoots.AsQueryable());
        HasMany(x => x.Mids);
    }
}

public sealed class IfRootProfile : EntitySetProfile<int, IfRoot>
{
    public IfRootProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "IfRoots";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.IfRoots.AsQueryable());
        HasMany(x => x.Children); // delegate-less; Children's element type carries an
                                  // interface-typed back-reference (AuditRef)
    }
}

// P1: sibling cross-reference — Customer and Orders are two independent, delegate-less leaves off the
// SAME root; the cycle between them is invisible to a guard that only checks each leaf against the root.
public sealed class CyInvoiceProfile : EntitySetProfile<int, CyInvoice>
{
    public CyInvoiceProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "CyInvoices";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.CyInvoices.AsQueryable());
        HasOptional(x => x.Customer!); // delegate-less, single-valued leaf
        HasMany(x => x.Orders); // delegate-less, collection leaf
    }
}

// P2: self-referential leaf element type — Employees' own element type (CyEmployee) is cyclic via
// Manager/Reports, with no reference to CyOrg at all.
public sealed class CyOrgProfile : EntitySetProfile<int, CyOrg>
{
    public CyOrgProfile(BidirectionalExpandDbContext db) : base(x => x.Id)
    {
        EntitySetName = "CyOrgs";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.CyOrgs.AsQueryable());
        HasMany(x => x.Employees); // delegate-less; element type is self-referential (Manager/Reports)
    }
}

// Non-EF (T22): same bidirectional shape, but backed by a plain in-memory List<T>.AsQueryable(). Must
// stay EDM-only for pushdown purposes exactly as before #323 — the EF-provider gate (ResolveEfCoreAssembly)
// is untouched by this fix, so a non-EF source can never engage the projection path at all, regardless
// of whether the related type is now admissible by the narrowed static guard.
public sealed class InMemoryBxAuthorProfile : EntitySetProfile<int, BxAuthor>
{
    private static readonly List<BxAuthor> _authors = new()
    {
        new BxAuthor
        {
            Id = 1, Name = "InMemAuthor1",
            Books = { new BxBook { Id = 10, AuthorId = 1, Title = "InMemBook1" } },
        },
    };

    public InMemoryBxAuthorProfile() : base(x => x.Id)
    {
        EntitySetName = "InMemoryBxAuthors";
        ExpandEnabled = true;
        GetQueryable = _ => Task.FromResult(_authors.AsQueryable());
        HasMany(x => x.Books); // delegate-less, bidirectional — but non-EF source → EDM-only
    }
}

// ── T20/T21: delegate safety for a bidirectional related type ─────────────────────────────────────

public sealed class BxDelegateCounter
{
    public int Calls;
}

// DELEGATE-BACKED bidirectional nav over the SAME CLR type as BxAuthorProfile (T21: the SAME element
// type is ALSO exposed delegate-less by a sibling set — the declaring-set authority (#292/#293) must
// keep dispatching each SET's OWN configuration, never conflating the two just because the narrowed
// #323 guard now admits the CLR shape).
public sealed class BxSecureAuthorProfile : EntitySetProfile<int, BxAuthor>
{
    public BxSecureAuthorProfile(BidirectionalExpandDbContext db, BxDelegateCounter counter) : base(x => x.Id)
    {
        EntitySetName = "BxSecureAuthors";
        ExpandEnabled = true;
        OrderByEnabled = true;
        GetQueryable = _ => Task.FromResult(db.BxAuthors.AsQueryable());
        HasMany(x => x.Books,
            getAll: (authorId, ct) =>
            {
                counter.Calls++;
                // AsNoTracking(): a delegate that returns real tracked entities off a bidirectional
                // relationship risks the SAME kind of tracked-entity fixup cycle #325 tracks for
                // self-referential sets — orthogonal to #323 (the delegate path is untouched by this
                // fix; ExpandLevelAsync assigns the delegate's return value as-is, with no projection).
                return Task.FromResult<IEnumerable<BxBook>>(
                    db.BxBooks.AsNoTracking().Where(b => b.AuthorId == authorId).ToList());
            });
    }
}

internal static class BidirectionalExpandPushdownHarness
{
    public static async Task<TestFixture> BuildAsync(
        SqliteConnection connection, BxDelegateCounter counter, SqlCaptureSink? sink,
        bool delegatelessFirst = true, Action<EntitySetDefaults>? defaults = null)
    {
        TestFixture fx = await TestHostBuilder.BuildAsync(
            b =>
            {
                if (defaults is not null) b.WithDefaults(defaults);
                if (delegatelessFirst)
                {
                    b.AddEntitySetProfile<BxAuthorProfile>();
                    b.AddEntitySetProfile<BxSecureAuthorProfile>();
                }
                else
                {
                    b.AddEntitySetProfile<BxSecureAuthorProfile>();
                    b.AddEntitySetProfile<BxAuthorProfile>();
                }
                b.AddEntitySetProfile<BxDeepAuthorProfile>();
                b.AddEntitySetProfile<BxOrderProfile>();
                b.AddEntitySetProfile<AcRootProfile>();
                b.AddEntitySetProfile<GsRootProfile>();
                b.AddEntitySetProfile<IfRootProfile>();
                b.AddEntitySetProfile<CyInvoiceProfile>();
                b.AddEntitySetProfile<CyOrgProfile>();
                b.AddEntitySetProfile<InMemoryBxAuthorProfile>();
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                if (sink is not null) services.AddSingleton(sink);
                services.AddDbContext<BidirectionalExpandDbContext>(o =>
                {
                    o.UseSqlite(connection);
                    if (sink is not null)
                    {
                        o.LogTo(
                            message => sink.Add(message),
                            (eventId, _) => eventId == Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted);
                    }
                });
            });

        using IServiceScope scope = fx.App.Services.CreateScope();
        BidirectionalExpandDbContext db = scope.ServiceProvider.GetRequiredService<BidirectionalExpandDbContext>();
        db.Database.EnsureCreated();

        // Author/Book: A1 -> Book1, Book2 (both back-reference A1).
        db.BxAuthors.Add(new BxAuthor { Id = 1, Name = "A1" });
        db.BxBooks.AddRange(
            new BxBook { Id = 10, AuthorId = 1, Title = "Book1" },
            new BxBook { Id = 11, AuthorId = 1, Title = "Book2" });

        db.BxDeepAuthors.Add(new BxDeepAuthor { Id = 1, Name = "DeepA1" });
        db.BxDeepBooks.Add(new BxDeepBook { Id = 10, AuthorId = 1, Title = "DeepBook1" });

        // Order/OrderLine: O1 -> 3 lines (so $top=1 genuinely windows, $count reports the full 3).
        db.BxOrders.Add(new BxOrder { Id = 1, Number = "ORD-1" });
        db.BxOrderLines.AddRange(
            new BxOrderLine { Id = 100, OrderId = 1, Sku = "SKU-A" },
            new BxOrderLine { Id = 101, OrderId = 1, Sku = "SKU-B" },
            new BxOrderLine { Id = 102, OrderId = 1, Sku = "SKU-C" });

        // AcRoot/AcMid/AcLeaf: Root1 -> Mid1 -> Leaf1, with Leaf1's audit columns pointing at Root1
        // (root-typed, T4) and Mid1 itself (mid-ancestor-typed, T6) — neither is the real MidId parent
        // link (which also happens to be Mid1 here; the point is the audit refs are SEPARATE
        // navigations that must never be bound/serialized regardless of what they point at).
        db.AcRoots.Add(new AcRoot { Id = 1, Name = "AcRoot1" });
        db.AcMids.Add(new AcMid { Id = 10, RootId = 1, Name = "AcMid1" });
        db.AcLeaves.Add(new AcLeaf { Id = 100, MidId = 10, Name = "AcLeaf1", AuditRootId = 1, AuditMidId = 10 });

        // GsRoot/GsMid/GsLeaf: grandparent-skip — GsLeaf1 back-references GsRoot1 directly, skipping
        // its real immediate parent GsMid1.
        db.GsRoots.Add(new GsRoot { Id = 1, Name = "GsRoot1" });
        db.GsMids.Add(new GsMid { Id = 10, RootId = 1, Name = "GsMid1" });
        db.GsLeaves.Add(new GsLeaf { Id = 100, MidId = 10, RootId2 = 1, Name = "GsLeaf1" });

        db.IfRoots.Add(new IfRoot { Id = 1, Name = "IfRoot1" });
        db.IfChildren.Add(new IfChild { Id = 10, RootId = 1, Name = "ifchild1" });

        // P1: CyInvoice1 -> Customer1, and -> two Orders that ALSO back-reference Customer1 (the cycle
        // is Customer1.Orders <-> Order.Customer, entirely independent of CyInvoice1).
        db.CyCustomers.Add(new CyCustomer { Id = 1, Name = "Cust1" });
        db.CyInvoices.Add(new CyInvoice { Id = 1, Number = "INV-1", CustomerId = 1 });
        db.CyOrders.AddRange(
            new CyOrder { Id = 100, InvoiceId = 1, CustomerId = 1, Sku = "SKU-X" },
            new CyOrder { Id = 101, InvoiceId = 1, CustomerId = 1, Sku = "SKU-Y" });

        // P2: CyOrg1 -> two employees, Manager1 and Report1 (Report1.ManagerId = Manager1.Id), a
        // self-referential cycle entirely independent of CyOrg1.
        db.CyOrgs.Add(new CyOrg { Id = 1, Name = "Org1" });
        db.CyEmployees.AddRange(
            new CyEmployee { Id = 10, OrgId = 1, Name = "Manager1", ManagerId = null },
            new CyEmployee { Id = 11, OrgId = 1, Name = "Report1", ManagerId = 10 });

        db.SaveChanges();
        return fx;
    }

    public static string LastSelectAgainst(SqlCaptureSink sink, string table) => sink.Snapshot()
        .Where(s => s.Contains("SELECT", StringComparison.Ordinal) && s.Contains($"\"{table}\"", StringComparison.Ordinal))
        .Last();
}

// ── T1/T2: Author/Book, 3- and 5-level expanded back-reference ────────────────────────────────────

public sealed class BidirectionalMultiLevelExpandTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task ThreeLevelChain_BackReferenceExpanded_ReturnsCorrectData_WithJoin()
    {
        // T1: Author -> Books -> Author (3 entities in the chain). The nested Author is a LEAF
        // (no further $expand), so pre-#323 it would have been the bare tracked entity; post-#323 it
        // is a fresh member-init projection, structurally acyclic.
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BxAuthors?$expand=Books($expand=Author)&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "BxAuthors");
        Assert.Contains("\"BxBooks\"", sql); // JOIN to Books
        // The Author leaf is folded into the SAME query (a self-JOIN back to BxAuthors).
        Assert.True(Regex.Matches(sql, "\"BxAuthors\"").Count >= 2,
            $"the nested back-reference expand must self-JOIN BxAuthors; got:\n{sql}");

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("A1", author.GetProperty("Name").GetString());
        JsonElement books = author.GetProperty("Books");
        Assert.Equal(2, books.GetArrayLength());
        foreach (JsonElement book in books.EnumerateArray())
        {
            JsonElement nestedAuthor = book.GetProperty("Author");
            Assert.Equal("A1", nestedAuthor.GetProperty("Name").GetString());
            // Leaf projection wire change (#323): the nested Author's own Books collection was never
            // $expand'd at that depth, so it is not bound at all (and — separately — is a navigation,
            // never a structural property, so it never rides the projection regardless).
            Assert.False(nestedAuthor.TryGetProperty("Books", out _));
        }
    }

    [Fact]
    public async Task FiveLevelChain_BackReferenceExpanded_ReturnsCorrectData_WithJoin()
    {
        // T2: Author -> Books -> Author -> Books -> Author (5 entities / 4 levels of nested $expand).
        // Only reachable at all once Change B admits the bidirectional relationship; only correct
        // (rather than 500) once Change A projects every level, including the deepest leaf, through a
        // fresh POCO. Uses BxDeepAuthors (a CLR type NOT also exposed by a delegate-backed sibling
        // profile — see the BxDeepAuthor/BxDeepBook fixture remarks) so the assertion below tests
        // Change A/B, not Model B's (correct, separate) declaring-set-authority guard.
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BxDeepAuthors?$expand=Books($expand=Author($expand=Books($expand=Author)))&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement author = doc.RootElement.GetProperty("value")[0];

        JsonElement book1 = author.GetProperty("Books")[0];
        JsonElement author2 = book1.GetProperty("Author");
        Assert.Equal("DeepA1", author2.GetProperty("Name").GetString());
        JsonElement book2 = author2.GetProperty("Books")[0];
        JsonElement author3 = book2.GetProperty("Author");
        Assert.Equal("DeepA1", author3.GetProperty("Name").GetString());
        // The deepest Author (level 5, a leaf) carries no further Books binding — a real EDM
        // navigation not $expand'd at that position is stripped by OmitUnexpandedNavigations.
        Assert.False(author3.TryGetProperty("Books", out _));
    }
}

// ── T3: Order/OrderLine, nested $top/$count on a bidirectional collection ─────────────────────────

public sealed class BidirectionalNestedTopCountExpandTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task NestedTopAndCount_OnBidirectionalCollection_WindowsAndCountsCorrectly_WithJoin()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/BxOrders?$expand=Lines($top=1;$count=true;$orderby=id)&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "BxOrders");
        Assert.Contains("\"BxOrderLines\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement order = doc.RootElement.GetProperty("value")[0];

        Assert.Equal(3, order.GetProperty("Lines@odata.count").GetInt32()); // full filtered count
        JsonElement lines = order.GetProperty("Lines");
        Assert.Single(lines.EnumerateArray()); // windowed to $top=1
        Assert.Equal("SKU-A", lines[0].GetProperty("Sku").GetString());
        // The back-reference nav is never bound on the leaf projection.
        Assert.False(lines[0].TryGetProperty("Order", out _));
    }
}

// ── T4/T6: root-typed AND mid-ancestor-typed audit columns ────────────────────────────────────────

public sealed class AuditColumnBackReferenceExpandTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task RootTypedAndMidAncestorTypedAuditColumns_DoNotBlockPushdown_WithJoin()
    {
        // T4 (root-typed AuditRoot) and T6 (mid-ancestor-typed AuditMid) together: neither the
        // grandparent-typed nor the immediate-ancestor-typed audit reference on AcLeaf blocks
        // pushdown, because Change B never evaluates TypeHasNavigationTo once AcLeaf is
        // member-init-projectable (which it is — both audit columns are navigations, not scalars, so
        // AddScalarBindings skips them regardless of what they point at).
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/AcRoots?$expand=Mids($expand=Leaves)&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "AcRoots");
        Assert.Contains("\"AcMids\"", sql);
        Assert.Contains("\"AcLeaves\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement.GetProperty("value")[0];
        JsonElement mid = root.GetProperty("Mids")[0];
        Assert.Equal("AcMid1", mid.GetProperty("Name").GetString());
        JsonElement leaf = mid.GetProperty("Leaves")[0];
        Assert.Equal("AcLeaf1", leaf.GetProperty("Name").GetString());

        // Neither audit navigation is bound on the leaf projection (they were not $expand'd, and are
        // navigations, never structural properties).
        Assert.False(leaf.TryGetProperty("AuditRoot", out _));
        Assert.False(leaf.TryGetProperty("AuditMid", out _));
    }
}

// ── T5: grandparent-skip, back-reference EXPANDED — assert CORRECT DATA, never deferral ───────────

public sealed class GrandparentSkipBackReferenceExpandTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    // #315 (superseded) proposed widening the static guard to catch this shape and defer it
    // gracefully to EDM-only. The #323 spec REFUTES that as the right fix: with Change A/B in place,
    // this exact shape returns CORRECT, FINITE data via a real JOIN — deferring it would throw away a
    // legitimately servable request. Empirically (see the cycle-regression pins below), this specific
    // shape does NOT actually trip a System.Text.Json cycle even with Change A reverted — the root's
    // own independent re-projection already prevents object-identity sharing on this path; Change B
    // alone is what admits it. It is pinned as a regression case regardless, and its correct-data
    // assertions below are unaffected by that nuance.
    [Fact]
    public async Task GrandparentSkip_BackReferenceExpanded_ReturnsCorrectData_WithJoin_NoDeferral()
    {
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync(
            "/odata/GsRoots?$expand=Mids($expand=Leaves)&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "GsRoots");
        Assert.Contains("\"GsMids\"", sql);
        Assert.Contains("\"GsLeaves\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("GsRoot1", root.GetProperty("Name").GetString());
        JsonElement mid = root.GetProperty("Mids")[0];
        Assert.Equal("GsMid1", mid.GetProperty("Name").GetString());
        JsonElement leaf = mid.GetProperty("Leaves")[0];
        Assert.Equal("GsLeaf1", leaf.GetProperty("Name").GetString());
        Assert.False(leaf.TryGetProperty("Root", out _)); // the grandparent-typed back-ref is never bound
    }
}

// ── T7/T8: interface-typed audit member + [NotMapped]/get-only/computed members ────────────────────

public sealed class InterfaceTypedAuditAndComputedMemberExpandTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact]
    public async Task InterfaceTypedAuditMember_DoesNotBlockPushdown_AndIsNeverMaterialized()
    {
        // T7: IfChild.AuditRef is typed as IBxAuditable, which IfRoot implements — TypeHasNavigationTo's
        // interface-aware assignability check would flag it as a back-reference under the OLD
        // unconditional guard. Change B never reaches that check here (IfChild is projectable).
        _sink.Clear();
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/IfRoots?$expand=Children&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "IfRoots");
        Assert.Contains("\"IfChildren\"", sql);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement.GetProperty("value")[0];
        JsonElement child = root.GetProperty("Children")[0];

        // T8, wire change (#323, accepted by design): AuditRef is a public CLR property that is NOT an
        // EDM structural property ([NotMapped]) — it is not BOUND by the leaf projection (the getter/
        // setter never runs against real data), so it serializes as its type's default value (null)
        // rather than whatever a bare, un-projected entity might have had wired up by EF fixup. It is
        // NOT omitted from the wire entirely (unlike a genuine un-expanded EDM navigation, which
        // OmitUnexpandedNavigations strips) — System.Text.Json still serializes every public property
        // of the projected POCO, including this one, at its default value.
        Assert.True(child.TryGetProperty("AuditRef", out JsonElement auditRef));
        Assert.Equal(JsonValueKind.Null, auditRef.ValueKind);

        // T8: a get-only computed property derived from a BOUND scalar (Name) still serializes
        // correctly — the getter runs against the projected POCO, which has Name bound.
        Assert.True(child.TryGetProperty("Shout", out JsonElement shout));
        Assert.Equal("IFCHILD1", shout.GetString());
    }
}

// ── T20/T21: delegate safety for a bidirectional related type ─────────────────────────────────────

public sealed class BidirectionalDelegateSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelegateBackedBidirectionalNav_NeverJoined_DelegateInvoked(bool delegatelessFirst)
    {
        // T20/T21: BxSecureAuthors declares a getAll delegate over the SAME bidirectional CLR type
        // (BxBook) that BxAuthors pushes down delegate-less. The narrowed #323 guard now admits the
        // CLR shape structurally, but the declaring-set authority (#292/#293, untouched by this fix)
        // must still route EACH SET through its OWN configuration — the delegate must never be
        // bypassed just because the shape became pushdown-eligible, in EITHER registration order.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var sink = new SqlCaptureSink();
        var counter = new BxDelegateCounter();
        await using TestFixture fx = await BidirectionalExpandPushdownHarness.BuildAsync(
            connection, counter, sink, delegatelessFirst);
        sink.Clear();

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/BxSecureAuthors?$expand=Books&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(sink, "BxAuthors");
        Assert.DoesNotContain("\"BxBooks\"", sql); // delegate set: never EF-included
        Assert.True(counter.Calls > 0, "the delegate-backed Books nav must expand through its delegate");

        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"Book1\"", body);

        // Control: the delegate-less sibling set over the SAME CLR type still pushes down and JOINs.
        sink.Clear();
        HttpResponseMessage siblingResp = await fx.Client.GetAsync("/odata/BxAuthors?$expand=Books&$orderby=id");
        Assert.Equal(HttpStatusCode.OK, siblingResp.StatusCode);
        string siblingSql = BidirectionalExpandPushdownHarness.LastSelectAgainst(sink, "BxAuthors");
        Assert.Contains("\"BxBooks\"", siblingSql);
    }
}

// ── T22: non-EF source stays EDM-only, unaffected by #323 ─────────────────────────────────────────

public sealed class NonEfBidirectionalExpandTests
{
    [Fact]
    public async Task NonEfSource_BidirectionalNav_StaysEdmOnly_NoCrash()
    {
        await using TestFixture fx = await TestHostBuilder.BuildAsync(
            b => b.AddEntitySetProfile<InMemoryBxAuthorProfile>());

        HttpResponseMessage resp = await fx.Client.GetAsync("/odata/InMemoryBxAuthors?$expand=Books");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The EF-provider gate (ResolveEfCoreAssembly) is untouched by #323: a non-EF source can never
        // reach the pushdown projection at all, so this must succeed exactly as before — whatever the
        // handler's own query already populated (here, the hand-built in-memory graph) serializes.
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"InMemAuthor1\"", body);
        Assert.Contains("\"InMemBook1\"", body);
    }
}

// ── T23-T27: shape-coverage regression pins, PLUS P1/P2: the actual cycle-load-bearing proof ────────
//
// CORRECTED MECHANISM (adversarial review, #323 fold-in — supersedes a prior "EMPIRICAL FINDING" here
// that claimed Change A was NOT load-bearing for cycle prevention on this path; that claim was FALSE
// and has been removed). A reference cycle forms on the member-init projection path if and only if TWO
// mutually-referencing instances are BOTH materialized bare (untransformed) at leaf positions in the
// SAME serialized graph. The root being independently re-projected into a fresh POCO only forecloses a
// cycle whose OTHER end is the root itself — every T23-T27 shape below happens to have exactly that
// shape (the back-reference always points at the root or an ancestor already re-projected), which is
// why none of them 500 with Change A reverted; it does NOT mean Change A is inert. Two proven
// counter-examples, where the cycle's other end is NOT the root:
//   - P1 (sibling cross-reference): CyInvoice expands Customer and Orders as two independent leaves;
//     Customer.Orders <-> Order.Customer cycles WITHOUT ever referencing CyInvoice. Neither leaf's
//     guard check (BuildExpandNavBinding, evaluated against the OWNER = CyInvoice) sees this cycle at
//     all, because it only checks each leaf against its immediate owner, never against sibling leaves.
//   - P2 (self-referential leaf element type): CyOrg expands Employees as one leaf; CyEmployee.Manager
//     <-> CyEmployee.Reports is self-referential and never references CyOrg either.
// Both shapes below are VERIFIED (see LeafCycleRegressionTests) to return `500` (JsonException, cyclic
// reference detected) with ONLY Change A reverted, and `200` with correct data with Change A restored —
// Change A is load-bearing for this disjoint class of cycle. Change B (the narrowed static guard) is
// separately load-bearing for the class FindCyclicLeafExpand/TypeHasNavigationTo actually check
// (back-reference to the immediate owner) — the two are not redundant.
//
// The #305 Include-fallback path (Change C, not Change A) has its OWN, separate cycle exposure: Include
// populates TRACKED entities with no re-projection safety net at all, so a leaf's back-reference to the
// ROOT can get fixed up to the same tracked root instance being serialized — verified empirically by
// TEMPORARILY disabling the Change C guard in the #305 Path A block: BOTH IncludeFallbackCyclicLeafTests
// methods (tracking AND AsNoTracking) then fail with a 500 (InternalServerError) instead of the expected
// 400 — confirming AsNoTracking is NOT a mitigation. See IncludeFallbackSqliteTests.cs's
// IncludeFallbackCyclicLeafTests for those pins. FindCyclicLeafExpand only checks a leaf's element type
// against the ROOT model too (same root-only blind spot as Change B) — a P1/P2-shaped cycle (neither end
// is the root) is NOT caught by Change C either and can still 500 on the Include-fallback path; this is
// NOT a regression (develop has the same gap) and is tracked separately as #326.
//
// T23-T27 below stay as general shape-coverage regression pins (collapsed into a Theory) — useful for
// catching an unrelated regression in these specific shapes, but they prove nothing about Change A's
// necessity; that proof is LeafCycleRegressionTests (P1/P2) below.

public sealed class CycleRegressionPinTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Theory] // T23-T27
    [InlineData("/odata/BxAuthors?$expand=Books($expand=Author)")] // T23: simple bidirectional
    [InlineData("/odata/BxDeepAuthors?$expand=Books($expand=Author($expand=Books($expand=Author)))")] // T24: 5-level chain
    [InlineData("/odata/GsRoots?$expand=Mids($expand=Leaves)")] // T25: grandparent-skip
    [InlineData("/odata/AcRoots?$expand=Mids($expand=Leaves)")] // T26: audit columns
    [InlineData("/odata/IfRoots?$expand=Children")] // T27: interface-typed audit
    public async Task Pin_Shape_Succeeds(string url)
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

// ── P1/P2: LeafCycleRegressionTests — the actual proof that Change A is load-bearing ────────────────
// (adversarial review, #323 fold-in). See the CORRECTED MECHANISM comment above for why T23-T27 don't
// exercise this class of cycle. Both tests here were verified, by TEMPORARILY reverting Change A
// (IsMemberInitProjectable(elem, model) -> engaged.Children is { Count: > 0 } at both call sites in
// BuildShapedNavAccess), to fail with 500 (JsonException, cyclic reference) — and to pass with Change A
// restored.
public sealed class LeafCycleRegressionTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SqlCaptureSink _sink = null!;
    private BxDelegateCounter _counter = null!;
    private TestFixture _fx = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _sink = new SqlCaptureSink();
        _counter = new BxDelegateCounter();
        _fx = await BidirectionalExpandPushdownHarness.BuildAsync(_connection, _counter, _sink);
        _sink.Clear();
    }

    public async Task DisposeAsync()
    {
        await _fx.DisposeAsync();
        _connection.Dispose();
    }

    [Fact] // P1: sibling cross-reference — Customer.Orders <-> Order.Customer, neither touches CyInvoice
    public async Task SiblingCrossReference_Customer_And_Orders_BothServed_WithJoin()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/CyInvoices?$expand=Customer,Orders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string sql = BidirectionalExpandPushdownHarness.LastSelectAgainst(_sink, "CyInvoices");
        Assert.Contains("\"CyCustomers\"", sql); // Customer leaf JOIN'd
        Assert.Contains("\"CyOrders\"", sql); // Orders leaf JOIN'd

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement invoice = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("INV-1", invoice.GetProperty("Number").GetString());

        JsonElement customer = invoice.GetProperty("Customer");
        Assert.Equal("Cust1", customer.GetProperty("Name").GetString());

        JsonElement orders = invoice.GetProperty("Orders");
        Assert.Equal(2, orders.GetArrayLength());
        Assert.Contains(orders.EnumerateArray(), o => o.GetProperty("Sku").GetString() == "SKU-X");
        Assert.Contains(orders.EnumerateArray(), o => o.GetProperty("Sku").GetString() == "SKU-Y");
    }

    [Fact] // P2: self-referential leaf element type — Manager/Reports, never touches CyOrg
    public async Task SelfReferentialLeafElementType_Employees_Served()
    {
        HttpResponseMessage resp = await _fx.Client.GetAsync("/odata/CyOrgs?$expand=Employees");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        string body = await resp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement org = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("Org1", org.GetProperty("Name").GetString());

        JsonElement employees = org.GetProperty("Employees");
        Assert.Equal(2, employees.GetArrayLength());
        Assert.Contains(employees.EnumerateArray(), e => e.GetProperty("Name").GetString() == "Manager1");
        Assert.Contains(employees.EnumerateArray(), e => e.GetProperty("Name").GetString() == "Report1");
    }
}

// ── T28: a self-referential entity set still 500s on a PLAIN GET with no $expand at all ───────────
// (tracked-entity fixup cycling before serialization — #325, NOT fixed by #323). Pinned here so a
// future fix for #325 is forced to update this test rather than silently changing behavior underneath
// it. Skipped rather than asserted as a hard 500 pin: reliably reproducing #325 depends on EF Core
// change-tracker fixup timing this harness's per-request DbContext scoping does not deterministically
// trigger (each HTTP request gets its own scoped DbContext instance, so entities tracked during
// seeding are not visible to it) — #325's own investigation is the right place to nail the precise
// repro, not this fix's test matrix.
public sealed class SelfReferentialPlainGetCycleTests
{
    [Fact(Skip = "Pinned against #325 (separate issue, not fixed by #323) — see class remarks.")]
    public Task SelfReferentialSet_PlainGet_NoExpand_Returns500()
    {
        throw new NotImplementedException("See #325.");
    }
}
