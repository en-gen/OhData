using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
}

public class OrderLine
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>()
            .HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId);
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
            return Task.FromResult(product);
        };

        PutById = (id, product, _) =>
        {
            var existing = db.Products.Find(id);
            if (existing is null) return Task.FromResult<Product>(null!);
            existing.Name = product.Name;
            existing.Price = product.Price;
            existing.Category = product.Category;
            db.SaveChanges();
            return Task.FromResult(existing);
        };

        Patch = (id, product, _) =>
        {
            var existing = db.Products.Find(id);
            if (existing is null) return Task.FromResult<Product?>(null);
            if (!string.IsNullOrEmpty(product.Name)) existing.Name = product.Name;
            if (product.Price > 0) existing.Price = product.Price;
            if (!string.IsNullOrEmpty(product.Category)) existing.Category = product.Category;
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
/// Order profile with navigation routing to order lines.
/// Demonstrates: HasMany with route handler → GET /v2/Orders(id)/Lines
/// </summary>
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        CountEnabled = true;
        ExpandEnabled = true;

        // Registers: GET /Orders(id)/Lines
        HasMany(x => x.Lines,
            getAll: (orderId, _) =>
                Task.FromResult<IEnumerable<OrderLine>>(
                    db.OrderLines.Where(l => l.OrderId == orderId).ToList()));

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());

        GetById = (id, _) => Task.FromResult(
            db.Orders.Include(o => o.Lines).FirstOrDefault(o => o.Id == id));

        Post = (order, _) =>
        {
            order.Id = Guid.NewGuid();
            db.Orders.Add(order);
            db.SaveChanges();
            return Task.FromResult(order);
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
/// Simple category catalog — demonstrates the GetAll (IEnumerable) path with a string key.
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
