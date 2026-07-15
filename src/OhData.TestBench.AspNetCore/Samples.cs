using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.EntityFrameworkCore;
using OhData.Abstractions;

namespace OhData.TestBench.AspNetCore;

// ── Entity models ─────────────────────────────────────────────────────────────

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Category { get; set; } = "";
}

public class Order
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<OrderNote> Notes { get; set; } = new List<OrderNote>();
}

public class OrderLine
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class OrderNote
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string Text { get; set; } = "";
}

public class Category
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

// ── EF Core InMemory context ──────────────────────────────────────────────────

/// <summary>
/// Registered as a singleton for demo purposes so profiles (also singletons) can share it.
/// In production, use IDbContextFactory&lt;T&gt; to avoid scoped-in-singleton issues.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId);

        modelBuilder.Entity<Order>()
            .HasMany(o => o.Notes)
            .WithOne()
            .HasForeignKey(n => n.OrderId);
    }
}

public static class DbSeeder
{
    public static void Seed(AppDbContext db)
    {
        if (db.Products.Any()) return;

        var order1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var order2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

        db.Products.AddRange(
            new Product { Id = 1, Name = "Widget", Price = 9.99m, Category = "Hardware" },
            new Product { Id = 2, Name = "Gadget", Price = 24.99m, Category = "Electronics" },
            new Product { Id = 3, Name = "Sprocket", Price = 4.49m, Category = "Hardware" },
            new Product { Id = 4, Name = "Doohickey", Price = 14.99m, Category = "Misc" },
            new Product { Id = 5, Name = "Thingamajig", Price = 39.99m, Category = "Electronics" }
        );

        db.Orders.AddRange(
            new Order { Id = order1, CustomerName = "Alice", Total = 34.98m },
            new Order { Id = order2, CustomerName = "Bob", Total = 9.99m }
        );

        db.OrderLines.AddRange(
            new OrderLine { Id = 1, OrderId = order1, ProductName = "Widget", Quantity = 2, UnitPrice = 9.99m },
            new OrderLine { Id = 2, OrderId = order1, ProductName = "Sprocket", Quantity = 3, UnitPrice = 4.99m },
            new OrderLine { Id = 3, OrderId = order2, ProductName = "Widget", Quantity = 1, UnitPrice = 9.99m }
        );

        db.SaveChanges();
    }
}

// ── Profiles ──────────────────────────────────────────────────────────────────

/// <summary>
/// Full-featured product profile backed by EF Core InMemory.
/// Demonstrates: GetQueryable with real LINQ pushdown, $filter/$orderby/$top/$skip/$count/$select.
/// </summary>
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        SelectEnabled = true;

        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());

        GetById = (id, _) => Task.FromResult(db.Products.Find(id));

        Post = (product, _) =>
        {
            db.Products.Add(product);
            db.SaveChanges();
            return Task.FromResult<Product?>(product);
        };

        Put = (id, product, _) =>
        {
            var existing = db.Products.Find(id);
            if (existing is null) return Task.FromResult<Product>(null!);
            existing.Name = product.Name;
            existing.Price = product.Price;
            existing.Category = product.Category;
            db.SaveChanges();
            return Task.FromResult(existing);
        };

        Patch = (id, delta, _) =>
        {
            var existing = db.Products.Find(id);
            if (existing is null) return Task.FromResult<Product?>(null);
            delta.Patch(existing);
            db.SaveChanges();
            return Task.FromResult<Product?>(existing);
        };

        Delete = (id, _) =>
        {
            var existing = db.Products.Find(id);
            if (existing is null) return Task.FromResult(false);
            db.Products.Remove(existing);
            db.SaveChanges();
            return Task.FromResult(true);
        };
    }
}

/// <summary>
/// Order profile with navigation routing to order lines and order notes.
/// Demonstrates: HasMany with a batch route handler → GET /v2/Orders(id)/Lines, and a
/// single-query <c>$expand=Lines</c> instead of one SQL query per order in the page
/// (REVIEW.md M-1 — see NavigationRouteDefinition.BatchHandler). Also demonstrates POST to a
/// collection navigation property → POST /v2/Orders(id)/Notes, creating a new related entity
/// (OData §11.4.2.1 — see NavigationRouteDefinition.PostChild), alongside the batch-loaded
/// Lines navigation on the same profile. And demonstrates deep insert (OData §11.4.2.2):
/// <c>AllowDeepInsert = true</c> lets <c>POST /v2/Orders</c> create an order together with its
/// Lines in one request — <c>Post</c> below adds the whole graph (order + lines) to the
/// DbContext and calls SaveChanges once, so EF Core's relationship fixup assigns each line's
/// OrderId automatically from the tracked navigation.
/// </summary>
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        AllowDeepInsert = true;

        // Registers: GET /Orders(id)/Lines (single-key route auto-derived from the batch
        // delegate below) AND makes GET /Orders?$expand=Lines load every order's lines with
        // ONE SQL query for the whole page instead of one query per order.
        HasMany(x => x.Lines, batchGetAll: (orderIds, ct) =>
        {
            ILookup<Guid, OrderLine> lookup = db.OrderLines
                .Where(l => orderIds.Contains(l.OrderId))
                .AsEnumerable()
                .ToLookup(l => l.OrderId);
            return Task.FromResult(lookup);
        });

        // Registers: GET /Orders(id)/Notes AND POST /Orders(id)/Notes (create a new note on an
        // existing order, §11.4.2.1). refTargetEntitySet lets the framework compute a
        // Location/@odata.id for the created note from its key, the same convention $ref uses.
        HasMany(
            navigation: x => x.Notes,
            getAll: (orderId, ct) =>
                Task.FromResult<IEnumerable<OrderNote>>(db.OrderNotes.Where(n => n.OrderId == orderId).ToList()),
            post: (orderId, note, ct) =>
            {
                if (db.Orders.Find(orderId) is null) return Task.FromResult<OrderNote?>(null);
                note.OrderId = orderId;
                db.OrderNotes.Add(note);
                db.SaveChanges();
                return Task.FromResult<OrderNote?>(note);
            },
            refTargetEntitySet: "OrderNotes");

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());

        GetById = (id, _) => Task.FromResult(
            db.Orders.Include(o => o.Lines).FirstOrDefault(o => o.Id == id));

        Post = (order, _) =>
        {
            order.Id = Guid.NewGuid();
            db.Orders.Add(order);
            db.SaveChanges();
            return Task.FromResult<Order?>(order);
        };

        Delete = (id, _) =>
        {
            var existing = db.Orders.Find(id);
            if (existing is null) return Task.FromResult(false);
            db.Orders.Remove(existing);
            db.SaveChanges();
            return Task.FromResult(true);
        };
    }
}

/// <summary>
/// Simple category catalog -- demonstrates the GetAll (IEnumerable) path with a string key.
/// No query options are applied; the handler returns the full list.
/// </summary>
public class CategoryProfile : EntitySetProfile<string, Category>
{
    private static readonly Category[] _categories =
    {
        new() { Code = "Hardware",    DisplayName = "Hardware & Tools" },
        new() { Code = "Electronics", DisplayName = "Electronics & Gadgets" },
        new() { Code = "Misc",        DisplayName = "Miscellaneous" },
    };

    public CategoryProfile() : base(x => x.Code)
    {
        EntitySetName = "Categories";

        GetAll = (_) => Task.FromResult<IEnumerable<Category>>(_categories);
        GetById = (code, _) => Task.FromResult(
            _categories.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// v2 variant of <see cref="ProductProfile"/> -- same configuration, separate DI registration
/// so it can coexist in the v2 OhData registration alongside <see cref="ProductProfile"/> in v1.
/// </summary>
public class ProductProfileV2 : ProductProfile
{
    public ProductProfileV2(AppDbContext db) : base(db) { }
}

/// <summary>
/// v2 variant of <see cref="CategoryProfile"/> -- same configuration, separate DI registration
/// so it can coexist in the v2 OhData registration alongside <see cref="CategoryProfile"/> in v1.
/// </summary>
public class CategoryProfileV2 : CategoryProfile { }
