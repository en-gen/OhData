# OhData + EF Core SQLite sample

A minimal shop API (Products / Categories) that puts a **real relational database** behind
OhData's `GetQueryable` path. OData query options are not applied in memory — they are composed
onto the `IQueryable` and EF Core translates them to SQL, so `$filter`/`$orderby`/`$skip`/`$top`
become `WHERE`/`ORDER BY`/`LIMIT`/`OFFSET` executed by SQLite. SQL logging is enabled, so every
request prints the exact SQL it produced.

What it demonstrates:

- `GetQueryable` with SQL pushdown, `GetById`, `Post`, `Put`, `Patch`, `Delete` — all async EF Core
- `FilterEnabled` / `OrderByEnabled` / `SelectEnabled` / `ExpandEnabled` / `CountEnabled` and `MaxTop`
- Batch-loaded `$expand` in both directions (`Products?$expand=Category`,
  `Categories?$expand=Products`) — one SQL query per page, not one per row (no N+1)
- An entity set over a **plain DTO** (`ProductSummaries`) — a `Join` projection inside the
  `IQueryable`, decoupling the wire model from the persistence model without losing pushdown
- A **many-to-many** (`Products ⟷ Tags`) whose join table has no CLR type and never appears
  on the wire — `$expand=Tags` batch-loads through it with a single JOIN per page
- EF Core **migrations** (committed, in [`Migrations/`](Migrations/)) applied with
  `Database.Migrate()` on startup, plus idempotent seeding

This sample references the framework source directly (`ProjectReference` to
`../../src/OhData.AspNetCore`) so it always runs against the current code. In your own app,
install the NuGet package instead:

```
dotnet add package EnGen.OhData.AspNetCore
```

## Run it

Prerequisites: the .NET 10 SDK. Nothing else — SQLite is bundled, and the committed migrations
create `app.db` on first run (no `dotnet ef` required).

```
dotnet run
```

The API listens on `http://localhost:5220`. Delete `app.db*` any time to reset the data.

## Try it

```bash
# Service document and metadata
curl "http://localhost:5220/odata/"
curl "http://localhost:5220/odata/\$metadata"

# Collection, and the query options (quote the URL and percent-encode spaces as %20 —
# modern curl rejects URLs containing literal spaces)
curl "http://localhost:5220/odata/Products"
curl "http://localhost:5220/odata/Products?\$filter=price%20gt%2010&\$orderby=name&\$top=5"
curl "http://localhost:5220/odata/Products?\$orderby=price%20desc&\$skip=5&\$top=3"
curl "http://localhost:5220/odata/Products?\$select=name,price&\$orderby=name&\$top=2"
curl "http://localhost:5220/odata/Products/\$count?\$filter=price%20gt%2010"

# Batch-loaded $expand — one JOIN per page, not one query per row
curl "http://localhost:5220/odata/Products?\$orderby=name&\$top=3&\$expand=Category"
curl "http://localhost:5220/odata/Categories?\$expand=Products"

# Single entity / navigation
curl "http://localhost:5220/odata/Products(1)"
curl "http://localhost:5220/odata/Products(1)/Category"

# Create and update
curl -X POST "http://localhost:5220/odata/Products" \
     -H "Content-Type: application/json" \
     -d '{"name":"Stud Finder","price":29.99,"stock":10,"categoryId":1}'

curl -X PATCH "http://localhost:5220/odata/Products(26)" \
     -H "Content-Type: application/json" \
     -d '{"price":27.49}'
# (26 is the id from the POST response above — adjust if yours differs)
```

(PowerShell: single-quote the URL and drop the `\` before `$` — the backslash is bash escaping,
and inside single quotes PowerShell doesn't expand `$filter` anyway. For example:
`Invoke-WebRequest 'http://localhost:5220/odata/Products?$filter=price%20gt%2010'`.)

## The SQL pushdown, live

The console logs every SQL statement (the `Microsoft.EntityFrameworkCore.Database.Command`
category is set to `Information` in [`appsettings.json`](appsettings.json)). Run:

```
curl "http://localhost:5220/odata/Products?\$filter=price%20gt%2010&\$orderby=name&\$top=5"
```

and the console shows the whole OData query as one SQL statement — filtering, ordering, and
paging all happen inside SQLite (actual output):

```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (0ms) [Parameters=[@TypedProperty='?' (DbType = Double), @TypedProperty1='?' (DbType = Int32)], CommandType='Text', CommandTimeout='30']
      SELECT "p"."Id", "p"."CategoryId", "p"."Name", "p"."Price", "p"."Stock"
      FROM "Products" AS "p"
      WHERE "p"."Price" > @TypedProperty
      ORDER BY "p"."Name"
      LIMIT @TypedProperty1
```

`$skip` adds an `OFFSET`:

```
      ORDER BY "p"."Price" DESC
      LIMIT @TypedProperty1 OFFSET @TypedProperty
```

`$count` becomes a `COUNT(*)` with the same `WHERE`:

```
      SELECT COUNT(*)
      FROM "Products" AS "p"
      WHERE "p"."Price" > @TypedProperty
```

and `$expand=Category` on a page of products issues a single batched `JOIN` for the whole page
(the `HasRequired` batch overload in [`Profiles.cs`](Profiles.cs)):

```
      SELECT "p"."Id", "c"."Id", "c"."Name"
      FROM "Products" AS "p"
      INNER JOIN "Categories" AS "c" ON "p"."CategoryId" = "c"."Id"
      WHERE "p"."Id" IN (@idSet1, @idSet2, @idSet3)
```

## OData DTOs: projecting away your persistence model

`ProductSummaries` is an entity set with **no table behind it**. `ProductSummary` is a plain
DTO — not an EF entity, no `DbSet`, no migration — and `ProductSummaryProfile` builds it by
projecting a join inside the `IQueryable`:

```csharp
GetQueryable = (_) => Task.FromResult(
    db.Products.Join(db.Categories,
        p => p.CategoryId, c => c.Id,
        (p, c) => new ProductSummary { Id = p.Id, Name = p.Name, Price = p.Price, CategoryName = c.Name }));
```

Because the projection is still un-materialized, OData query options compose **through** it.
Filtering on `categoryName` — a property that only exists on the wire model — becomes a
`WHERE` on the joined `Categories` table:

```bash
curl "http://localhost:5220/odata/ProductSummaries?\$filter=categoryName%20eq%20'Tools'&\$orderby=name&\$top=3"
```

```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (0ms) [Parameters=[@TypedProperty='?' (Size = 5), @TypedProperty1='?' (DbType = Int32)], CommandType='Text', CommandTimeout='30']
      SELECT "p"."Id", "p"."Name", "p"."Price", "c"."Name" AS "CategoryName"
      FROM "Products" AS "p"
      INNER JOIN "Categories" AS "c" ON "p"."CategoryId" = "c"."Id"
      WHERE "c"."Name" = @TypedProperty
      ORDER BY "p"."Name"
      LIMIT @TypedProperty1
```

Note the `SELECT` list: it contains exactly the four projected columns. `Stock` and
`CategoryId` never leave the database, because the wire model doesn't have them — the
projection prunes the SQL, and `$select` (try
`?\$select=name,categoryName&\$orderby=name&\$top=3`) additionally prunes the JSON payload.
The client sees a flat `{ id, name, price, categoryName }` resource and can't tell that
categories live in their own table:

```json
{"@odata.context":"http://localhost:5220/odata/$metadata#ProductSummaries","value":[
  {"Id":3,"Name":"Adjustable Wrench","Price":12.25,"CategoryName":"Tools"},
  {"Id":2,"Name":"Ball-Peen Hammer","Price":17.49,"CategoryName":"Tools"},
  {"Id":1,"Name":"Claw Hammer","Price":14.99,"CategoryName":"Tools"}]}
```

Only `GetQueryable` is assigned, so `ProductSummaries` is read-only by construction — no
POST/PUT/PATCH/DELETE routes exist at all (nor a single-entity `GET /ProductSummaries({id})`,
since `GetById` is also unassigned — the collection query is this set's entire surface).

## Many-to-many without exposing the join table

`Product ⟷ Tag` is a many-to-many through a `ProductTags` join table that **has no CLR
type**: the EF model uses skip navigations (`HasMany(p => p.Tags).WithMany(t => t.Products)`)
with an implicit shared-type join entity, so the join table exists only inside SQLite. The
`ProductProfile` exposes the relationship with the batch `HasMany` overload — a single
`SelectMany` query through the join per page:

```bash
curl "http://localhost:5220/odata/Tags"
curl "http://localhost:5220/odata/Products(1)/Tags"
curl "http://localhost:5220/odata/Products?\$orderby=id&\$top=3&\$expand=Tags"
```

The `$expand=Tags` response inlines each product's tags:

```json
{"@odata.context":"http://localhost:5220/odata/$metadata#Products","value":[
  {"Id":1,"Name":"Claw Hammer","Price":14.99,"Stock":42,"CategoryId":1,
   "Tags":[{"Id":1,"Label":"bestseller"},{"Id":3,"Label":"heavy-duty"}]},
  {"Id":2,"Name":"Ball-Peen Hammer","Price":17.49,"Stock":18,"CategoryId":1,"Tags":[]},
  {"Id":3,"Name":"Adjustable Wrench","Price":12.25,"Stock":31,"CategoryId":1,"Tags":[]}]}
```

and the console shows the tags for the whole page loaded by **one** query that joins through
`ProductTags` (after the page query itself — two statements total, regardless of page size):

```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (0ms) [Parameters=[@idSet1='?' (DbType = Int32), @idSet2='?' (DbType = Int32), @idSet3='?' (DbType = Int32)], CommandType='Text', CommandTimeout='30']
      SELECT "p"."Id", "s"."Id", "s"."Label"
      FROM "Products" AS "p"
      INNER JOIN (
          SELECT "t"."Id", "t"."Label", "p0"."ProductsId"
          FROM "ProductTags" AS "p0"
          INNER JOIN "Tags" AS "t" ON "p0"."TagsId" = "t"."Id"
      ) AS "s" ON "p"."Id" = "s"."ProductsId"
      WHERE "p"."Id" IN (@idSet1, @idSet2, @idSet3)
```

Two different suppressions are at work here, and they live at different layers:

- **EF's model-level suppression** hides things from the *persistence* model: the join
  entity has no CLR type at all (only the table name `ProductTags`), and
  `modelBuilder...Ignore()` keeps `Product.Category`/`Category.Products` out of the EF model
  entirely.
- **OhData's profile `Ignore()`** (#226) hides things from the *wire* model: `TagProfile`
  ignores `Tag.Products` — the property must stay in the EF model (it's half of the skip
  navigation), but a Tag serializes as just `{ id, label }`.

Same word, different layers.

## Project tour

| File | What's in it |
|------|--------------|
| [`Models.cs`](Models.cs) | `Product`, `Category`, `Tag`, the `ProductSummary` DTO, and the `ShopDbContext` (FK-only Product↔Category — see the comment there for why those CLR navigations are `Ignore`d in EF — plus the Product⟷Tag skip navigations) |
| [`Profiles.cs`](Profiles.cs) | The four `EntitySetProfile` classes — this is the OhData part |
| [`Program.cs`](Program.cs) | `AddOhData` + `MapOhData`, `Database.Migrate()`, seeding |
| [`Migrations/`](Migrations/) | Committed EF Core migrations. To add one: `dotnet tool restore && dotnet ef migrations add <Name>` (the [`dotnet-ef` local tool](.config/dotnet-tools.json) is pinned in this directory) |

## Further reading

- [Repository README](../../README.md) — quick start, package list, benchmarks
- [docs/query-options.md](../../docs/query-options.md) — everything `$filter`/`$orderby`/`$select`/`$expand`/`$count` can do
- [docs/navigation-routing.md](../../docs/navigation-routing.md) — navigation routes, `$ref`, and the batch `$expand` pattern used here
