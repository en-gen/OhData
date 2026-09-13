# Choosing a read handler

An entity set's collection `GET` is served by exactly one of three handlers, and the choice decides
how much of the OData query pipeline the framework applies for you. Pick `GetQueryable` unless you
have a reason not to.

| Handler | Returns | Query options |
|---|---|---|
| [`GetQueryable`](#getqueryable---iqueryable-with-pushdown-recommended-for-databases) | `IQueryable<TModel>` | The framework applies them, and EF translates to SQL. |
| [`GetAll`](#getall---simple-in-memory-path) | `IEnumerable<TModel>` | `$top`/`$skip`/`$select`/`$expand` in memory; `$filter`/`$orderby` are refused. |
| [`GetODataQueryable`](#getodataqueryable---full-odata-pushdown-advanced) | `ODataQueryResult<TModel>` | You apply them yourself. |

Which options each one accepts, and the errors it returns, is in
[query options](query-options.md).

## `GetAll` - simple in-memory path

```csharp
GetAll = (ct) => OhDataResult.Success<IEnumerable<Product>>(myList);
```

Returns all items. The framework does **not** apply `$filter` or `$orderby` to the returned collection - and it does not silently ignore them either. If the client sends either of these, the request is rejected with `400 Bad Request` (`UnsupportedQueryOption`), regardless of the capability flags - `GetAll` has no `ApplyTo`/`IQueryable` pipeline to push them down to.

`$top` and `$skip`, by contrast, **are** applied on this path: they are pure post-materialization `Skip()`/`Take()` calls against the array `GetAll` (or `Search`, when `$search` is also present) returned - the same class of operation as the already-live `$select`/`$expand`/`$count` below. `$select`, `$expand`, `$count`, `$top`, `$skip`, and `$search` (when a `Search` handler is configured) are all honored on this path - `$select`/`$expand`/`$count` are each gated by its capability flag (`SelectEnabled`/`ExpandEnabled`/`CountEnabled`), exactly like the `GetQueryable` path: sending a disabled option returns `400` (`UnsupportedQueryOption`). `$top`/`$skip` need no flag - they are always live, mirroring `GetQueryable`.

`MaxTop` caps an **explicit** `$top` on this path exactly like it does on `GetQueryable`: a value
greater than `MaxTop` returns `400 Bad Request` (`InvalidQueryOption`).

An **omitted** `$top` is capped to `MaxTop` too (or to a smaller `Prefer: maxpagesize`), and the
response carries a `@odata.nextLink` for the remainder — so `GetAll` is safe by default and cannot be
coerced into returning an unbounded result set. That works because `GetAll` re-enumerates its source
on each request and applies `$skip` itself, making an offset `$skip` link a valid continuation; the
framework only ever emits a link it also honours. Note it is `$skip`, not the opaque `$skiptoken`
`GetQueryable` emits, nor the framework-private token the Priority-1 path uses.

**To opt out** — return the full set in one response, however large — set `MaxTop = null` on the
profile. An omitted `$top` then applies no cap and emits no `@odata.nextLink`. `Preference-Applied`
echoes the honoured page size, clamped so `maxpagesize` can never lift the `MaxTop` ceiling.

`@odata.count` (`$count=true`) reflects the **pre-paging** total on this path too, per §11.2.5.5 - it is computed from the full materialized array before `$skip`/`$top` are applied, not from the length of the returned page.

Use `GetAll` when your data source is small and in-memory, or when you want complete control over what is returned.

## `GetODataQueryable` - full OData pushdown (advanced)

```csharp
GetODataQueryable = (opts, ct) => ...
```

The profile receives the raw `ODataQueryOptions<TModel>` and is responsible for applying them to the data source. The capability flags and property allowlists are still enforced by the framework **before** the handler runs: a disabled option present in the request returns `400` (`UnsupportedQueryOption`) and a non-allowlisted property returns `400` (`InvalidQueryOption`) without invoking the handler. Use this when:

- You need full control over how query options are translated (e.g. custom SQL, Dapper, a remote API).
- You want to apply paging yourself and return the pre-paging total count alongside the results.

Return an `ODataQueryResult<TModel>` to supply paging metadata:

```csharp
GetODataQueryable = async (opts, ct) =>
{
    // Apply filtering, ordering, paging - however your data source requires.
    var (items, totalCount) = await myDataSource.QueryAsync(opts, ct);

    return new ODataQueryResult<TModel>
    {
        Items = items.AsQueryable(),
        TotalCount = totalCount,   // pre-paging count; used for $count=true
        NextLink = ...,            // optional; emitted as @odata.nextLink
    };
};
```

`ODataQueryResult<TModel>` properties:

| Property | Type | Description |
|----------|------|-------------|
| `Items` | `IQueryable<TModel>` | The (paged) item sequence to materialise. |
| `TotalCount` | `long?` | Pre-paging total count. Used as `@odata.count` in the response when `$count=true` is requested. Leave `null` to fall back to the length of `Items`. |
| `NextLink` | `string?` | When set, emitted as `@odata.nextLink` in the response envelope, taking priority over any framework-computed next link. Use this for cursor- or token-based pagination. |

The framework does not prescribe how `items` or `totalCount` are obtained. That is entirely up to the profile. Some data sources support retrieving both in a single operation (window functions, `COUNT(*) OVER()`); others require two separate requests. Either approach satisfies the contract — the framework only requires that `TotalCount` reflect the number of matching records **before** paging was applied.

If `TotalCount` is not set and the client sends `$count=true`, the count in the response will reflect only the current page size, which is incorrect per the OData spec. Prefer always supplying `TotalCount` when using this handler.

## Deterministic paging is the profile's responsibility

On this path the profile — not the framework — owns query application, including `$skip`. If you
return a lazily-translated `IQueryable` (an EF Core queryable, say) and rely on the framework's
`MaxTop`/`Prefer: maxpagesize` cap and its `@odata.nextLink` continuation, **you must give that
queryable a stable, total order**: a terminal `OrderBy`, typically on the entity key, or the client's
`$orderby` applied.

Without one the emitted `LIMIT`/`OFFSET` runs over an undefined row order, so a row can appear on two
pages or be skipped between them, and EF Core logs warning `10102` ("row limiting operation without
OrderBy").

The framework does not inject an order for you here. It cannot do so safely once you have applied
your own `$skip` — ordering a sliced subset is wrong — and a stable key column is your decision. This
applies even if you ignore the incoming options entirely, because the framework applies its **own**
continuation offset on top of whatever you return, and an unordered source still pages incoherently.

The `GetQueryable` path is different: there the framework owns the whole pipeline and orders paged
results by the entity key automatically.

> **Note:** `GetODataQueryable` is available on `ODataEntitySetProfile<TKey, TModel>`, not the base `EntitySetProfile<TKey, TModel>`. It requires the `OhData.AspNetCore` package. An `IQueryable<TModel>` is implicitly convertible to `ODataQueryResult<TModel>` for backward compatibility with handlers that return a bare queryable.

## `GetQueryable` - IQueryable with pushdown (recommended for databases)

```csharp
GetQueryable = () => db.Products;
```

Returns a base `IQueryable<TModel>`. The framework applies `$filter`, `$orderby`, `$skip`, and `$top` via `ApplyTo(IQueryable)`. With EF Core these become SQL clauses - only matching rows are fetched.

Enable the query capabilities you want to expose:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        FilterEnabled  = true;   // allow $filter
        OrderByEnabled = true;   // allow $orderby
        CountEnabled   = true;   // allow $count
        SelectEnabled  = true;   // allow $select
        ExpandEnabled  = true;   // allow $expand

        GetQueryable = () => db.Products;
    }
}
```

Any disabled capability returns `400 Bad Request` (`UnsupportedQueryOption`, with a message naming the option and the flag that enables it) if the client sends that query option. **All capability flags default to `false`** (inheriting from `EntitySetDefaults`) - an entity set accepts no query options until you opt in.

The single-entity route `GET /Products(1)` honors the same gates for the options it supports: `$select` requires `SelectEnabled` and `$expand` requires `ExpandEnabled`. When `ExpandEnabled` is on, `$expand` on the single-entity route inlines the requested navigation properties using the same navigation-route handlers (batch handlers included) as the collection route.

## Advanced: independent contexts with `IDbContextFactory`

Profiles are registered **scoped**, so the request-scoped `DbContext` injects directly into the
constructor — that is the pattern shown above and the default to reach for. Use
`IDbContextFactory<T>` only when a handler needs a **fresh, independently-scoped** context, for
example to run queries concurrently (a single `DbContext` instance is not thread-safe). Create it
per call and dispose it with `await using`:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(IDbContextFactory<AppDbContext> factory) : base(x => x.Id)
    {
        // Simple materializing read path (no deferred IQueryable to keep alive).
        GetAll = async ct =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return OhDataResult.Success<IEnumerable<Product>>(await db.Products.ToListAsync(ct));
        };
    }
}

// Registration:
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
```

Pair the factory with a **materializing** handler like `GetAll`, not `GetQueryable`: a
factory-created context can't back a deferred `IQueryable` without leaking (the framework enumerates
it after your method returns, so there is no safe point to dispose). The `GetQueryable` +
request-scoped `DbContext` pairing above remains the default when you want `$filter`/`$select`/
`$expand` pushed down to SQL.
