# EF Core + SQLite tutorial

The [getting-started walkthrough](getting-started.md) put an in-memory list behind OhData. This
tutorial puts a **real relational database** behind it — SQLite via EF Core — and shows the
payoff: OData query options are not applied in memory, they are composed onto an `IQueryable` and
**translated to SQL** by EF Core. `$filter`/`$orderby`/`$skip`/`$top` become
`WHERE`/`ORDER BY`/`LIMIT`/`OFFSET` executed by the database.

The complete, runnable version of everything below lives in
[`samples/OhData.Sample.EfCoreSqlite/`](https://github.com/en-gen/OhData/tree/develop/samples/OhData.Sample.EfCoreSqlite) —
`git clone`, `dotnet run`, done.

## Prerequisites

- The **.NET 10 SDK**. Nothing else — SQLite is bundled with the EF Core SQLite provider, and no
  external database server is required.

## 1. Create the project and add packages

```bash
dotnet new web -o ShopApi
cd ShopApi
dotnet add package EnGen.OhData.AspNetCore
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
```

## 2. Model and DbContext

An ordinary EF Core model. `Product` is the flagship queryable entity set; `Category` is a
navigation target reachable via `$expand`.

```csharp
// Models.cs
using Microsoft.EntityFrameworkCore;

namespace ShopApi;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category).WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId);

        // SQLite has no native decimal type; storing Price as REAL keeps $filter/$orderby on
        // price translating to plain SQL comparisons. (An approximate type — a real money app
        // should store cents as an integer.)
        modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
    }
}
```

> The runnable sample keeps the `Product`/`Category` relationship FK-only and loads navigation
> data with explicit LINQ, which avoids EF navigation-fixup cycles. The mapped-navigation form
> above is the more common starting point; see the
> [sample's `Models.cs`](https://github.com/en-gen/OhData/blob/develop/samples/OhData.Sample.EfCoreSqlite/Models.cs)
> and its comments for the trade-off.

## 3. The profile: SQL pushdown through `GetQueryable`

The key move: `GetQueryable` hands the framework an **un-materialized** `IQueryable`, so the
OData query options compose onto it and EF Core translates the whole thing to one SQL statement.
Contrast with `GetAll` (`IEnumerable`), which would fetch every row and skip query-option
processing entirely.

```csharp
// ProductProfile.cs
using Microsoft.EntityFrameworkCore;
using OhData;

namespace ShopApi;

public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(ShopDbContext db) : base(x => x.Id)
    {
        // Enforced at runtime: a disabled option returns 400, not a silently-ignored parameter.
        FilterEnabled = OrderByEnabled = SelectEnabled = ExpandEnabled = CountEnabled = true;

        // Server-side page-size ceiling. An explicit $top above 50 is REJECTED with 400 (not
        // silently capped); a request with no $top is server-paged to 50 with an @odata.nextLink.
        MaxTop = 50;

        // Delegate-less navigation: declaring the relationship with NO delegate opts $expand=Category
        // into SQL-JOIN pushdown (#206). On the EF Core-backed GetQueryable path the framework folds
        // it into an Include, so one JOIN'd query loads the page AND its categories — no delegate to
        // write, no N+1. (Supply a get/batchGet delegate only when expansion needs custom logic —
        // filtering, ordering, authorization; that opts the nav back OUT of pushdown and also gives
        // you a standalone GET /odata/Products(1)/Category route.)
        HasRequired(x => x.Category);

        GetQueryable = _ => Task.FromResult(db.Products.AsQueryable());
        GetById      = (id, ct) => db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);

        Post = async (product, ct) =>
        {
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            return product;
        };

        Patch = async (id, delta, ct) =>
        {
            Product? existing = await db.Products.FindAsync([id], ct);
            if (existing is null) return null;
            delta.Patch(existing);          // applies only the properties in the request body
            await db.SaveChangesAsync(ct);
            return existing;
        };

        Delete = async (id, ct) =>
        {
            Product? existing = await db.Products.FindAsync([id], ct);
            if (existing is null) return false;   // IdempotentDelete defaults to true → 204
            db.Products.Remove(existing);
            await db.SaveChangesAsync(ct);
            return true;
        };
    }
}
```

A `CategoryProfile` follows the same shape with `GetQueryable`/`GetById`. Leaving its
`Put`/`Patch`/`Delete` unassigned is a feature: those routes simply won't exist.

## 4. Wire it up, with SQL logging on

`AddOhData`/`MapOhData` live in the framework's `Microsoft.Extensions.DependencyInjection` and
`Microsoft.AspNetCore.Builder` namespaces, so they need no dedicated `using`.

```csharp
// Program.cs
using Microsoft.EntityFrameworkCore;
using ShopApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ShopDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Shop") ?? "Data Source=app.db"));

// Profiles are scoped, so injecting the scoped ShopDbContext into their constructors is safe —
// one DbContext per request, the normal ASP.NET Core lifetime.
builder.Services.AddOhData(o => o
    .WithPrefix("/odata")
    .AddEntitySetProfile<ProductProfile>()
    .AddEntitySetProfile<CategoryProfile>());

var app = builder.Build();

// Create/upgrade the database on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    db.Database.Migrate();   // or db.Database.EnsureCreated() before you add migrations
}

app.MapOhData();
app.Run();
```

Turn on SQL logging so you can *see* the pushdown — set the EF Core command logger to
`Information` in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  }
}
```

## 5. Add the initial migration

```bash
dotnet tool install --global dotnet-ef      # once per machine
dotnet ef migrations add InitialCreate
```

`Database.Migrate()` in `Program.cs` applies it (and creates `app.db`) on first run. Delete
`app.db*` any time to reset.

## 6. Watch OData become SQL

Run it and fire a query with all three of filter, order, and page. Property names in the query
options are the canonical PascalCase names from `$metadata` (matching is case-insensitive, but the
responses come back PascalCase, so prefer the canonical form):

```bash
dotnet run
curl "http://localhost:5220/odata/Products?\$filter=Price%20gt%2010&\$orderby=Name&\$top=5"
```

The console prints the single SQL statement the whole OData query produced — the database does
the work:

```sql
SELECT "p"."Id", "p"."CategoryId", "p"."Name", "p"."Price", "p"."Stock"
FROM "Products" AS "p"
WHERE "p"."Price" > @TypedProperty
ORDER BY "p"."Name"
LIMIT @TypedProperty1
```

- `$skip` adds `OFFSET`.
- `$count` (`/odata/Products/$count?$filter=...`) becomes `SELECT COUNT(*)` with the same `WHERE`.
- `$select` pushes a **column-pruned projection** — `?$select=Name,Price` narrows the `SELECT`
  list to just those columns (plus the key), so the database reads and returns less. The wire
  output is byte-identical to selecting client-side; only the SQL changes.
- `$expand=Category` folds the delegate-less navigation into a **single JOIN'd query** (EF Core
  `Include`) — the page and its categories load together, no query-per-row.

Expanding is one `Include`-backed statement:

```bash
curl "http://localhost:5220/odata/Products?\$orderby=Name&\$top=3&\$expand=Category"
```

```sql
SELECT "p"."Id", "p"."CategoryId", "p"."Name", "p"."Price", "p"."Stock",
       "c"."Id", "c"."Name"
FROM "Products" AS "p"
INNER JOIN "Categories" AS "c" ON "p"."CategoryId" = "c"."Id"
ORDER BY "p"."Name"
LIMIT @TypedProperty
```

Nested and multi-level expands push the same way: `$expand=Category($expand=…)` becomes an
`Include`→`ThenInclude` chain, and a self-referential `$levels=N` folds N levels into one query
(bounded by `MaxExpansionDepth`). That is the entire point of the `GetQueryable` path: the
filtering, ordering, paging, projection, **and** expansion happen **inside the database**, so
adding `$expand` never reintroduces N+1. (A navigation declared *with* a delegate keeps using
that handler instead — see the [`$expand` pushdown reference](../docs/query-options.md#expand-pushdown-delegate-less-navigations-join-automatically-206).)

## Going further with the sample

The runnable
[`samples/OhData.Sample.EfCoreSqlite/`](https://github.com/en-gen/OhData/tree/develop/samples/OhData.Sample.EfCoreSqlite)
also demonstrates, with live SQL for each:

- An entity set over a **plain DTO** (`ProductSummaries`) — a `Join` projection inside the
  `IQueryable` that decouples the wire model from the persistence model *without losing pushdown*
  (filtering on a DTO-only property becomes a `WHERE` on the joined table).
- A **many-to-many** (`Products ⟷ Tags`) whose join table has no CLR type and never appears on
  the wire; `$expand=Tags` batch-loads through it with a single JOIN per page.
- The two different "ignores" — EF's model-level `Ignore()` (persistence model) vs OhData's
  profile-level [`Ignore()`](../docs/ignoring-properties.md) (wire model).

## Related guides

- [Query options](../docs/query-options.md) — the full query surface and the capability flags that gate it.
- [Navigation & `$ref` routing](../docs/navigation-routing.md) — navigation routes, `$ref`, and the batch `$expand` pattern used here.
- [Deployment](../docs/deployment.md) — packaging an OhData service for production.
