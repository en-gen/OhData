# Query Options

OhData supports the OData 4.0 system query options. Which ones are applied depends on the collection handler you choose for the entity set.

## JSON property casing

By default OhData serializes response property names in **PascalCase** — the CLR property names,
which are exactly the identifiers declared in `$metadata` (the EDM). Payload casing therefore
matches `$metadata` casing, satisfying OData §4.4 and letting case-sensitive OData-native clients
(e.g. `Microsoft.OData.Client`) bind properties out of the box.

This default is **owned by OhData**, not inherited from the host's
`HttpJsonOptions.SerializerOptions.PropertyNamingPolicy`. Configuring `ConfigureHttpJsonOptions`
does *not* change OhData response casing (any custom converters/encoder you register there are
still honoured — only the property-naming policy is OhData's own).

To emit **camelCase** payloads instead, opt in explicitly on the registration:

```csharp
using System.Text.Json;

builder.Services.AddOhData(o =>
{
    o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase);
    o.AddEntitySetProfile<ProductProfile>();
});
```

`WithJsonPropertyNamingPolicy(null)` is the default (PascalCase). The policy applies uniformly to
every response path: collection and single-entity reads, POST/PUT/PATCH echoes, `$select`/`$expand`
output, `$value`, and bound/unbound function/action results.

> **Known limitation of the camelCase opt-in:** `$metadata` always uses the PascalCase CLR/EDM
> property names (the EDM has no naming policy). Opting into camelCase therefore desyncs your
> payload casing from `$metadata` — a case-sensitive OData-native client that reads `$metadata` to
> learn property names will not match the camelCase keys on the wire. The PascalCase default keeps
> payloads and `$metadata` in agreement (OData §4.4); opt into camelCase only when your clients bind
> case-insensitively.

> Note: this affects **response** casing only. OData query-option property references
> (`$select=Name`, `$filter=…`, `$orderby=…`, `$expand=…`) and request bodies are matched
> case-insensitively against the EDM, so a client may use either casing on the way in.

The OpenAPI/Swagger companion packages (`OhData.AspNetCore.OpenApi`, `.NSwag`, `.Swashbuckle`)
follow this same policy: generated schema property names match the wire casing exactly — PascalCase
by default, camelCase when you opt in — instead of the host `HttpJsonOptions` casing the underlying
generators would otherwise use. A `[JsonPropertyName]` rename still wins, in the schema and on the
wire alike. So the generated document (and any client code generated from it) agrees with what
responses actually emit.

## Handler paths

### `GetAll` - simple in-memory path

```csharp
GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(myList);
```

Returns all items. The framework does **not** apply `$filter` or `$orderby` to the returned collection - and it does not silently ignore them either. If the client sends either of these, the request is rejected with `400 Bad Request` (`UnsupportedQueryOption`), regardless of the capability flags - `GetAll` has no `ApplyTo`/`IQueryable` pipeline to push them down to.

`$top` and `$skip`, by contrast, **are** applied on this path: they are pure post-materialization `Skip()`/`Take()` calls against the array `GetAll` (or `Search`, when `$search` is also present) returned - the same class of operation as the already-live `$select`/`$expand`/`$count` below. `$select`, `$expand`, `$count`, `$top`, `$skip`, and `$search` (when a `Search` handler is configured) are all honored on this path - `$select`/`$expand`/`$count` are each gated by its capability flag (`SelectEnabled`/`ExpandEnabled`/`CountEnabled`), exactly like the `GetQueryable` path: sending a disabled option returns `400` (`UnsupportedQueryOption`). `$top`/`$skip` need no flag - they are always live, mirroring `GetQueryable`.

`MaxTop` caps an **explicit** `$top` on this path exactly like it does on `GetQueryable`: a `$top` value greater than `MaxTop` returns `400 Bad Request` (`InvalidQueryOption`). As of #201, an **omitted** `$top` is also capped to `MaxTop` (or a smaller `Prefer: maxpagesize`), and the response carries a `@odata.nextLink` for the remainder - so `GetAll` is safe-by-default and can no longer be coerced into returning an unbounded result set. This became possible because `GetAll` re-enumerates its source on each request, so an offset `$skip` link is a valid continuation story (the same `$skip` scheme the Priority-1 path uses; note it is `$skip`, not the opaque `$skiptoken` `GetQueryable` emits). **To opt out** - return the full set in one response, however large - set `MaxTop = null` on the profile; an omitted `$top` then applies no cap and emits no `@odata.nextLink`. `Preference-Applied` echoes the honored page size, clamped so `maxpagesize` can never lift the `MaxTop` ceiling.

`@odata.count` (`$count=true`) reflects the **pre-paging** total on this path too, per §11.2.6.5 - it is computed from the full materialized array before `$skip`/`$top` are applied, not from the length of the returned page.

Use `GetAll` when your data source is small and in-memory, or when you want complete control over what is returned.

### `GetODataQueryable` - full OData pushdown (advanced)

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

### Deterministic paging is the profile's responsibility

On this path the profile — not the framework — owns query application, including `$skip`. When you return a lazily-translated `IQueryable` (e.g. an EF Core queryable) and rely on the framework's `MaxTop`/`Prefer: maxpagesize` cap plus its `@odata.nextLink` continuation, **you must give that queryable a stable, total order** — a terminal `OrderBy` (typically the entity key), or by applying the client's `$orderby`. Without one, the emitted `LIMIT`/`OFFSET` runs over an undefined row order, so a row can appear on two pages or be skipped between them, and EF Core logs warning `10102` ("row limiting operation without OrderBy"). The framework does not inject an order for you here: it can't do so safely once you've applied your own `$skip` (ordering a sliced subset is wrong), and a stable key column is your decision, not the framework's. (The `GetQueryable` path is different — there the framework owns the whole pipeline and orders paged results by the entity key automatically.)

> **Note:** `GetODataQueryable` is available on `ODataEntitySetProfile<TKey, TModel>`, not the base `EntitySetProfile<TKey, TModel>`. It requires the `OhData.AspNetCore` package. An `IQueryable<TModel>` is implicitly convertible to `ODataQueryResult<TModel>` for backward compatibility with handlers that return a bare queryable.

### `GetQueryable` - IQueryable with pushdown (recommended for databases)

```csharp
GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
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

        GetQueryable = (_) => Task.FromResult(db.Products.AsQueryable());
    }
}
```

Any disabled capability returns `400 Bad Request` (`UnsupportedQueryOption`, with a message naming the option and the flag that enables it) if the client sends that query option. **All capability flags default to `false`** (inheriting from `EntitySetDefaults`) - an entity set accepts no query options until you opt in.

The single-entity route `GET /Products(1)` honors the same gates for the options it supports: `$select` requires `SelectEnabled` and `$expand` requires `ExpandEnabled`. When `ExpandEnabled` is on, `$expand` on the single-entity route inlines the requested navigation properties using the same navigation-route handlers (batch handlers included) as the collection route.

### Production pattern: `IDbContextFactory`

Profiles are singletons, so a scoped `DbContext` cannot be injected directly. Use `IDbContextFactory<T>`:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(IDbContextFactory<AppDbContext> factory) : base(x => x.Id)
    {
        FilterEnabled  = true;
        OrderByEnabled = true;

        GetQueryable = async (_) =>
        {
            var db = await factory.CreateDbContextAsync();
            return db.Products.AsQueryable();
        };
    }
}

// Registration:
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString));
```

---

## `$filter`

Enabled via `FilterEnabled = true`. Supports comparison operators (`eq`, `ne`, `gt`, `ge`, `lt`, `le`), logical operators (`and`, `or`, `not`), arithmetic, string functions (`contains`, `startswith`, `endswith`, `tolower`, `toupper`, `trim`), date functions, and more.

```
GET /odata/Products?$filter=Price gt 10 and contains(Name,'Widget')
GET /odata/Products?$filter=year(CreatedAt) eq 2024
```

Restrict which properties may appear in `$filter`:

```csharp
FilterProperties(x => x.Price, x => x.Name, x => x.Category);
// or string overload:
FilterProperties("Price", "Name", "Category");
```

A `$filter` referencing a property outside the allowlist returns `400 Bad Request`
(`InvalidQueryOption`, "The property 'X' cannot be used in the $filter query option.").

`FilterProperties` restricts this entity's own structural properties only; it never restricts
a path through a navigation property. `$filter=Lines/any(l: l/Quantity gt 1)` is unaffected by
`Orders`' own `FilterProperties` allowlist (or the lack of one) because navigation-target types
(`OrderLine` here) have no allowlist surface of their own - only `FilterProperties` on the
navigated-to entity set's own profile (if it has one) governs its properties.

### `round()` midpoint rounding

OData Part 2 §5.1.1.9 specifies that the `round()` canonical function rounds a midpoint value
*away from zero* (`2.5 → 3`, `-2.5 → -3`). Microsoft.OData's `ApplyTo` binder instead emits
.NET's single-argument `Math.Round(double)`/`Math.Round(decimal)`, which default to
*round-half-to-even* ("banker's rounding": `2.5 → 2`). On the `GetQueryable` path (and its
`$count` companion), OhData rewrites those calls in the post-`ApplyTo` expression tree to the
two-argument `Math.Round(value, MidpointRounding.AwayFromZero)` overload, so `round()` matches
the spec by default:

```
GET /odata/Products?$filter=round(Price) eq 3
```

Control this via the `RoundingMode` setting (`RoundingMode.SpecCompliant`, the default, or
`RoundingMode.BankersRounding`), inheriting from `EntitySetDefaults.RoundingMode` the same way
`PropertyAccessEnabled`/`AllowDeepInsert` do:

```csharp
// Per profile - opt back into .NET's pre-fix banker's rounding:
RoundingMode = RoundingMode.BankersRounding;

// Or globally across all profiles in the registration:
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.RoundingMode = RoundingMode.BankersRounding)
    .AddEntitySetProfile<ProductProfile>());
```

**Provider-translation caveat:** the two-argument `Math.Round(value, MidpointRounding)` overload
is not translatable by every EF Core provider - a query using `round()` that worked before this
fix may throw a translation exception against your provider. If that happens, set
`RoundingMode = BankersRounding` on the affected profile (or globally) to fall back to the
single-argument overload that provider could already translate; this restores the pre-fix
(banker's rounding) behavior and documents the spec deviation locally. EF Core InMemory (used in
this repo's test suite) is LINQ-to-Objects and is unaffected either way.

**Coverage note:** this rewrite only reaches the base-class `GetQueryable` path, where the
framework itself calls `ApplyTo`. On the Priority-1 `ODataEntitySetProfile.GetODataQueryable`
path the profile calls `ApplyTo` itself inside its own handler, so `RoundingMode` does not
automatically apply there - a profile using that path must apply the same rewrite itself if it
wants spec-compliant `round()` semantics.

---

## `$orderby`

Enabled via `OrderByEnabled = true`. Supports multiple sort keys, ascending (`asc`, default) and descending (`desc`).

```
GET /odata/Products?$orderby=Category asc,Price desc
```

Restrict which properties may be sorted on:

```csharp
OrderByProperties(x => x.Price, x => x.Name);
```

Sorting on a property outside the allowlist returns `400 Bad Request` (`InvalidQueryOption`).
As with `FilterProperties`, this only restricts the entity's own structural properties -
`$orderby=Category/Name` (a path through a navigation property) is unaffected.

---

## `$top` and `$skip`

Limit and offset the result set. On the `GetQueryable` path these become SQL `LIMIT`/`OFFSET`; on `GetAll` they are applied as an in-memory `Skip()`/`Take()` against the materialized collection, after `GetAll`/`Search` runs and before `$select`/`$expand` are applied to the page.

```
GET /odata/Products?$top=20&$skip=40
```

Cap the maximum `$top` value server-side:

```csharp
// Per profile:
MaxTop = 100;

// Or globally across all profiles in the registration:
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxTop = 500)
    .AddEntitySetProfile<ProductProfile>());
```

**`MaxTop` defaults to `1000`** (`EntitySetDefaults.MaxTop`) when not overridden per-profile or globally - server-side paging is always active on the `GetQueryable`/`GetAll`/Priority-1 paths, even if you never configure it explicitly.

Requests with `$top` exceeding `MaxTop` receive `400 Bad Request`, on every collection path (`GetQueryable`, `GetAll`, and Priority-1).

On `GetQueryable` **and Priority-1 (`GetODataQueryable`)**, an **omitted** `$top` also gets `MaxTop` (or a smaller `Prefer: maxpagesize`) applied implicitly as the default page size, and the response carries `@odata.nextLink` so the client can retrieve the rest. `Prefer: maxpagesize` (see the [`Prefer` header docs](spec-compliance.md#prefer-header)) is capped at `MaxTop` when `$top` is absent: the honored page size is `min(maxpagesize, MaxTop)`. A client cannot use `maxpagesize` to request a page larger than `MaxTop` - it can only ask for a *smaller* page. `Preference-Applied` always echoes the page size actually honored (the clamped value), not the value the client asked for, per §8.2.8.7.

The two paths differ only in the shape of the continuation link. `GetQueryable` emits an opaque `$skiptoken` (which the framework decodes back to a `$skip` itself). Priority-1 emits a plain `$skip` instead, because that path hands the incoming `ODataQueryOptions` to the profile's own `ApplyTo`, which honors `$skip` natively but has no handler for `$skiptoken`. On a Priority-1 continuation request the profile applies the `$skip`, and the framework re-applies only the `MaxTop`/`maxpagesize` `Take` cap on top. A profile that sets `ODataQueryResult.NextLink` itself is trusted to be paging on its own terms, and the framework does not add or override the cap in that case.

**`GetAll` now mirrors the "omitted `$top`" behavior above (#201).** An omitted `$top` is capped to `MaxTop` (or a smaller `Prefer: maxpagesize`) with a `@odata.nextLink` for the remainder, so this path is safe-by-default like the others. The one difference from `GetQueryable` is the continuation shape: `GetAll` emits a `$skip` link (which it re-applies against its re-enumerated source) rather than the opaque `$skiptoken`. Set `MaxTop = null` on the profile to opt out and return the full set in one response, however large - see the `GetAll` section above.

---

## `$count`

Enabled via `CountEnabled = true`. Two forms:

**Inline count** - embed the total (pre-pagination) count in the collection envelope:

```
GET /odata/Products?$count=true
```

```json
{
  "@odata.context": "https://host/odata/$metadata#Products",
  "@odata.count": 1234,
  "value": [...]
}
```

**Standalone count** - returns a plain integer, `$filter` is applied if present:

```
GET /odata/Products/$count
GET /odata/Products/$count?$filter=Price gt 10
```

Gating: the **inline** form (`$count=true`) is gated by `CountEnabled`. The **standalone**
`/$count` route is always registered when a collection handler exists (it is an addressable
resource, not a query option) - on that route only `$filter` is gated, by `FilterEnabled`
(and the `FilterProperties` allowlist).

Behaviour depends on the handler path:

| Handler | `$count=true` behaviour |
|---|---|
| `GetODataQueryable` | Uses `TotalCount` from `ODataQueryResult<TModel>`. If not supplied, falls back to the current page size - **incorrect per spec**. Always set `TotalCount` on this path. |
| `GetQueryable` | Framework runs a second `COUNT(*)` query against the `IQueryable` before paging is applied. |
| `GetAll` | Full collection is enumerated and counted. |

---

## `$select`

Enabled via `SelectEnabled = true`. Reduces the response payload to the specified properties:

```
GET /odata/Products?$select=Id,Name,Price
```

The response shape is produced by JSON post-processing (unselected properties are removed from
the serialized entity), which is what keeps the output consistent with the configured naming
policy (PascalCase by default — see [JSON property casing](#json-property-casing)).

### Projection pushdown (#206)

On the `GetQueryable` path, an eligible `$select` additionally pushes a **column projection**
down to the data source: the framework composes a member-init projection
(`x => new TModel { Id = x.Id, Name = x.Name }`) onto the queryable before enumeration, so LINQ
providers emit a column-pruned `SELECT` instead of reading every column. The wire output is
**byte-identical** with or without pushdown — the projection changes the SQL, never the
response.

The projected member set is the selected structural properties **plus the entity key** (needed
for `@odata.id` and `$expand` correlation) **plus any `UseETag` properties** (so `@odata.etag`
values are unchanged). Nested `$select` paths (`$select=address/city`) project the whole
top-level member.

Pushdown is **on by default** (`EntitySetDefaults.SelectPushdownEnabled`, per-profile
`SelectPushdownEnabled` override) and falls back silently to the full fetch — with a
Debug-level log naming the reason — when a request is ineligible:

- the model has no public parameterless constructor (e.g. positional records),
- a projected member is **complex-typed** (phase-1 boundary: projecting an EF-*owned* complex
  property under a tracking queryable throws inside EF; `byte[]` counts as primitive, so
  rowversion ETag inputs keep pushdown),
- a projected member has no public setter (init-only setters are fine; this arises via
  `UseETag` selectors over get-only computed properties, since the EDM excludes get-only
  properties from `$select` itself),
- `UseETag` was configured with a non-direct (computed) selector, making the ETag property
  names unknowable,
- the model has structural properties whose names differ only by case (the name lookup is
  case-insensitive, so such models are pushdown-ineligible outright),
- or the profile/server opted out via `SelectPushdownEnabled = false` (do this for exotic
  `IQueryable` providers that cannot translate member-init projections; every EF Core
  relational provider and InMemory can).

`GetAll` (no queryable) and `GetById` (no collection query) have no pushdown path. On the
Priority-1 `GetODataQueryable` path the profile owns the `ApplyTo` call, so — like
`RoundingMode` — the framework does not project automatically; a Priority-1 handler that wants
column pruning applies its own `Select` projection (it already owns the whole query pipeline).

Restrict which properties may be selected:

```csharp
SelectProperties(x => x.Id, x => x.Name, x => x.Price);
```

Selecting a property outside the allowlist returns `400 Bad Request` (`InvalidQueryOption`).

---

## `$expand`

Enabled via `ExpandEnabled = true`. Embeds related entities inline in the parent response:

```
GET /odata/Orders?$expand=Lines
GET /odata/Orders?$expand=Lines($select=ProductName,Quantity)
GET /odata/Orders?$expand=Lines,Customer
GET /odata/Orders(3f2a...)?$expand=Lines        ← single-entity route too
```

For a navigation **declared with a delegate**, `$expand` does **not** use EF Core's `Include()` or push the join into SQL. Instead the framework invokes that navigation's registered handler. This is a generic mechanism with no EF Core dependency, and it behaves identically on the `GetQueryable`, `GetAll`, and Priority-1 (`IODataEntitySetEndpointSource`) paths. See [navigation-routing.md](navigation-routing.md) for details. A navigation **declared without a delegate** takes a different path — SQL-JOIN pushdown — described in [Delegate-less navigations JOIN automatically](#expand-pushdown-delegate-less-navigations-join-automatically-206) below.

There are two ways to register the handler, and they have very different `$expand` performance:

- **Per-entity (`getAll`/`get`)** - invoked once per parent entity per expanded property. For a
  page of *N* items with *P* expanded properties, that's *N×P* sequential awaited calls (an N+1
  query pattern when the handler hits a database). Simple to write; fine for small pages or
  handlers with no per-call cost.
- **Batch (`batchGetAll`/`batchGet`)** - invoked **once per expanded property per page**,
  receiving every parent key on the page at once. *N×P* collapses to *P*. This is the
  recommended form for EF Core-backed handlers.

Navigation properties must be declared in the profile:

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        ExpandEnabled = true;

        // Batch form: ONE query loads every order's lines for the whole page.
        HasMany(x => x.Lines, batchGetAll: async (orderIds, ct) =>
        {
            var lines = await db.OrderLines.Where(l => orderIds.Contains(l.OrderId)).ToListAsync(ct);
            return lines.ToLookup(l => l.OrderId);
        });

        // Per-entity form: one query PER order (N+1 under $expand).
        HasOptional(x => x.Customer,
            get: (orderId, ct) => Task.FromResult(db.Customers.Find(orderId)));

        GetQueryable = (_) => Task.FromResult(db.Orders.AsQueryable());
    }
}
```

`HasMany`'s batch overload returns an `ILookup<TKey, TNavigation>` (e.g. via `.ToLookup(...)`); `HasOptional`/`HasRequired`'s batch overloads return an `IReadOnlyDictionary<TKey, TNavigation?>`/`IReadOnlyDictionary<TKey, TNavigation>`. A parent key missing from the result is treated as "no children" (`[]`) for a collection nav, or "no related entity" (`null`) for a single-valued nav.

Registering only the batch overload is enough - the framework auto-derives a single-key handler from it, so the standalone `GET /Orders(id)/Lines` route, nav `$count`, and `$ref` endpoints all keep working without writing a second handler. You may still register both explicitly (e.g. if the single-key path warrants a different query shape), in which case the per-entity handler you supply is used for those standalone routes and the batch handler is used only for `$expand`.

Restrict which navigation properties may be expanded:

```csharp
ExpandProperties(x => x.Lines, x => x.Customer);
```

Expanding a navigation property outside the allowlist returns `400 Bad Request` (`InvalidQueryOption`).

<a id="expand-pushdown-delegate-less-navigations-join-automatically-206"></a>
### `$expand` pushdown: delegate-less navigations JOIN automatically (#206)

> **The one rule to remember:** writing an expand delegate opts a navigation **out** of pushdown; a bare declaration opts it **in**.
>
> **Mental model:** write a delegate only when expansion needs real logic (filtering, ordering, authorization, a custom query shape). A plain relationship gets SQL-JOIN expansion for free.

A navigation declared **without** any expand delegate — a bare `HasMany(x => x.Lines)` / `HasOptional(x => x.Ref)` / `HasRequired(x => x.Ref)` with no `getAll`/`get`/`batchGetAll`/`batchGet` — is now **SQL-JOIN-expandable automatically**. On the EF Core-backed `GetQueryable` path, `$expand`'ing such a navigation folds it into the collection query's projection (`x => new Order { …, Lines = x.Lines.ToList() }`), so **one JOIN'd query** loads the page and all its related rows — no delegate to write, no N+1. This is why the earlier caveat ("a `HasMany(x => x.Lines)` alone is silently skipped under `$expand`") no longer holds: a bare declaration is a first-class, pushed expansion.

The behavior is decided **purely by whether a delegate exists** — there is no global flag to flip and no per-navigation opt-in:

| Declaration | `$expand` path | Why |
|---|---|---|
| `HasMany(x => x.Lines)` — **no delegate** | **SQL-JOIN pushdown** (one query) | There is no delegate to bypass; the `Include`/JOIN *is* the definition of the expansion. |
| `HasMany(x => x.Lines, getAll: …)` / `batchGetAll: …` — **has a delegate** | **Delegate** (never pushed down) | The delegate may filter/order/authorize; pushing it down would change results or leak rows, so it is always honored. |

This is **not** "byte-identical to the delegate path" — for a pushed navigation there is no delegate to compare against; the JOIN *is* the source of the related rows. (The un-pushed, delegate path stays exactly as documented above.)

Pushdown is **on by default** (`EntitySetDefaults.ExpandPushdownEnabled`, per-profile `ExpandPushdownEnabled` override). It engages **only** on the EF Core-backed `GetQueryable` path and **only** for a navigation whose related type has no back-reference cycle. Whenever pushdown is ineligible for a request — a non-EF provider, a cyclic navigation, a deferred nested option (see the table below), or a projection/translation/serialization failure — it **falls back silently**: the delegate-less navigation simply stays EDM-only for that request (as it was before this feature), the request still succeeds (never a `500`), and the reason is `Debug`-logged. A delegate-backed navigation is **never** affected — it always expands through its delegate. Set `ExpandPushdownEnabled = false` (per profile or in `WithDefaults`) to keep every delegate-less navigation unexpandable.

`$expand` pushdown composes with `$select` pushdown: `?$select=name&$expand=Lines` prunes the parent's column list *and* JOINs the lines in the same single query. The two capabilities are **independent** — disabling `SelectPushdownEnabled` does not disable `$expand` pushdown, and an `$expand` push never column-prunes the parent on its own.

#### Nested options on a pushed `$expand`

A pushed (delegate-less) `$expand` honors the nested options of the expanded collection. `$filter`, `$orderby`, and `$top`/`$skip` are pushed down to SQL as a **filtered / ordered / paged `Include`** (translated by Microsoft's own OData `FilterBinder`/`OrderByBinder`, so the semantics match a top-level `$filter`/`$orderby`), producing a single JOIN'd query — no per-parent N+1. `$count` and `$select` are then applied to the serialized result (in whatever naming policy is configured — PascalCase by default).

| Nested option (on a delegate-less pushed nav) | Supported | How |
|---|---|---|
| `$select` — `Children($select=name)` | ✅ | JSON projection of the expanded elements (configured naming policy preserved) |
| `$filter` — `Children($filter=active eq true)` | ✅ | filtered `Include` (SQL `WHERE` in the JOIN) |
| `$orderby` — `Children($orderby=name desc)` | ✅ | ordered `Include` (SQL `ORDER BY` in the JOIN) |
| `$top` / `$skip` — `Children($orderby=name;$top=5)` | ✅ | paged `Include` (SQL `ROW_NUMBER` window) |
| `$count` — `Children($count=true)` | ✅ | inline `Children@odata.count` = full filtered count (paging is applied after counting, per §11.2.4.2) |
| **nested `$expand`** — `Children($expand=Grandkids)` | ✅ | multi-level pushdown: folded into the same query as an `Include`→`ThenInclude` JOIN when every level is delegate-less (see [Multi-level `$expand`](#multi-level-expand-and-levels-206) below) |
| `$levels` — `Children($levels=2)` / `Children($levels=max)` | ✅ | recursive self-referential expand, bounded by `MaxExpansionDepth` (see below) |
| `$search` / `$compute` / `$apply` | ❌ (deferred) | not implemented on the pushdown path |

A deferred nested option is not an error: the request still returns `200`, but the delegate-less navigation that carried it stays EDM-only (empty) for that request. Nested options on a **delegate-backed** navigation follow the delegate path and are subject to that path's own support (see [navigation-routing.md](navigation-routing.md)); they never engage pushdown.

<a id="multi-level-expand-and-levels-206"></a>
#### Multi-level `$expand` and `$levels` (#206)

A nested `$expand` is pushed **recursively**: `?$expand=Books($expand=Chapters($expand=Pages))` folds all three levels into one JOIN'd query (EF Core `Include`→`ThenInclude`), applying each level's own nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select`. A branch is pushed only when it is **delegate-less at every level**; the moment a level's navigation carries a delegate (or is cyclic / a non-projectable type), that whole branch is deferred off pushdown and resolves through the existing path — a **delegate-backed navigation is never EF-included at any depth**, so the delegate is never bypassed. A delegate-backed navigation reached directly from the root (or under delegate-backed ancestors) still expands through its delegate exactly as before; a delegate navigation nested *beneath* a delegate-less pushed one stays empty (it is never JOIN-loaded).

`$levels=N` recursively expands a **self-referential** navigation (a tree/hierarchy) `N` levels deep — `?$expand=Children($levels=2)` — as a bounded, cycle-free projection (each level is a fresh POCO; the deepest loaded level terminates the recursion). `$levels=max` resolves to the configured `MaxExpansionDepth`. Both are capped at `MaxExpansionDepth`: a `$levels` (or a nested `$expand`) that resolves deeper is rejected with `400` before any handler runs (see [Complexity limits](#complexity-limits-202)). A `$levels` expand that *also* carries other nested options (`$filter`/`$select`/…) is deferred off pushdown (a rare combination) and stays EDM-only.

The ceiling is advertised in `$metadata` as the `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` annotation on each entity set, so a client can discover it before issuing a request.

**Caveats.**

- **Nested options are not gated by the parent profile's property allowlists.** `FilterProperties`/`OrderByProperties`/`SelectProperties` restrict the *root* entity set only; a navigation-target type has no allowlist surface of its own and is treated as fully queryable (this is the same design decision that lets nav-path `$filter` work — see `MarkNavigationTargetTypesFullyQueryable`). So `$expand=Children($filter=…)`/`($orderby=…)`/`($select=…)` may reference any column of the child type regardless of what the parent restricted. Model your navigation targets accordingly (e.g. don't expose a sensitive column on a type reachable via a delegate-less navigation you `$expand`), or write an expand **delegate** for that navigation (which opts it out of pushdown and lets you enforce your own shaping).
- **`$count` on a pushed expand materializes the full filtered child collection.** To report `Nav@odata.count` accurately, the whole filtered set is loaded before `$top`/`$skip` paging is applied — the same amount of data a bare `$expand=Nav` already loads. `$top`/`$skip` *without* `$count` push the paging into SQL and transfer only the page. There is no per-navigation `MaxTop` ceiling on a nested `$top`.
- **Nested paging without a nested `$orderby` is stabilized by the child's key.** When `$top`/`$skip` are pushed to SQL without a nested `$orderby`, the navigation element's single key is appended as a deterministic tiebreaker (mirroring the root path). A composite-keyed child type is left to the provider's order.

To also expose navigation as a standalone HTTP route (`GET /Orders(id)/Lines`), provide a handler to `HasMany` - see [navigation-routing.md](navigation-routing.md).

---

## Complexity limits (#202)

Four ceilings bound how expensive a single request's query options may be. Each is configurable globally via `WithDefaults` or per entity set on the profile (the profile value overrides the global default); a request that exceeds a limit is rejected with `400` before any handler runs. They apply on all three collection read paths (`GetQueryable`, `GetAll`, Priority-1).

| Limit | Default | Bounds |
|---|---|---|
| `MaxExpansionDepth` | `3` | Nesting depth of `$expand`, and the ceiling `$levels` is resolved and capped to (`$levels=max` becomes exactly this value). **Enforced** as of #202 — a deeper `$expand`/`$levels` returns `400` rather than a silently-truncated result. Advertised per entity set in `$metadata` as `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` (#206). Raise it to allow deeper graph/hierarchy queries, or lower it to harden. |
| `MaxFilterNodeCount` | `10000` | Number of nodes in a `$filter` expression tree. |
| `MaxOrderByNodeCount` | `1000` | Number of nodes in an `$orderby`. |
| `MaxAnyAllExpressionDepth` | `1000` | Nesting depth of `any()`/`all()` lambdas in a `$filter`. |

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => { d.MaxExpansionDepth = 3; d.MaxFilterNodeCount = 200; })
    .AddEntitySetProfile<OrderProfile>());

public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile() { MaxExpansionDepth = 5; /* this set allows deeper expands than the default */ }
}
```

The node-count defaults are unchanged from what OhData already applied (they were previously hardcoded); #202 makes them lowerable. Note that `$top`/`$skip` are governed separately by `MaxTop` (see above), not by these node counts.

---

## `$search`

Register a `Search` handler to support free-text search:

```csharp
Search = (term, ct) => Task.FromResult<IEnumerable<Product>>(
    db.Products
      .Where(p => p.Name.Contains(term) || p.Description.Contains(term))
      .ToList());
```

```
GET /odata/Products?$search=widget
```

Without a `Search` handler, `$search` requests return `400 Bad Request` (`UnsupportedQueryOption`). The interpretation of the search term is entirely up to the handler.

On the `GetQueryable` path, `$search` composes with the other query options: the handler's results become the base sequence, and `$filter`, `$orderby`, `$top`, and `$skip` are then applied on top of the search results (in that order). On the `GetAll` path, `$search` composes the same way with the options `GetAll` supports: the handler's results become the base sequence, and `$top`/`$skip` are applied on top of them (`$filter`/`$orderby` remain unsupported on this path regardless of `$search`).

---

## `$skiptoken` (server-driven paging)

When a response includes `@odata.nextLink` (emitted once the page size reaches `MaxTop` or the client-requested `maxpagesize`), the link contains a `$skiptoken` value:

```
GET /odata/Products?$top=20
→ "@odata.nextLink": "https://host/odata/Products?$top=20&$skiptoken=MjA="
```

**`$skiptoken` is a Base64-encoded raw 4-byte little-endian integer - the literal skip offset - not an opaque or cryptographically-protected cursor.** A client (or anyone who intercepts a link) can trivially decode, predict, or forge a token to jump to an arbitrary offset; it provides no more protection than sending `$skip` directly. Don't rely on it to gate access to specific pages or ranges of data - apply authorization/filtering in the handler itself if that matters.

A malformed or corrupted `$skiptoken` (wrong length, invalid Base64) returns `400 Bad Request` (`InvalidSkipToken`). If both `$skip` and `$skiptoken` are present, `$skip` takes precedence.

---

## Error responses

Invalid or disabled query options return `400 Bad Request` with an OData error body. A disabled
capability flag produces `UnsupportedQueryOption`:

```json
{ "error": { "code": "UnsupportedQueryOption", "message": "This resource does not support $filter. Set FilterEnabled = true on the profile (or the corresponding EntitySetDefaults property) to enable it." } }
```

A syntactically invalid option, an unknown property, or a property outside a configured
allowlist produces `InvalidQueryOption`:

```json
{ "error": { "code": "InvalidQueryOption", "message": "The property 'Id' cannot be used in the $filter query option." } }
```
