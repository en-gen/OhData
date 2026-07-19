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

# Collection, and the query options (quote the URL: $ and spaces)
curl "http://localhost:5220/odata/Products"
curl "http://localhost:5220/odata/Products?\$filter=price gt 10&\$orderby=name&\$top=5"
curl "http://localhost:5220/odata/Products?\$orderby=price desc&\$skip=5&\$top=3"
curl "http://localhost:5220/odata/Products?\$select=name,price"
curl "http://localhost:5220/odata/Products/\$count?\$filter=price gt 10"

# Batch-loaded $expand — one JOIN per page, not one query per row
curl "http://localhost:5220/odata/Products?\$top=3&\$expand=Category"
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
```

(PowerShell: use `Invoke-WebRequest` or single-quote the URLs so `$filter` isn't expanded as a
variable.)

## The SQL pushdown, live

The console logs every SQL statement (the `Microsoft.EntityFrameworkCore.Database.Command`
category is set to `Information` in [`appsettings.json`](appsettings.json)). Run:

```
curl "http://localhost:5220/odata/Products?\$filter=price gt 10&\$orderby=name&\$top=5"
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

## Project tour

| File | What's in it |
|------|--------------|
| [`Models.cs`](Models.cs) | `Product`, `Category`, and the `ShopDbContext` (FK-only relationship — see the comment there for why the CLR navigations are `Ignore`d in EF) |
| [`Profiles.cs`](Profiles.cs) | The two `EntitySetProfile` classes — this is the OhData part |
| [`Program.cs`](Program.cs) | `AddOhData` + `MapOhData`, `Database.Migrate()`, seeding |
| [`Migrations/`](Migrations/) | Committed EF Core migrations. To add one: `dotnet tool restore && dotnet ef migrations add <Name>` (the [`dotnet-ef` local tool](.config/dotnet-tools.json) is pinned in this directory) |

## Further reading

- [Repository README](../../README.md) — quick start, package list, benchmarks
- [docs/query-options.md](../../docs/query-options.md) — everything `$filter`/`$orderby`/`$select`/`$expand`/`$count` can do
- [docs/navigation-routing.md](../../docs/navigation-routing.md) — navigation routes, `$ref`, and the batch `$expand` pattern used here
