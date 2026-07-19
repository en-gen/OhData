using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OhData.Abstractions;

namespace OhData.Sample.EfCoreSqlite;

// ── Products ──────────────────────────────────────────────────────────────────

/// <summary>
/// Full CRUD over SQLite. <c>GetQueryable</c> hands the framework an un-materialized
/// <c>IQueryable</c>, so <c>$filter</c>/<c>$orderby</c>/<c>$skip</c>/<c>$top</c> are composed
/// onto it and EF Core translates the whole thing to a single SQL statement — the database
/// does the filtering, not the app. Compare with <c>GetAll</c> (<c>IEnumerable</c>), which
/// would return every row and skip query-option processing entirely.
/// </summary>
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(ShopDbContext db) : base(x => x.Id)
    {
        // Query capabilities are off by default and enforced at runtime — a request using a
        // disabled option gets 400 rather than a silently-ignored parameter.
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        // Server-side page-size ceiling: $top above this (or no $top at all, on servers that
        // choose to page) is capped at 50 rows per request.
        MaxTop = 50;

        // Batch-loaded $expand=Category (see docs/navigation-routing.md): the batchGet
        // delegate is called ONCE per page of products with all their keys, issuing a single
        // SQL query — not once per product (the classic N+1). The framework auto-derives the
        // per-entity handler from it, so GET /odata/Products(1)/Category works too.
        HasRequired(
            navigation: x => x.Category,
            batchGet: async (productIds, ct) =>
            {
                var idSet = productIds.ToHashSet();
                Dictionary<int, Category> map = await db.Products
                    .Where(p => idSet.Contains(p.Id))
                    .Join(db.Categories, p => p.CategoryId, c => c.Id, (p, c) => new { p.Id, Category = c })
                    .ToDictionaryAsync(x => x.Id, x => x.Category, ct);
                return map;
            },
            refTargetEntitySet: "Categories");

        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());

        GetById = (id, ct) => db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);

        Post = async (product, ct) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return product;
        };

        Put = async (id, product, ct) =>
        {
            Product? existing = await db.Products.FindAsync([id], ct);
            if (existing is null) return null!; // framework maps a null result to 404
            existing.Name = product.Name;
            existing.Price = product.Price;
            existing.Stock = product.Stock;
            existing.CategoryId = product.CategoryId;
            await db.SaveChangesAsync(ct);
            return existing;
        };

        Patch = async (id, delta, ct) =>
        {
            Product? existing = await db.Products.FindAsync([id], ct);
            if (existing is null) return null;
            delta.Patch(existing); // applies only the properties present in the request body
            await db.SaveChangesAsync(ct);
            return existing;
        };

        Delete = async (id, ct) =>
        {
            Product? existing = await db.Products.FindAsync([id], ct);
            if (existing is null) return false; // IdempotentDelete defaults to true → 204
            db.Products.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        };
    }
}

// ── Categories ────────────────────────────────────────────────────────────────

/// <summary>
/// The navigation target, queryable in its own right, with the reverse batch-loaded
/// <c>$expand=Products</c> navigation.
/// </summary>
public class CategoryProfile : EntitySetProfile<int, Category>
{
    public CategoryProfile(ShopDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // Reverse side of the same batching pattern: $expand=Products loads every expanded
        // category's products with ONE query for the whole page.
        HasMany(x => x.Products, batchGetAll: async (categoryIds, ct) =>
        {
            var idSet = categoryIds.ToHashSet();
            List<Product> products = await db.Products
                .Where(p => idSet.Contains(p.CategoryId))
                .ToListAsync(ct);
            return products.ToLookup(p => p.CategoryId);
        });

        GetQueryable = (_) => Task.FromResult(db.Categories.AsQueryable());

        GetById = (id, ct) => db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct);

        Post = async (category, ct) =>
        {
            db.Categories.Add(category);
            await db.SaveChangesAsync(ct);
            return category;
        };
    }
}
