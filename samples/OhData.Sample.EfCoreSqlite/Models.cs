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

// ── EF Core SQLite context ────────────────────────────────────────────────────

/// <summary>
/// Standard scoped DbContext over SQLite. Profiles are registered scoped too (that's
/// OhData's default), so injecting this into a profile constructor is safe.
/// </summary>
public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

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

        // SQLite has no native decimal column type; storing Price as REAL (double) keeps
        // $filter/$orderby on price translating to plain SQL comparisons instead of hitting
        // the SQLite provider's decimal-in-query limitations. Note that double is an
        // approximate type — fine for a demo, but a real money app should store cents as an
        // integer or use the provider's default decimal-as-TEXT mapping instead.
        modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
    }
}
