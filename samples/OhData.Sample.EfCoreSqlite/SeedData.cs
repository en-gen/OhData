using System.Collections.Generic;
using System.Linq;

namespace OhData.Sample.EfCoreSqlite;

/// <summary>
/// Seeds the database on first run (after <c>Database.Migrate()</c> has created the schema).
/// Idempotent: does nothing when products already exist, so the SQLite file survives restarts
/// and your own POST/PATCH/DELETE experiments persist between runs.
/// </summary>
public static class SeedData
{
    public static void EnsureSeeded(ShopDbContext db)
    {
        if (db.Products.Any()) return;

        var tools = new Category { Name = "Tools" };
        var fasteners = new Category { Name = "Fasteners" };
        var electrical = new Category { Name = "Electrical" };
        var paint = new Category { Name = "Paint" };
        db.Categories.AddRange(tools, fasteners, electrical, paint);
        db.SaveChanges(); // assigns category ids

        db.Products.AddRange(new List<Product>
        {
            new() { Name = "Claw Hammer", Price = 14.99m, Stock = 42, CategoryId = tools.Id },
            new() { Name = "Ball-Peen Hammer", Price = 17.49m, Stock = 18, CategoryId = tools.Id },
            new() { Name = "Adjustable Wrench", Price = 12.25m, Stock = 31, CategoryId = tools.Id },
            new() { Name = "Locking Pliers", Price = 11.80m, Stock = 27, CategoryId = tools.Id },
            new() { Name = "Screwdriver Set", Price = 24.90m, Stock = 55, CategoryId = tools.Id },
            new() { Name = "Tape Measure 5m", Price = 8.99m, Stock = 73, CategoryId = tools.Id },
            new() { Name = "Utility Knife", Price = 6.49m, Stock = 88, CategoryId = tools.Id },
            new() { Name = "Cordless Drill", Price = 89.00m, Stock = 12, CategoryId = tools.Id },
            new() { Name = "Spirit Level 60cm", Price = 15.75m, Stock = 22, CategoryId = tools.Id },
            new() { Name = "Wood Screws 4x40 (200)", Price = 7.20m, Stock = 140, CategoryId = fasteners.Id },
            new() { Name = "Drywall Screws 3.5x35 (500)", Price = 11.50m, Stock = 96, CategoryId = fasteners.Id },
            new() { Name = "Hex Bolts M8 (50)", Price = 9.35m, Stock = 64, CategoryId = fasteners.Id },
            new() { Name = "Washers M8 (100)", Price = 3.10m, Stock = 210, CategoryId = fasteners.Id },
            new() { Name = "Wall Anchors (100)", Price = 5.60m, Stock = 175, CategoryId = fasteners.Id },
            new() { Name = "Finishing Nails (300)", Price = 4.25m, Stock = 133, CategoryId = fasteners.Id },
            new() { Name = "LED Bulb 9W (4-pack)", Price = 13.99m, Stock = 61, CategoryId = electrical.Id },
            new() { Name = "Extension Cord 10m", Price = 19.99m, Stock = 34, CategoryId = electrical.Id },
            new() { Name = "Wire Stripper", Price = 10.45m, Stock = 29, CategoryId = electrical.Id },
            new() { Name = "Electrical Tape (3-pack)", Price = 4.99m, Stock = 118, CategoryId = electrical.Id },
            new() { Name = "Smart Plug", Price = 22.50m, Stock = 40, CategoryId = electrical.Id },
            new() { Name = "Interior Paint White 5L", Price = 32.00m, Stock = 26, CategoryId = paint.Id },
            new() { Name = "Primer 2.5L", Price = 18.75m, Stock = 33, CategoryId = paint.Id },
            new() { Name = "Paint Roller Kit", Price = 9.85m, Stock = 57, CategoryId = paint.Id },
            new() { Name = "Masking Tape 48mm", Price = 3.45m, Stock = 149, CategoryId = paint.Id },
            new() { Name = "Brush Set (5)", Price = 12.99m, Stock = 44, CategoryId = paint.Id },
        });
        db.SaveChanges();
    }
}
