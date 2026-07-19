using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace OhData.Sample.EfCoreSqlite;

// ── Entity models ─────────────────────────────────────────────────────────────
//
// A small shop: Product is the flagship queryable set (GetQueryable → SQL pushdown),
// Category is the navigation target reached via $expand in both directions.

/// <summary>
/// The flagship entity set. Exposed through <c>GetQueryable</c> (see
/// <see cref="ProductProfile"/>) so <c>$filter</c>/<c>$orderby</c>/<c>$skip</c>/<c>$top</c>
/// are applied to the <c>IQueryable</c> and translated by EF Core into SQL
/// <c>WHERE</c>/<c>ORDER BY</c>/<c>LIMIT</c>/<c>OFFSET</c> — watch the console while
/// querying to see the exact SQL each request produces.
/// </summary>
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }

    /// <summary>Foreign key into <see cref="Category"/> (a real FK constraint in SQLite).</summary>
    public int CategoryId { get; set; }

    /// <summary>Single-valued navigation, batch-loaded for <c>$expand=Category</c> — see
    /// <see cref="ProductProfile"/>.</summary>
    public Category Category { get; set; } = null!;

    /// <summary>
    /// Many-to-many skip navigation (EF Core <c>HasMany().WithMany()</c>): the
    /// <c>ProductTags</c> join table exists only inside the database — it has no CLR type,
    /// so it can never leak onto the OData wire. Batch-loaded for <c>$expand=Tags</c> — see
    /// <see cref="ProductProfile"/>.
    /// </summary>
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
}

/// <summary>A product category with a batch-loaded reverse navigation.</summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Reverse collection navigation, batch-loaded for <c>$expand=Products</c> — see
    /// <see cref="CategoryProfile"/>.</summary>
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

/// <summary>
/// A product tag, related to <see cref="Product"/> many-to-many through an implicit
/// shared-type join entity (the <c>ProductTags</c> table) — no CLR join type anywhere.
/// </summary>
public class Tag
{
    public int Id { get; set; }
    public string Label { get; set; } = "";

    /// <summary>
    /// Reverse skip navigation. EF needs it for the <c>HasMany().WithMany()</c> pairing, but
    /// <see cref="TagProfile"/> excludes it from the wire with the profile-level
    /// <c>Ignore()</c>, so a Tag serializes as just <c>{ id, label }</c>.
    /// </summary>
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

/// <summary>
/// A plain DTO — not an EF entity, no DbSet, no table. <see cref="ProductSummaryProfile"/>
/// projects it from <see cref="Product"/> ⋈ <see cref="Category"/> inside the
/// <c>IQueryable</c>, so the OData surface (the "wire model") is fully decoupled from the
/// persistence model while keeping SQL pushdown: <c>$filter</c>/<c>$orderby</c> on DTO
/// properties translate through the projection into SQL on the underlying tables.
/// </summary>
public class ProductSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    /// <summary>Flattened from the joined <see cref="Category"/> row — the client never
    /// learns that categories live in their own table.</summary>
    public string CategoryName { get; set; } = "";
}

// ── EF Core SQLite context ────────────────────────────────────────────────────

/// <summary>
/// Standard scoped DbContext over SQLite. Profiles are registered scoped too (that's
/// OhData's default), so injecting this into a profile constructor is safe.
/// </summary>
public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The CLR navigation properties exist for the OData profiles to point their
        // HasRequired/HasMany lambdas at (x => x.Category, x => x.Products); the profiles load
        // the actual data with explicit LINQ. EF therefore ignores the navigations and the
        // relationship is configured FK-only — this keeps a real FOREIGN KEY constraint in
        // SQLite while preventing EF navigation fixup from stitching entities into the cyclic
        // Product → Category → Products → Product → ... object graph.
        modelBuilder.Entity<Product>().Ignore(p => p.Category);
        modelBuilder.Entity<Category>().Ignore(c => c.Products);

        modelBuilder.Entity<Product>()
            .HasOne<Category>()
            .WithMany()
            .HasForeignKey(p => p.CategoryId);

        // Many-to-many with a SUPPRESSED join table: Product.Tags ⟷ Tag.Products are EF Core
        // "skip navigations", and the join entity is an implicit shared-type entity — there is
        // no CLR class for it, only the ProductTags table UsingEntity() names. Unlike
        // Category/Products above, these navigations must stay IN the EF model (skip
        // navigations are how EF knows to route the relationship through the join table).
        // That's safe here because the profiles only ever read tags through a projection
        // (SelectMany), which never materializes join-entity rows — so EF's relationship
        // fixup never stitches up the cyclic Product ⟷ Tag object graph.
        modelBuilder.Entity<Product>()
            .HasMany(p => p.Tags)
            .WithMany(t => t.Products)
            .UsingEntity("ProductTags");

        // SQLite has no native decimal column type; storing Price as REAL (double) keeps
        // $filter/$orderby on price translating to plain SQL comparisons instead of hitting
        // the SQLite provider's decimal-in-query limitations. Note that double is an
        // approximate type — fine for a demo, but a real money app should store cents as an
        // integer or use the provider's default decimal-as-TEXT mapping instead.
        modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
    }
}
