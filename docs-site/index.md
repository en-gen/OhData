---
_layout: landing
---

# OhData

> **DRAFT — needs maintainer review**
>
> This landing page was distilled from `README.md` for the documentation site (issue #208).
> Verify the value proposition, demo links, and version numbers before publishing.

**Convention-based OData 4.0 server and typed client for ASP.NET Core.** Define a profile
class, assign only the handler delegates you need, and get a spec-faithful OData API — no
controllers required. Consume it from .NET with a fluent, LINQ-native client.

```csharp
// A profile IS the API surface — handlers you assign become routes; handlers you skip don't.
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled = OrderByEnabled = CountEnabled = SelectEnabled = true;

        // IQueryable path → EF Core translates $filter/$orderby/$skip/$top into SQL
        GetQueryable = _ => Task.FromResult(db.Products.AsQueryable());
        GetById      = (id, ct) => db.Products.FindAsync([id], ct).AsTask();
        Post         = async (p, ct) => { db.Products.Add(p); await db.SaveChangesAsync(ct); return p; };
    }
}

builder.Services.AddOhData(o => o.WithPrefix("/odata").AddProfile<ProductProfile>());
app.MapOhData();
```

## Why OhData

- **No controllers, no attributes.** A declarative profile class per entity set is the whole
  API definition. Handlers you assign register routes; handlers you leave `null` don't exist.
- **Real SQL pushdown.** The `GetQueryable` path composes `$filter`/`$orderby`/`$skip`/`$top`
  onto an `IQueryable`, so EF Core turns them into `WHERE`/`ORDER BY`/`LIMIT`/`OFFSET` executed
  by the database — not fetched-then-filtered in memory.
- **Spec-faithful.** Service document, `$metadata` CSDL, the OData error envelope, ETag
  concurrency, navigation routing and `$ref`, bound functions and actions. See the
  [spec-compliance reference](../docs/spec-compliance.md) for exactly what's covered.
- **Fast.** OhData's minimal-API pipeline beat `Microsoft.AspNetCore.OData`'s
  `ODataController` + `[EnableQuery]` pipeline on all 11 benchmarked scenarios (writes ~5–6×,
  full-page reads ~3–3.7×). See [performance](https://github.com/en-gen/OhData/blob/develop/src/OhData.Server.Benchmarks/docs/server-comparison-report.md).
- **A typed client to match.** [`OhData.Client`](../docs/client.md) translates LINQ filter/select/expand
  into OData query strings and follows `@odata.nextLink` automatically.

## Try it live

Fire real `$filter`/`$orderby`/`$expand` queries (writes too) at a deployed demo service, or hit
the raw [v2 service document](https://ohdata.onrender.com/v2) directly:

- [Scalar API reference](https://ohdata.onrender.com/scalar/v2)
- [Swagger UI](https://ohdata.onrender.com/swagger)

(Free-tier hosting: the first load after a quiet spell takes a moment to wake up, and demo data
is ephemeral.)

## Install

```bash
dotnet add package EnGen.OhData.AspNetCore   # server framework
dotnet add package EnGen.OhData.Client        # typed LINQ client
```

Optional API-documentation companions — one line each, matched to your OpenAPI stack:
`EnGen.OhData.AspNetCore.OpenApi`, `EnGen.OhData.AspNetCore.Swashbuckle`,
`EnGen.OhData.AspNetCore.NSwag`.

## Start here

- **[Installation & quick start](getting-started.md)** — zero to a running OData API.
- **[EF Core + SQLite tutorial](ef-core-sqlite.md)** — put a real relational database behind
  OhData and watch the SQL.
- **[Architecture](../docs/architecture.md)** — the core flow and design decisions.
- **[Query options](../docs/query-options.md)** — everything `$filter`/`$orderby`/`$select`/`$expand`/`$count`/`$search` can do.
- **[Migrating from Microsoft.AspNetCore.OData](../docs/migrating-from-microsoft-odata.md)** — coming from `ODataController`.
