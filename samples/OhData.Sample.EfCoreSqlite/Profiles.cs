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

        // Server-side page-size ceiling: an explicit $top above 50 is REJECTED with 400
        // (it is not silently capped); a request with no $top at all is server-paged to
        // 50 rows with an @odata.nextLink to the rest.
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

        // Batch-loaded $expand=Tags across the many-to-many. The SelectMany goes THROUGH the
        // suppressed ProductTags join table (watch the SQL: one query with two JOINs for the
        // whole page — no N+1, and no join entity ever materialized). The framework derives
        // the per-entity handler from this too, so GET /odata/Products(1)/Tags also works.
        // (The two-argument SelectMany overload also keeps the parent key alongside each tag.
        // Empirically, the one-lambda correlated form — p.Tags.Select(t => new { p.Id, t }) —
        // failed to translate on SQLite with EF Core 10 here; this two-argument shape
        // translates to plain INNER JOINs on every provider.)
        HasMany(x => x.Tags, batchGetAll: async (productIds, ct) =>
        {
            var idSet = productIds.ToHashSet();
            var pairs = await db.Products
                .Where(p => idSet.Contains(p.Id))
                .SelectMany(p => p.Tags, (p, t) => new { p.Id, Tag = t })
                .ToListAsync(ct);
            return pairs.ToLookup(x => x.Id, x => x.Tag);
        });

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

        // Deliberately partial CRUD: any handler left unassigned registers NO route at all
        // (OhData's headline rule) — so Categories has no PUT/PATCH/DELETE endpoints.
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

// ── Tags ──────────────────────────────────────────────────────────────────────

/// <summary>
/// The other end of the many-to-many. Note the two different "ignores" at play: EF's
/// <c>modelBuilder...Ignore()</c> (used for Category/Products in
/// <see cref="ShopDbContext"/>) removes a property from the PERSISTENCE model, while the
/// profile's <c>Ignore()</c> here (#226) removes one from the WIRE model — same word,
/// different layer. <c>Tag.Products</c> must stay in the EF model (it's half of the skip
/// navigation), so it is ignored wire-side instead.
/// </summary>
public class TagProfile : EntitySetProfile<int, Tag>
{
    public TagProfile(ShopDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // Wire-shape ignore: Tag serializes as { id, label } — no products collection, and
        // no reverse navigation route. The EF skip navigation is untouched.
        Ignore(x => x.Products);

        GetQueryable = (_) => Task.FromResult(db.Tags.AsQueryable());

        GetById = (id, ct) => db.Tags.SingleOrDefaultAsync(t => t.Id == id, ct);
    }
}

// ── ProductSummaries (DTO projection) ─────────────────────────────────────────

/// <summary>
/// An entity set over a DTO that has no table of its own. <c>GetQueryable</c> returns a
/// projection — <c>Products ⋈ Categories</c> flattened into <see cref="ProductSummary"/> —
/// and because it's still an un-materialized <c>IQueryable</c>, OData query options compose
/// on top of it: <c>$filter=categoryName eq 'Tools'</c> becomes SQL
/// <c>WHERE c."Name" = 'Tools'</c> on the JOIN, and <c>$select</c> prunes the SELECT list.
/// The wire model is decoupled from the persistence model with zero mapping code.
/// </summary>
public class ProductSummaryProfile : EntitySetProfile<int, ProductSummary>
{
    public ProductSummaryProfile(ShopDbContext db) : base(x => x.Id)
    {
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;
        MaxTop = 50;

        // An explicit Join rather than p.Category: this sample's EF model deliberately keeps
        // the Product/Category relationship FK-only (the CLR navigations are Ignored — see
        // ShopDbContext), so the join condition is spelled out. In a model with the navigation
        // mapped, projecting through p.Category.Name would translate to the same SQL INNER
        // JOIN — here it would throw, since the navigation isn't in the EF model. Read-only by
        // design: no other handlers are assigned, so no POST/PUT/PATCH/DELETE routes exist for
        // this set (and no single-entity GET either — GetById is unassigned).
        GetQueryable = (_) => Task.FromResult(
            db.Products.Join(
                db.Categories,
                p => p.CategoryId,
                c => c.Id,
                (p, c) => new ProductSummary
                {
                    Id = p.Id,
                    Name = p.Name,
                    Price = p.Price,
                    CategoryName = c.Name,
                }));
    }
}
