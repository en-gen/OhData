# Query Options

OhData supports the OData 4.0 system query options. Which ones are applied depends on the collection handler you choose for the entity set.

## JSON property casing

By default OhData serializes response property names in **PascalCase** — the CLR property names,
which are exactly the identifiers declared in `$metadata` (the EDM). Payload casing therefore
matches `$metadata` casing, which is what lets case-sensitive OData-native clients
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
> payloads and `$metadata` in agreement; opt into camelCase only when your clients bind
> case-insensitively.

> Note: this affects **response** casing only. OData query-option property references
> (`$select=Name`, `$filter=…`, `$orderby=…`, `$expand=…`) are matched case-insensitively against
> the EDM, so a client may use either casing on the way in. Request **bodies** are matched against
> the serializer contract the deserializer itself resolves — i.e. the policy-converted name — with
> the EDM and CLR names accepted as case-insensitive aliases. With the PascalCase default and with
> camelCase those are the same set; with a non-case-preserving policy (`SnakeCaseLower`,
> `KebabCaseLower`) a body may name a property in either its policy spelling or its EDM/CLR
> spelling. Before #536 `PATCH` accepted only the latter and silently discarded the former.

The OpenAPI/Swagger companion packages (`OhData.AspNetCore.OpenApi`, `.NSwag`, `.Swashbuckle`)
follow this same policy: generated schema property names match the wire casing exactly — PascalCase
by default, camelCase when you opt in — instead of the host `HttpJsonOptions` casing the underlying
generators would otherwise use. A `[JsonPropertyName]` rename still wins, in the schema and on the
wire alike. So the generated document (and any client code generated from it) agrees with what
responses actually emit.

## Handler paths

### `GetAll` - simple in-memory path

```csharp
GetAll = (ct) => OhDataResult.SuccessTask<IEnumerable<Product>>(myList);
```

Returns all items. The framework does **not** apply `$filter` or `$orderby` to the returned collection - and it does not silently ignore them either. If the client sends either of these, the request is rejected with `400 Bad Request` (`UnsupportedQueryOption`), regardless of the capability flags - `GetAll` has no `ApplyTo`/`IQueryable` pipeline to push them down to.

`$top` and `$skip`, by contrast, **are** applied on this path: they are pure post-materialization `Skip()`/`Take()` calls against the array `GetAll` (or `Search`, when `$search` is also present) returned - the same class of operation as the already-live `$select`/`$expand`/`$count` below. `$select`, `$expand`, `$count`, `$top`, `$skip`, and `$search` (when a `Search` handler is configured) are all honored on this path - `$select`/`$expand`/`$count` are each gated by its capability flag (`SelectEnabled`/`ExpandEnabled`/`CountEnabled`), exactly like the `GetQueryable` path: sending a disabled option returns `400` (`UnsupportedQueryOption`). `$top`/`$skip` need no flag - they are always live, mirroring `GetQueryable`.

`MaxTop` caps an **explicit** `$top` on this path exactly like it does on `GetQueryable`: a `$top` value greater than `MaxTop` returns `400 Bad Request` (`InvalidQueryOption`). As of #201, an **omitted** `$top` is also capped to `MaxTop` (or a smaller `Prefer: maxpagesize`), and the response carries a `@odata.nextLink` for the remainder - so `GetAll` is safe-by-default and can no longer be coerced into returning an unbounded result set. This became possible because `GetAll` re-enumerates its source on each request and applies `$skip` itself, so an offset `$skip` link is a valid continuation story - the framework only ever emits a link it also honors (note it is `$skip`, not the opaque `$skiptoken` `GetQueryable` emits, nor the framework-private token the Priority-1 path uses). **To opt out** - return the full set in one response, however large - set `MaxTop = null` on the profile; an omitted `$top` then applies no cap and emits no `@odata.nextLink`. `Preference-Applied` echoes the honored page size, clamped so `maxpagesize` can never lift the `MaxTop` ceiling.

`@odata.count` (`$count=true`) reflects the **pre-paging** total on this path too, per §11.2.5.5 - it is computed from the full materialized array before `$skip`/`$top` are applied, not from the length of the returned page.

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

On this path the profile — not the framework — owns query application, including `$skip`. When you return a lazily-translated `IQueryable` (e.g. an EF Core queryable) and rely on the framework's `MaxTop`/`Prefer: maxpagesize` cap plus its `@odata.nextLink` continuation, **you must give that queryable a stable, total order** — a terminal `OrderBy` (typically the entity key), or by applying the client's `$orderby`. Without one, the emitted `LIMIT`/`OFFSET` runs over an undefined row order, so a row can appear on two pages or be skipped between them, and EF Core logs warning `10102` ("row limiting operation without OrderBy"). The framework does not inject an order for you here: it can't do so safely once you've applied your own `$skip` (ordering a sliced subset is wrong), and a stable key column is your decision, not the framework's. This matters on this path even if you ignore the incoming options entirely, because the framework applies its **own** continuation offset on top of whatever you return (#360) - an unordered source will still page incoherently. (The `GetQueryable` path is different — there the framework owns the whole pipeline and orders paged results by the entity key automatically.)

> **Note:** `GetODataQueryable` is available on `ODataEntitySetProfile<TKey, TModel>`, not the base `EntitySetProfile<TKey, TModel>`. It requires the `OhData.AspNetCore` package. An `IQueryable<TModel>` is implicitly convertible to `ODataQueryResult<TModel>` for backward compatibility with handlers that return a bare queryable.

### `GetQueryable` - IQueryable with pushdown (recommended for databases)

```csharp
GetQueryable = _ => OhDataResult.SuccessTask<IQueryable<Product>>(db.Products);
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

        GetQueryable = _ => OhDataResult.SuccessTask<IQueryable<Product>>(db.Products);
    }
}
```

Any disabled capability returns `400 Bad Request` (`UnsupportedQueryOption`, with a message naming the option and the flag that enables it) if the client sends that query option. **All capability flags default to `false`** (inheriting from `EntitySetDefaults`) - an entity set accepts no query options until you opt in.

The single-entity route `GET /Products(1)` honors the same gates for the options it supports: `$select` requires `SelectEnabled` and `$expand` requires `ExpandEnabled`. When `ExpandEnabled` is on, `$expand` on the single-entity route inlines the requested navigation properties using the same navigation-route handlers (batch handlers included) as the collection route.

### Advanced: independent contexts with `IDbContextFactory`

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

> **⚠ It does not restrict dynamic (open-type) properties either.** The allowlist is enforced
> through the EDM's model-bound `NotFilterable` annotation, and a dynamic property is not in the
> EDM - so there is nothing to annotate and nothing to enforce. On a model with an
> [open complex type](open-types.md), `$filter` over a dynamic key is not gated by
> `FilterProperties` at all. `Microsoft.AspNetCore.OData` behaves the same way. **If a value must
> not be filterable, do not put it in a dynamic bag.** See
> [Dynamic keys are outside the query-option property allowlists](open-types.md#dynamic-keys-outside-allowlists)
> for the measured behaviour (which also varies by LINQ provider) and issue
> [#401](https://github.com/en-gen/OhData/issues/401).

<a name="allowlists-are-per-clr-type"></a>
### Allowlists are scoped per CLR model type, not per entity set

`FilterProperties`, `OrderByProperties`, `SelectProperties` and `ExpandProperties` are declared on
a profile, but they are enforced through OData's **model-bound query settings**, which
`Microsoft.AspNetCore.OData` resolves off the EDM *type* - never off the entity set. Two profiles
over the same CLR model type in one registration therefore write the same settings, and the result
would be their **union**: each entity set would accept properties the other allows, with responses
indistinguishable from the correctly-gated case.

Per-entity-set model-bound settings do not exist to scope this down. In
`Microsoft.OData.ModelBuilder` the fluent `Filter`/`OrderBy`/`Select`/`Expand`/`Count`/`Page` API is
declared only on `StructuralTypeConfiguration<T>` and `PropertyConfiguration`;
`EntitySetConfiguration` has no such surface, and the capability-vocabulary annotations that *can*
sit on an entity set are metadata-only - the query validators never read them.

So OhData refuses the ambiguous configuration outright. `MapOhData()` throws
`InvalidOperationException` when two profiles expose the same model type and declare **different**
allowlists for the same query option, naming both entity sets and both declarations. Multi-set-
per-type registrations remain fully supported - the check fires only on a genuine divergence:

- Two profiles that both leave an allowlist unset agree (both permissive).
- A profile whose capability flag (`FilterEnabled` etc.) is off contributes nothing to the shared
  settings and agrees with anything; its own requests are already refused by the flag gate.
- An `AdvancedConfigure` override owns the EDM outright and is not compared.
- Separate registrations each build their own model and are never compared to one another.

If two entity sets genuinely need different allowlists over the same data, give them distinct CLR
model types. See issue [#458](https://github.com/en-gen/OhData/issues/458).

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
`PropertyAccessEnabled`/`AllowDeepWrites` do:

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

> **⚠ It does not restrict dynamic (open-type) properties either** - same reason as
> `FilterProperties`: the `NotSortable` annotation has nothing to attach to. See
> [Dynamic keys are outside the query-option property allowlists](open-types.md#dynamic-keys-outside-allowlists).

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

The two paths differ only in the shape of the continuation link. `GetQueryable` emits an opaque `$skiptoken` (which the framework decodes back to a `$skip` itself).

**Priority-1 carries its continuation offset in a framework-private custom query option (`ohdata-skiptoken`), which the framework applies itself (#360).** It is not `$skiptoken` - `ODataQueryOptions.ApplyTo` throws on a `$skiptoken` it has no handler for, which would break every profile that calls it. And it is deliberately no longer `$skip`: the framework emitted a `$skip` link but never applied it, leaving the skip entirely to the profile's own `ApplyTo`, so a profile that did **not** re-apply the incoming options served the identical first page forever and a client walking `@odata.nextLink` never terminated. The framework now applies its own offset on top of whatever the profile returns, so the continuation is correct whether or not the profile honors the standard options; the client's own `$skip` rides along verbatim on every hop and is re-applied by the profile (or not) identically each time, so there is no double-skip either way. Treat `@odata.nextLink` as opaque (§11.2.5.7) - the option name is an implementation detail. A profile that sets `ODataQueryResult.NextLink` itself is trusted to be paging on its own terms, and the framework does not add or override the cap in that case.

**A page that is exactly `MaxTop`/`maxpagesize` long is not assumed to have more behind it (#360).** On `GetQueryable` and Priority-1 the framework fetches one row *past* the page and emits `@odata.nextLink` only if that probe row actually came back, then discards it. This costs no extra round-trip (it is the same single query, one row wider) and leaves `@odata.count` untouched - that is computed separately, pre-paging. Previously a collection whose row count was an exact multiple of the page size ended every walk with one spurious empty page. `GetAll` never had the problem: it already materializes its source and compares against the pre-paging total.

**A collection-returning bound FUNCTION is bounded the same way (#357).** A bound (or entity-bound)
function whose result is a collection of the entity set's own type used to bypass `MaxTop`, the
client's `$top`/`$skip` and server-driven paging entirely - so the ceiling enforced on every route
above was fully bypassable through any such operation, and a `$top` sent against one was neither
applied nor rejected. It now follows the `GetAll` rules exactly (in-memory `Skip()`/`Take()` over the
materialized result, `$skip` continuation, `MaxTop` capping an explicit `$top` with the same message,
`Prefer: maxpagesize` honoured, `MaxTop = null` to opt out). **Breaking** for a function returning
more than `MaxTop` entities. Bound and unbound **actions** are excluded - see
[Bound operations](bound-operations.md#a-collection-returning-function-is-paged-like-any-other-collection-357).
No other system query option is applied to an operation result.

**`GetAll` now mirrors the "omitted `$top`" behavior above (#201).** An omitted `$top` is capped to `MaxTop` (or a smaller `Prefer: maxpagesize`) with a `@odata.nextLink` for the remainder, so this path is safe-by-default like the others. The one difference from `GetQueryable` is the continuation shape: `GetAll` emits a `$skip` link (which it re-applies against its re-enumerated source) rather than the opaque `$skiptoken`. Because it has the pre-paging total in hand, it emits a link only when rows actually remain. Set `MaxTop = null` on the profile to opt out and return the full set in one response, however large - see the `GetAll` section above.

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

> **§11.2.9 decides what this segment implements, and it splits the options in two.** Verbatim:
>
> > "On success, the response body MUST contain the count of items matching the request after
> > applying any `$filter` or `$search` system query options … **The returned count MUST NOT be
> > affected by `$top`, `$skip`, `$orderby`, or `$expand`.**"
>
> | Option | On `GET /{Set}/$count` | Why |
> |---|---|---|
> | `$filter` | **Applied** | §11.2.9: the count is taken after applying it |
> | `$top` `$skip` `$orderby` `$expand` | **Accepted and ignored** | §11.2.9 names these four and says the count MUST NOT be affected by them |
> | `$select` | **Accepted and ignored** | not named by that sentence, but it changes an item's *shape*, never its membership, and the body is a bare scalar |
> | `$format` | **Accepted and ignored** | §11.2.9 disallows content negotiation here; the body is `text/plain` regardless |
> | `$search` | **`501`** | §11.2.9 requires the count to be taken *after applying* it, and this route has no `$search` leg — ignoring it would return a **wrong number** under a `200` |
> | `$apply` `$compute` `$count`, any unrecognized `$`-name | **`501`** | outside the clause, and implemented nowhere here |
>
> **`$search` is `501` and that is #353 (`GET /odata/Products/$count?$search=alpha`).** Until 1.7.0
> it returned the **unfiltered** total under a `200`, which §11.2.9 forbids and which no client
> could detect. Ask for the searched count inline instead - `GET /odata/Products?$search=alpha&$count=true`
> and read `@odata.count`, which does honour it.
>
> **The four §11.2.9 names, and `$select`, are accepted and ignored — the same behaviour as
> 1.0.0 through 1.6.0.** Ignoring them is what the clause specifies, so under Minimal item 7's
> *"either follow the specification or return 501 … for any unsupported functionality"* it is the
> follow arm; a `501` there would claim non-implementation of something this route has done
> correctly since 1.0.0. This also matches `Microsoft.AspNetCore.OData`, whose
> `ODataQueryOptions.ApplyTo` returns early on `Request.IsCountRequest()` before reaching the
> `$orderby`/`$skip`/`$top` block, and it is what `Microsoft.OData.Client` requires: it translates
> `LongCount()` by appending `/$count` to the query it has **already** built and strips nothing, so
> `q.OrderBy(…).LongCount()`, `q.Take(n).LongCount()` and `q.Skip(n).LongCount()` all send the
> option along.
>
> `Accept: application/xml` on this segment still answers `406` — §11.2.9 forbids the *client* to
> negotiate, which is not a licence for the server to ship a media type the client refused.
>
> See [Unsupported system query options are rejected](#unsupported-system-query-options-are-rejected-359-380-353).

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

> **⚠ It does not restrict dynamic (open-type) properties - and on an open type it can be
> circumvented for *declared* ones too.** The allowlist is enforced through the EDM's model-bound
> `NotSelectable` annotation, which a dynamic property has no place to carry. Worse: `$select` over
> a dynamic key silently degrades to selecting the **whole containing complex value**, so on a
> model with an [open complex type](open-types.md), `$select=Meta/<anyUndeclaredName>` returns the
> entire `Meta` value - including declared sub-properties the allowlist denies. Measured, and
> provider-independent. See
> [Dynamic keys are outside the query-option property allowlists](open-types.md#dynamic-keys-outside-allowlists)
> and issue [#401](https://github.com/en-gen/OhData/issues/401).

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
            get: async (orderId, ct) => await db.Customers.FindAsync([orderId], ct));

        GetQueryable = _ => OhDataResult.SuccessTask<IQueryable<Order>>(db.Orders);
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

Pushdown is **on by default** (`EntitySetDefaults.ExpandPushdownEnabled`, per-profile `ExpandPushdownEnabled` override). It engages **only** on the EF Core-backed `GetQueryable` path, and for a navigation whose related type is either free of a back-reference cycle or — as of #323 — itself member-init-projectable: a projectable element is always materialized through a fresh POCO (never the bare EF-tracked entity), which forecloses a serialization cycle structurally regardless of what navigations the related type declares, so a standard bidirectional relationship (e.g. `Author.Books` / `Book.Author`) now pushes down and JOINs like any other navigation. Only a related type that is BOTH cyclic AND not member-init-projectable (no public parameterless constructor, or a complex/unsettable structural member) still keeps today's conservative defer. Whenever pushdown is *structurally* ineligible for a request — a non-EF provider, a cyclic *and* non-projectable navigation, or a deferred nested option (see the table below) — it **falls back silently**: the delegate-less navigation simply stays EDM-only for that request (as it was before this feature), and the reason is `Debug`-logged. Falling back does not *itself* surface a `500`, and — as of #325/#326 (Option B, the `SerializeBounded` walker) — it no longer risks one either: whatever the handler's own query already produced for a deferred navigation, tracked/cyclic object graph included, now serializes through the SAME clause-bounded walker the rest of the response body does, so a reference cycle among those tracked entities is structurally unreachable regardless of how far pushdown deferred. **"Stays EDM-only" means the framework doesn't itself load the navigation — it does not mean the navigation is guaranteed to come back empty.** No pushdown does not automatically mean nulling either: whatever the handler's `GetQueryable`/`GetAll` query already produced for that navigation (a non-EF `IQueryable`'s own eager load, an EF `Include` the handler wrote itself, or a hand-built object graph) passes through and serializes as-is **whenever the framework composes no projection of its own** — `$expand`'d navigations are never stripped by `OmitUnexpandedNavigations` regardless of how they got their data. Two `GetQueryable`-path exceptions do force it empty anyway, because a member-init projection structurally omits any navigation it doesn't bind: (1) **another navigation in the same `$expand` pushed down** — `TryApplySelectProjection` then binds only the structural properties and the navigation(s) that engaged pushdown, so a sibling that stayed EDM-only is never bound and comes back empty even if the handler had populated it; and (2) **`$select` pushdown is eligible for the request** (`$select` + `$expand`, `SelectPushdownEnabled`, on by default) — `ApplySelectPushdown` is not gated on an EF Core provider, so `?$select=Name&$expand=Children` against e.g. a `List.AsQueryable()` source still composes a member-init of only the selected + key properties, structurally omitting `Children`. Outside those two shapes, only a handler that left the navigation genuinely unpopulated will actually report it empty.

**Wire change (#323, accepted by design):** every pushed-down expand — including a leaf (no nested `$expand` of its own) — is now materialized through the same member-init projection `$levels` and intermediate multi-level expands already used. A public CLR property on the related type that is **not** an EDM structural property (e.g. a `[NotMapped]` field, a get-only computed property not derived from bound scalars, or a member excluded from the model) is therefore no longer materialized on a leaf-expanded entity — it comes back as its type's default value, exactly as it already did at intermediate levels. Leaves are now *consistent* with intermediate levels rather than being a special case. A computed get-only property whose getter derives purely from bound scalar properties still serializes correctly (the scalar inputs are bound; the getter still runs against the projected POCO).

One shape keeps the pre-#323 behavior entirely: a related type where an EDM **structural** property itself is get-only (no public setter) is not `IsMemberInitProjectable` at all — a public setter on every structural property is a hard requirement, not just a nice-to-have — so that type falls back to the bare (untransformed) leaf, exactly as it always did. No wire change happens there (the get-only structural property still serializes, since nothing is projected), but the type also doesn't get the #323 pushdown/cycle fix — a back-reference on that type still defers the branch off pushdown under the pre-#323 rules.

**A root model that can't support a member-init `$select` projection at all — no public parameterless constructor, an unknowable ETag selector, or a complex/unsettable structural member — is a separate case, and is no longer silently dropped to EDM-only (#305).** The engaged, delegate-less `$expand` navigations are instead served through EF Core's own `Include` (resolved via reflection — this package carries no compile-time EF Core dependency), bounded by `MaxExpandTop` exactly like the projection path, and a nested `$count`/`$select`/`$top`/`$skip` is shaped afterward exactly as it is on the projection path. A nested `$filter`/`$orderby` can't ride a plain `Include` (it's a SQL-only capability of the member-init path), so it still fails loud with `400` rather than silently degrading, and so does a nested `$expand`/`$levels` under this fallback (out of scope for #305) — both error messages point at making the root model projection-eligible, or writing an expand delegate for that navigation, instead. **A leaf expand whose related type has a back-reference (to the root model, to a sibling leaf, or to itself) is now served rather than rejected** (#325/#326, Option B): `Include` populates *tracked* entities, and EF Core's own relationship fixup can wire the back-reference up, but the response now serializes through the same clause-bounded `SerializeBounded` walker every other path uses, which never hands an un-expanded navigation to `System.Text.Json` at all — a reference cycle among the tracked entities is structurally unreachable regardless of which two instances it closes between. (#323 originally introduced a `400` here for the root-back-reference case specifically — "Change C" — before #326 identified two further cycle classes that guard missed; #325/#326 removed the guard entirely rather than widen it, since the underlying request can now be answered correctly.) The one residual gap: a cycle closed by an entity-typed CLR property that is **not** an EDM navigation (excluded from the EDM model entirely) is outside what `SerializeBounded` bounds, and still rethrows as a generic `500` — by that point the query itself already succeeded, so this is the one case where fail-loud means an actual server error, not a `400`.

**A translation failure is different, and fails loud (FAIL LOUD, post-#298/#300 review).** If a nested `$filter`/`$orderby` cannot be bound, or the composed query cannot be translated by the provider even though everything looked eligible, the request now returns `400` (`InvalidQueryOption`) instead of silently degrading to EDM-only under a `200` — before this fix, a translation failure could mean the affected navigation (or, for the specific shapes #298/#300 identified, the whole parent collection) came back wrong or empty with no indication anything failed. Simplify the nested option combination, or write an expand **delegate** for that navigation to take full control of its query shape. See the wire-change note above for what remains possible outside pushdown.

A delegate-backed navigation is **never** affected by any of this — it always expands through its delegate. Set `ExpandPushdownEnabled = false` (per profile or in `WithDefaults`) to keep every delegate-less navigation unexpandable.

`$expand` pushdown composes with `$select` pushdown: `?$select=name&$expand=Lines` prunes the parent's column list *and* JOINs the lines in the same single query. The two capabilities are **independent** — disabling `SelectPushdownEnabled` does not disable `$expand` pushdown, and an `$expand` push never column-prunes the parent on its own.

#### Nested options on a pushed `$expand`

A pushed (delegate-less) `$expand` honors the nested options of the expanded collection. `$filter`, `$orderby`, and `$top`/`$skip` are pushed down to SQL as a **filtered / ordered / paged `Include`** (translated by Microsoft's own OData `FilterBinder`/`OrderByBinder`, so the semantics match a top-level `$filter`/`$orderby`), producing a single JOIN'd query — no per-parent N+1. `$count` and `$select` are then applied to the serialized result (in whatever naming policy is configured — PascalCase by default).

| Nested option (on a delegate-less pushed nav) | Supported | How |
|---|---|---|
| `$select` — `Children($select=name)` | ✅ | JSON projection of the expanded elements (configured naming policy preserved) |
| `$filter` — `Children($filter=active eq true)` | ✅ | filtered `Include` (SQL `WHERE` in the JOIN) |
| `$orderby` — `Children($orderby=name desc)` | ✅ | ordered `Include` (SQL `ORDER BY` in the JOIN) |
| `$top` / `$skip` — `Children($orderby=name;$top=5)` | ✅ | paged `Include` (SQL `ROW_NUMBER` window); `$top` is capped by [`MaxExpandTop`](#complexity-limits-202) when that is set (it defaults to no ceiling) |
| `$count` — `Children($count=true)` | ✅ | inline `Children@odata.count` = full filtered count (paging is applied after counting, per §11.2.5.5); bounded by [`MaxExpandTop`](#complexity-limits-202) when that is set (it defaults to no ceiling) |
| **nested `$expand`** — `Children($expand=Grandkids)` | ✅ | multi-level pushdown: folded into the same query as an `Include`→`ThenInclude` JOIN when every level is delegate-less (see [Multi-level `$expand`](#multi-level-expand-and-levels-206) below) |
| `$levels` — `Children($levels=2)` / `Children($levels=max)` | ✅ | recursive self-referential expand, bounded by `MaxExpansionDepth`; may carry `$filter`/`$orderby`/`$skip`/`$top`/`$count`/`$select`, applied at **every** level (see below) |
| `$search` / `$compute` / `$apply` | ❌ (deferred) | not implemented on the pushdown path |

A deferred nested option is not an error: the request still returns `200`, but the delegate-less navigation that carried it stays EDM-only for that request — the framework doesn't load it via pushdown, though whatever the handler's own query already populated (or didn't) is what serializes (see the caveat above). Nested options on a **delegate-backed** navigation follow the delegate path and are subject to that path's own support (see [navigation-routing.md](navigation-routing.md)); they never engage pushdown. One option is not merely unsupported there but actively rejected: a nested `$top`/`$skip` against a delegate-backed navigation returns `400` (`InvalidQueryOption`) rather than being forwarded to (or silently dropped by) the delegate (#294) — the delegate's `Handler`/`BatchHandler` returns its full answer for a given parent key and nothing downstream re-windows it, so honoring the option would mean quietly serving every related row under an unsuspicious `200`. This applies to any delegate-backed navigation under `$expand`, self-referential or not (see `SelfReferentialNavMaxTopTests.cs` and `BatchExpandTests.cs`).

<a id="multi-level-expand-and-levels-206"></a>
#### Multi-level `$expand` and `$levels` (#206)

A nested `$expand` is pushed **recursively**: `?$expand=Books($expand=Chapters($expand=Pages))` folds all three levels into one JOIN'd query (EF Core `Include`→`ThenInclude`), applying each level's own nested `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select`. A branch is pushed only when it is **delegate-less at every level**; the moment a level's navigation carries a delegate (or is cyclic / a non-projectable type), that whole branch is deferred off pushdown and resolves through the existing path — a **delegate-backed navigation is never EF-included at any depth**, so the delegate is never bypassed. A delegate-backed navigation reached directly from the root (or under delegate-backed ancestors) still expands through its delegate exactly as before; a delegate navigation nested *beneath* a delegate-less parent is **never JOIN-loaded and its delegate is never invoked** — but, exactly as for any deferral, that is not a guarantee of emptiness: if the parent handler's own query populated it (an `Include`/`ThenInclude` it wrote, or a hand-built graph), it serializes as-is.

`$levels=N` recursively expands a **self-referential** navigation (a tree/hierarchy) `N` levels deep — `?$expand=Children($levels=2)` — as a bounded, cycle-free projection (each level is a fresh POCO; the deepest loaded level terminates the recursion). `$levels=max` resolves to the configured `MaxExpansionDepth`. Both are capped at `MaxExpansionDepth`: a `$levels` (or a nested `$expand`) that resolves deeper is rejected with `400` before any handler runs (see [Complexity limits](#complexity-limits-202)).

**`$levels` off the pushdown path (#466).** The recursion above is the EF-pushed one. On a
**delegate-less** navigation served from the handler's own graph — `GetAll`, Priority-1, a non-EF
`IQueryable`, `GET /{Set}({key})`, or a branch the pushdown declined — `$levels=N` now serves the
same `N` levels the explicit nested spelling does, read straight off that graph: `?$expand=Children($levels=2)`
and `?$expand=Children($expand=Children)` return byte-identical responses. Until #466 the first served
**one** level and the second served two, silently, because the levels budget was only ever seeded for a
navigation the pushdown had recursed. On a **delegate-backed** navigation a `$levels` resolving to more
than one level is rejected with `400` (`InvalidQueryOption`) instead: the delegate loads a single level
and there is no settled rule for which delegate governs level 2 on that substrate (the pushed path
deliberately stays on the URL-named set all the way down — #318, frozen — while Model B resolves depth ≥ 2
from the exact-EDM-type union — #293, frozen), so the framework says so rather than truncating. Spell
the depth out with nested `$expand`, or declare the navigation delegate-less. `$levels=1` is unaffected
anywhere: it restates a bare `$expand` and is served as one.

A `$levels` expand may **also carry `$filter`, `$orderby`, `$skip`, `$top`, `$count`, and `$select`** (#254). Those options apply **at every level of the recursion**, not just the first — the semantics Microsoft's own OData stack implements (`$levels=N` is rewritten into `N` nested expands each carrying the same options) and the reading the spec's equivalence example implies. So `?$expand=Children($levels=2;$filter=active eq true)` prunes inactive nodes at both levels (an inactive node's whole subtree disappears with it), `($levels=2;$count=true)` emits `Children@odata.count` on every level, and `($levels=2;$select=name)` keeps the self-navigation itself at every level while pruning the other properties.

One caveat, now **fixed** (#296/#294, PR #321): a nested `$top` on a self-referential navigation used to be rejected by the underlying OData validator before OhData's pushdown code ever ran, because the navigation's target type is necessarily its own entity set — the same thing that makes `$levels` legal on it at all — so its model-bound `MaxTop` always defaulted to `0`. `OhDataBuilder.MarkNavigationTargetTypesFullyQueryable` now clears that model-bound `MaxTop` for a root-and-nav-target ("shared"/self-referential) type exactly as it already did for a pure nav-target-only type (#296; the fix generalizes to any non-self-referential "shared type" too — a type that is both a root entity set and someone else's navigation target, see `SharedNavTargetTypePushdownTests.cs`) — so that pre-emptive `400` no longer fires. What happens instead depends on whether the navigation is delegate-backed:
- On a **delegate-less** navigation, the nested `$top` now genuinely reaches OhData, and — like `$skip` — is applied in the JSON pass (`ApplyNestedWindow`/`ShapeLevelsInJson`) rather than pushed to SQL, for the same `APPLY`/`LATERAL`-shaped translation problem the `$count` caveat below describes: `?$expand=Children($levels=2;$top=1)` windows to one child **at every level** of the recursion.
- On a **delegate-backed** navigation, a nested `$top`/`$skip` is instead rejected with a *different* `400` (`InvalidQueryOption`) — OhData's own check (#294), not the old model-bound one — since the delegate returns its full per-parent answer and nothing downstream re-windows it (see the caveat above, and `SelfReferentialNavMaxTopTests.cs`).

That rejection is resolved from the navigation's **Model B treatment** — serve-raw, run-the-delegate, or blank, decided from the declarations of every entity set exposing the type at that level ([#293](https://github.com/en-gen/OhData/issues/293)) — not from which navigation the expansion walker happens to reach, so it does not depend on how the navigation was arrived at (#320):

- It fires on a **blanked** navigation too — one whose candidate entity sets disagree about whether it is delegate-backed — with a message naming that disagreement instead of a delegate. A blanked navigation is emptied outright, so no window can be applied to it either.
- It fires on a navigation reached **only through a delegate-less parent's already-materialized graph**, at any depth. Before #320 that case returned `200` with every related row and the option dropped without a trace, because the walker's serve-raw branch never descends into it.
- It does **not** fire on a serve-raw navigation, which is exactly the case where the window *is* applied (a branch is SQL-pushdown-windowed only when every level of it is serve-raw).

One residual, deliberately unchanged: a serve-raw navigation whose branch was never pushed down at all — an in-memory `GetAll` source, a non-EF `IQueryable`, or a branch deferred off pushdown for a structural reason — still ignores its nested `$top`/`$skip` silently. Rejecting that would make the answer depend on whether pushdown happened to engage, which is an internal optimisation decision invisible to the client, and would turn requests that are honoured today into `400`s.

A plain (non-`$levels`) `$expand=Children($top=…)` against a **delegate-less self-referential** navigation used to be a separate, orthogonal limitation: the plain member-init projection for a self-reference was treated as genuinely cyclic, so it never engaged pushdown regardless of `$top`, and the `$top` silently went unapplied. **Resolved by #323**: a self-referential related type is still projectable (a public parameterless constructor plus settable scalar structural properties is all `IsMemberInitProjectable` requires — cyclicity is orthogonal to that), so it now clears the narrowed back-reference guard and genuinely engages pushdown even without `$levels`. `?$expand=Children($top=1)` now actually windows to one child (see `LevelsWithOptionsPushdownSqliteTests`, T19). The projected elements are leaves — the self-navigation property on each is not itself bound (consistent with the leaf-projection wire change above), so the result stays finite without needing `$levels`' explicit termination. `$skip` never carried a model-bound ceiling and always reached OhData's code even before #294/#296. `?$expand=Children($levels=2;$orderby=name desc;$skip=1)` windows deterministically at every level; the other options are unaffected.

The one combination still **deferred** off pushdown is a `$levels` expand carrying its **own nested `$expand`** (`Children($levels=2;$expand=Tags)`): depth accounting between the `$levels` budget and the nested branch's own remaining depth is ambiguous against `MaxExpansionDepth`. As with any deferral the request still returns `200`; the navigation just stays EDM-only for that request — not guaranteed empty, per the caveat above.

The ceiling is advertised in `$metadata` as the `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` annotation on each entity set, so a client can discover it before issuing a request.

**Caveats.**

- **Nested options are not gated by the parent profile's property allowlists.** `FilterProperties`/`OrderByProperties`/`SelectProperties` restrict the *root* entity set only; a navigation-target type has no allowlist surface of its own and is treated as fully queryable (this is the same design decision that lets nav-path `$filter` work — see `MarkNavigationTargetTypesFullyQueryable`). So `$expand=Children($filter=…)`/`($orderby=…)`/`($select=…)` may reference any column of the child type regardless of what the parent restricted. Model your navigation targets accordingly (e.g. don't expose a sensitive column on a type reachable via a delegate-less navigation you `$expand`), or write an expand **delegate** for that navigation (which opts it out of pushdown and lets you enforce your own shaping).
- **`$count` on a pushed expand no longer discards the nested `$top`/`$skip` SQL bound (#334); whether `MaxExpandTop` bounds the fetch at all still depends on the shape — and, since #304, the same shape question governs a plain nested `$top`/`$skip` (no `$count`) too.** At a **projection leaf** — a level with no nested `$expand` of its own — a `$count` that comes **with** a nested `$top`/`$skip` now bounds the SQL fetch by that **window**, and takes `Nav@odata.count` separately as a correlated `COUNT(*)` scalar subquery over the filtered-but-unwindowed collection, in the same single query. (That is the split `Microsoft.AspNetCore.OData` has always made between `CreateTotalCountExpression` and the projected collection; a correlated scalar aggregate is *not* the `APPLY`/`LATERAL` shape a windowed collection projected out of a windowed collection needs, so it translates on SQLite too — pinned by `NestedCountTopSqlBoundTests`.) Before #334 the count *was* the materialized array's length, so `$count=true` had to suppress the client's `$top` and compose the ceiling bound instead: `?$expand=Children($top=10;$count=true)` fetched `MaxExpandTop + 1` rows to return 10 — and with the ceiling unset (the shipping default since #313) it composed **no bound at all**. A `$count` with **no** window is unchanged: there is nothing to bound, so it still materializes the filtered collection (bounded by `Take(MaxExpandTop + 1)` when a ceiling is set) and counts it. §11.2.4.2 is preserved throughout — `Nav@odata.count` is the count of the **full filtered** collection, never the returned page — and the ceiling breach is still a `400` (`InvalidQueryOption`) rather than a truncated count; it is simply detected from the exact count now instead of from an over-fetched probe row, so a breach is caught even when only the requested window was fetched. At a level that **also** carries its own nested `$expand` (a level with children), anywhere inside a `$levels` recursion, or on `GET /{Set}({key})`, none of this applies: the SQL bound is **not** composed for `$count` **or** for a plain `$top`/`$skip` (#304) — windowing a collection *and* projecting a further collection out of it in the same query requires SQL `APPLY`/`LATERAL`, which not every provider (SQLite among them) translates — so the window (and the count) is computed **after an unbounded materialization** in the JSON pass, and the request is rejected with the same `400` if the collection exceeds `MaxExpandTop`. Before #304, a nested `$top`/`$skip` at a level with its own nested `$expand` failed loud with `400` outright (e.g. `?$expand=Books($top=1;$expand=Chapters)`); it is now windowed correctly instead, the same JSON-pass trade `$levels` and `$count` already made — and #316 closed the matching ceiling gap on the `$levels` JSON-windowing path. The *correctness* of the ceiling is enforced on every shape (never a truncated count, never an untranslatable-query failure); [#299](https://github.com/en-gen/OhData/issues/299) tracks tightening the unbounded-materialize-then-`400` *cost* on the shapes #334 does not reach, which stays open. Narrow the collection with a nested `$filter`, or raise/remove `MaxExpandTop`.
- **Once `MaxExpandTop` is set, it bounds every collection `$expand` level, the bare one included (#313).** `MaxExpandTop` is unset by default, so none of this applies until you set it — see the table below. Before #313 the ceiling covered an *explicit* nested `$top` and the nested-`$count` materialization only, and `$expand=Nav` with no nested `$count`/`$top` — the most common `$expand` shape there is — composed no SQL `Take` and got no post-hoc size check even with a ceiling configured, so a 5,000-row related collection under a `MaxExpandTop` of 1000 returned all 5,000 rows. It now returns `400` (`InvalidQueryOption`). The rule is: **a collection expand level carrying neither a nested `$count` nor an explicit nested `$top` is bounded by `MaxExpandTop`, whatever else it carries.** That is deliberately broad, and wider than "bare" suggests — `($select=…)`, `($orderby=…)`, `($filter=…)` and `($skip=N)` are all in scope, because none of them bounds the collection either. At a projection leaf the bound is pushed into SQL as `Take(MaxExpandTop + 1)` (so the over-cap case is detected without transferring the whole collection); at a level with its own nested `$expand`, and at every level of a `$levels` recursion, it is a post-materialization check in the JSON pass — the same `APPLY`/`LATERAL` trade the `$count` caveat above describes; and on a **raw-served** expansion — `GetAll`, Priority-1, a non-EF `IQueryable`, every level of `GET /{Set}({key})`, a branch the pushdown declined, and every level below any of those — it is a post-materialization check over the serialized response, always a `400` and never a link (#418/#463/#464). Until #463/#464 that last group was not checked at all beyond depth 1 of the single-entity read, which made the sentence this bullet opens with false on three of the five read paths. **`$levels=N` is checked at each level independently**, so `Nav($levels=1)` behaves exactly like the bare `$expand=Nav` it restates, and a deeper level that breaches is rejected even when the levels above it are under the cap. An **explicit** nested `$top` still wins: it is validated against the ceiling up front (`400` if larger than `MaxExpandTop`) and windows the collection itself, so no default bound is composed alongside it.
- **An over-cap collection is a `400` unless it is a *truly bare* `$expand` on a profile that opted in with `ExpandPagingEnabled` — then it is a page plus a `Nav@odata.nextLink` (#313).** Silently windowing an expanded collection without a link to continue from would be a worse spec violation than the cost of rejecting, so the framework never does it: over the ceiling a shape either rejects or links, at every commit. The full rule, the exact pageable set and the continuation's own surface are in [Nested server-driven paging](#nested-server-driven-paging-expandpagingenabled-313) below. For every shape that is not truly bare — and for every profile that did not opt in — the answer is unchanged: narrow the collection with a nested `$filter`, give the navigation an explicit nested `$top`, or raise/remove `MaxExpandTop`.
- **Nested paging without a nested `$orderby` is stabilized by the child's key.** When `$top`/`$skip` are pushed to SQL without a nested `$orderby`, the navigation element's single key is appended as a deterministic tiebreaker (mirroring the root path). A composite-keyed child type is left to the provider's order. Since #313 a **bare** collection expand carries a default bound too once `MaxExpandTop` is set, so the same tiebreaker applies to it — which makes `MaxExpandTop` govern the nested **wire order**, not only the status code: with a ceiling in force a nested collection comes back in child-key order; with `MaxExpandTop` unset (the default) no tiebreaker is composed at all and the order is whatever the provider yields. Setting the ceiling therefore opts *in* to the `400` **and** to deterministic nested ordering, together; there is no way to take one without the other.
- **The SQL bound at a projection leaf needs window functions.** `Take(MaxExpandTop + 1)` inside a collection projection is translated by EF Core as the standard top-N-per-group form, `ROW_NUMBER() OVER (PARTITION BY <fk> ORDER BY <key>)` — the same shape an explicit nested `$top` has always produced. Every provider OhData tests against (SQLite, EF Core InMemory) supports it, as do SQL Server, PostgreSQL, Oracle, MySQL 8.0+ and MariaDB 10.2+. The only relational providers that cannot translate it are MySQL before 8.0 (2018) and MariaDB before 10.2 (2017), both long past end-of-life and neither referenced or tested here. On such a provider, leave `MaxExpandTop` unset (the default) to keep the plain join.
- **Which rows the ceiling counts differs by shape, and it is worth knowing which.** At a projection **leaf** the bound is composed *after* any nested `$skip`, so what is measured against the ceiling is the **post-`$skip` remainder** — `Children($skip=4995)` over 5,000 rows at a ceiling of 1000 succeeds and returns 5. At a level with its own nested `$expand`, or inside a `$levels` recursion, no SQL window is composable and the check runs over the **fully materialized, pre-window** collection, so the same request is rejected. That asymmetry predates #313 (it is the #304 deferred-window shape, where `EnsureWithinExpandCeiling` necessarily runs before the JSON-pass window because there is nothing to window until the collection is materialized); #313 only makes it reachable from more requests. It goes away if and when [#299](https://github.com/en-gen/OhData/issues/299) removes the unbounded materialization, and not before.
- **Use `null` — not a very large number — to mean "no ceiling".** `MaxExpandTop = int.MaxValue` still counts as *set*, so every bound and every key tiebreaker is composed exactly as for a small value; the only difference is that the resulting check can never fire. You pay the `ROW_NUMBER()` window for a rejection that cannot happen. Unset (the default `null`) is the opt-out; a sentinel number is not.
- **With no ceiling set, a startup `Warning` names each exposed navigation (#313).** That is what replaced the arbitrary `1000` default. At `MapOhData()` OhData logs one warning per navigation that is collection-valued, delegate-less **on that profile's own declaration**, on a profile that has `GetQueryable`, `ExpandEnabled` **and** `ExpandPushdownEnabled`, when that profile's resolved `MaxExpandTop` is `null` — i.e. exactly the navigations a bare `?$expand=Nav` will materialize without bound. A *sibling* profile over the same EDM entity type declaring the navigation with a delegate does **not** silence it ([#421](https://github.com/en-gen/OhData/issues/421)): that sibling's delegate governs the sibling's own set, and this one still serves the navigation raw and unbounded. It names the entity set, the navigation, `MaxExpandTop` **and** `ExpandPagingEnabled` — in that order, because the second is inert without the first — and it deliberately prescribes no *number*: the framework cannot know how large your child collections are. Leaving it unset is a legitimate choice for a collection you know is small; the warning informs that choice rather than making it. Because `ExpandEnabled` is `false` by default, a registration that never opts into `$expand` gets no warning at all. Emitted once at startup, never per request.
- **`Prefer: odata.maxpagesize` narrows a nested page too, as of [#412](https://github.com/en-gen/OhData/issues/412).** §8.2.8.5 scopes the preference to *"each collection within the response"*, so it is not a root-only header. It applies to a nested collection **only where a `Nav@odata.nextLink` is going out** — a truly bare `$expand` on a profile that opted into `ExpandPagingEnabled` — because trimming a collection that gets no link is the silent truncation the framework never does. It is clamped **down** to `MaxExpandTop` and never up (a client preference cannot lift the server's ceiling, mirroring the root's clamp to `MaxTop`), and it never lowers the *ceiling*, so it can never turn a `200` into a `400`. The continuation route reads the same header, so a client that keeps sending it pages at its requested size all the way down; one that stops gets `MaxExpandTop`-sized pages from there on, with nothing skipped or repeated. `Preference-Applied` is unchanged and stays a single header: §8.2.8.5 makes the echo a `MAY` and defines its value as *"the maximum page size applied"* for the whole response, so no per-collection echo is added.

To also expose navigation as a standalone HTTP route (`GET /Orders(id)/Lines`), provide a handler to `HasMany` - see [navigation-routing.md](navigation-routing.md).

### Nested server-driven paging (`ExpandPagingEnabled`, #313)

A bare `?$expand=Nav` whose related collection exceeds `MaxExpandTop` can be served as its first
`MaxExpandTop` children plus a `Nav@odata.nextLink` continuation, instead of being rejected with
`400`. This is **off by default** and needs **two** settings, both of them yours to make.

#### The two knobs, and how they interact

| `MaxExpandTop` | `ExpandPagingEnabled` | What an over-large bare `$expand` does |
|---|---|---|
| unset (`null`, the default) | `false` (the default) | Returns the **whole** related collection. No bound, no `400`, no link. A startup `Warning` names each navigation in this state. |
| unset (`null`) | `true` | Identical to the row above — the flag is **inert without a ceiling**: no route is registered, no link is emitted, and there is no boundary at which a continuation could begin. |
| set to `N` | `false` | `400 InvalidQueryOption` — *"the related collection exceeds the maximum of N entities. Narrow it with a nested `$filter`."* |
| set to `N` | `true` | `200` with the first `N` children and a `Nav@odata.nextLink` — **but only for a truly bare `$expand`**. Every other over-ceiling shape keeps the `400` from the row above. |

`MaxExpandTop` is **also the page size**, for the first page and every continuation alike. There is
deliberately no second page-size knob: a number you have no basis on which to pick is the mistake
that removed `MaxExpandTop`'s own `1000` default, and a second one would need disambiguating at four
enforcement sites. The page size is **never** `MaxTop` — that is an independent knob with its own
default, and paging the continuation at it would serve `MaxExpandTop` rows on page 1 and `MaxTop`
rows on page 2, or (with `MaxTop = null`) an unbounded page 2.

`ExpandPagingEnabled` is a separate opt-in from the ceiling, not a refinement of it, because **a
continuation link is worse than a `400` for a client that does not read nested annotations** — that
client sees a complete-looking collection that has been silently truncated, with no error to notice.
Only turn it on if you know your clients follow `Nav@odata.nextLink`.

**OhData's own first-party [`OhData.Client`](client/index.md) does read this link**, through the
annotation-preserving terminal operations added in the same cycle (#417):
[`ToAnnotatedPageAsync`](client/terminal-operations.md#annotation-preserving-reads),
`ToAnnotatedAsyncEnumerable` and `GetAnnotatedAsync` return entries exposing
`NextLinkFor(x => x.Nav)` and `CountFor(x => x.Nav)`. So a nested continuation is fully consumable
end to end by the first-party client — server emission and client read are covered together by
`ExpandPagingSeamTests`.

The caveat that remains true is narrower, and it is about **which call you make**, not about which
client you use:

- The **ordinary** read path still drops annotations. `ToListAsync`, `ToPageAsync`, `ToAsyncEnumerable`
  and `GetAsync` bind the envelope only, so a paged nested collection looks complete through them.
  Preserving annotations costs a buffered body and a second read of it, which is why it is a separate
  method rather than a client-wide default. Reach for the `Annotated` counterpart whenever a query
  carries `$expand` against a server with this knob on.
- A **third-party** client that ignores unknown annotations sees a complete-looking collection that
  has been silently truncated, with no error to notice. That is the failure mode this opt-in exists to
  keep you from causing by accident, and it is unchanged.

#### The pageable set is exactly "a truly bare `$expand`"

**One shape pages: `$expand=Nav`, carrying no nested options at all.** The rule, stated once: *a
nested option list that normalizes to the identity transform is bare; anything else is not.* Only
two no-ops survive the parser, and both count as bare —

| Shape | Answer over the ceiling | Why |
|---|---|---|
| `$expand=Books` | **pages** | the case #313 is about |
| `$expand=Books($skip=0)` | **pages** | `$skip=0` is the identity; the continuation is still a faithful `?$skip={cap}` |
| `$expand=Books($count=false)` | **pages** | `$count=false` and an absent `$count` are already the same value |
| `$expand=Books($top=0)` | `200`, `[]`, **no link** | the client asked for zero rows and got zero rows — the response is complete with respect to the request |
| `$expand=Books($top=N)`, `N ≤ cap` | `200`, **no link** | same reasoning; an explicit `$top` wins over the default bound |
| `$expand=Books()` | `400` | rejected by the OData URI parser before OhData sees it — *"Missing expand option on navigation property 'Books'"* |
| `$expand=Books($filter=…)` / `($orderby=…)` / `($select=…)` | `400` | a `$skip`-only link cannot carry a nested option, so hop 2 could not reproduce hop 1 |
| `$expand=Books($skip=N)`, `N > 0` | `400` | same: the offset is already in play and the link carries only `$skip` |
| `$expand=Books($count=true)` | `400` | §11.2.5.5 requires a count to be *"the total count of results across all pages"*, i.e. the **full filtered** count; a paged collection cannot report one. `Nav@odata.count` and `Nav@odata.nextLink` therefore never coexist |
| `$expand=Books($expand=Chapters)` | `400` | a level with children is not SQL-bounded at all (`APPLY`/`LATERAL`); the rows were already fully materialized, so a link would advertise a bound that does not exist |
| `$expand=Nav($levels=N)` | `400` | same, at every level |
| a nav whose element type has a composite or unresolvable key | `400` | no single key ⇒ no total order ⇒ no sound `$skip` walk |
| depth ≥ 2 — the leaf under `$expand=Books($expand=Chapters)` | `400` | see [Deliberate limits](#deliberate-limits-and-why-they-are-limits) |
| a nav **this profile** declares with a delegate | `400`, and no route | delegate safety; see below |

That is the whole matrix, and it **fails closed**: over the ceiling, a shape either pages or `400`s.
There is no third answer and no commit at which a bound existed without one or the other, so silent
truncation never occurs.

#### The continuation

```jsonc
// GET /odata/BeAuthors?$filter=Id eq 1&$expand=Books      (MaxExpandTop = 3, ExpandPagingEnabled = true)
// 200
{
  "@odata.context": "http://localhost/odata/$metadata#BeAuthors",
  "value": [
    {
      "Id": 1, "Name": "Ann", "PublisherId": 100,
      "Books": [
        { "Id": 1, "AuthorId": 1, "Title": "Bk1" },
        { "Id": 2, "AuthorId": 1, "Title": "Bk2" },
        { "Id": 3, "AuthorId": 1, "Title": "Bk3" }
      ],
      "Books@odata.nextLink": "http://localhost/odata/BeAuthors(1)/Books?$skip=3"
    }
  ]
}
```

```jsonc
// GET /odata/BeAuthors(1)/Books?$skip=3
// 200
{
  "@odata.context": "http://localhost/odata/$metadata#BeAuthors(1)/Books",
  "value": [
    { "Id": 4, "AuthorId": 1, "Title": "Bk4" },
    { "Id": 5, "AuthorId": 1, "Title": "Bk5" }
  ]
}
```

Follow it to exhaustion the way you would any server-driven page. The continuation emits its own
envelope-level `@odata.nextLink` (at the absolute offset `$skip + MaxExpandTop`) while rows remain,
and omits it on the last page — a page that is exactly `MaxExpandTop` long is **not** assumed to have
more behind it, the same one-row probe the root path uses (#360).

Four properties of that route worth knowing:

- **It accepts `$skip` and nothing else.** Every other system query option returns
  `400 UnsupportedQueryOption` — including `$select`/`$orderby`/`$top`/`$count`, which the
  *delegate-backed* [navigation route](navigation-routing.md) on the same URL shape does accept.
  There is nothing to carry: the link is only ever emitted for an expand that had no nested options at all. Rejection is
  by the `$` sigil rather than a name allowlist, so a future OData system option this build has never
  heard of is refused rather than silently ignored. This route's sigil check was the precedent
  [#359 generalised to every read route](#unsupported-system-query-options-are-rejected-359-380-353),
  and it now shares that matcher rather than carrying its own copy.
- **`$format` is the one exemption, and it is not a data option.** §11.2.10 content negotiation is
  implemented once, on the group filter that wraps the whole OData surface, so `$format` never
  reaches this handler and cannot change a single row. Refusing it would make this the only route in
  the surface that `400`s a conformant, already-supported option, and would break the common client
  habit of appending it to a server-issued link. An unsupported `$format` **value** is still
  rejected, by that same group filter, unchanged.
- **It is ordered by the child key, unconditionally.** Not through the root path's
  `EnsureStableOrder`, which skips appending the key when the source is already ordered and would
  leave a pre-ordered parent's continuation without a total order. The key comes from the same
  resolution that composes the first page's tiebreaker, so both sides agree on the ordering column by
  construction. The emitted plan is an `INNER JOIN … LIMIT/OFFSET` index seek, not the partitioned
  `ROW_NUMBER()` window the first page uses.
- **It composes off the parent profile's own `GetQueryable`**, so a tenant filter or soft-delete
  predicate baked into that queryable scopes the continuation exactly as it scoped the first page,
  and the route requires no foreign-key knowledge (which the convention EDM does not have). Profile
  authorization applies to it as to every other route on the set.

The link's parent key is read from the **CLR entity**, never from the response JSON — a root
`$select` strips the key before the shaping pass runs, so `?$select=Name&$expand=Books` still emits
`"Books@odata.nextLink": ".../BeAuthors(1)/Books?$skip=3"` with a payload containing no `Id` at all.

Root paging and nested paging coexist without interacting. The root's continuation is a
`$skiptoken` on the collection route; the nested one is a plain `$skip` on a different path served by
a different route that has no `$skiptoken` concept. Neither link builder reads the response body, so
neither can rewrite the other, and a parent appearing on root page 2 gets its own independent child
links.

One **new startup failure**, and it can only fire on a registration that opted in: an entity-level
bound function sharing a name with a pageable navigation now throws from `MapOhData()`. Both would
claim `GET /{Set}({key})/{Name}`, and the pre-existing collision check compares bound functions
against structural properties only — which excludes declared navigations — so that pairing was legal
until this route existed. (The same check still does not cover a bound function colliding with a
**delegate-backed** navigation route; that collision predates #313 and is tracked in
[#416](https://github.com/en-gen/OhData/issues/416).)

#### Deliberate limits, and why they are limits

These are decisions, not gaps waiting to be filled. The first three are tracked together in
[#410](https://github.com/en-gen/OhData/issues/410) so they are not rediscovered as bugs.

- **A continuation for a parent key that does not exist returns `200` with an empty `value` and no
  link**, where `Microsoft.AspNetCore.OData` returns `404`. **This is a documented divergence.** The
  continuation is a `SelectMany` over the pinned parent, and a `SelectMany` cannot distinguish "no
  such parent" from "a parent that has no children" — both yield zero rows. Telling them apart would
  cost an existence probe, i.e. a second round trip on **every** continuation, to improve the status
  code of a request a well-behaved client never issues (it only ever follows a link the server
  emitted, which by construction names a parent that existed). Note the contrast with the
  delegate-backed navigation route on the same URL shape, where the handler decides — returning
  `null` there produces `404`. This route has no handler to ask, and does not probe for one.
- **Depth ≥ 2 stays `400`.** `$expand=Books` pages; `$expand=Books($expand=Chapters)` does not, at
  either level. The asymmetry is real and deliberate: a level with children cannot be SQL-bounded at
  all, so it is **unbounded in materialization** regardless of what the response says — a link there
  would advertise a bound that does not exist. Restricting emission to depth 1 also removes the
  set-authority question entirely, because at depth 1 the URL already names the parent set and there
  is no child entity set to disambiguate.
- **Delegate-backed navigations stay unbounded, and #313 does not close their DoS.** A navigation
  declared with a handler is never in the engaged pushdown tree, so no ceiling, no bound and no link
  applies to it; a nested `$top`/`$skip` on one is already `400` (#294). Bounding it would mean the
  framework silently truncating a collection the developer's delegate deliberately returned, which
  directly weakens the delegate-safety invariant. The real fix is a **contract** change — a delegate
  overload taking `(key, skip, take, ct)` — not a ceiling applied behind the delegate's back. Until
  then, a delegate is where you own the size of your own answer.
- **Delegate safety is the declaring set's own declaration, and a sibling's delegate does not
  suppress paging** ([#421](https://github.com/en-gen/OhData/issues/421)). Route registration and
  link emission share one predicate, whose `ServeRaw` test is resolved by `ResolveNavTreatment` over
  **the URL-named set alone** — byte-for-byte the candidate set the root read path uses. A navigation
  *this* profile declares with a delegate is `RunDelegate`, so it never gets a raw continuation route
  or a link; that is the invariant, and it is unchanged.

  Until #421 the predicate resolved over the whole sibling union instead, so a *sibling* profile
  declaring the navigation with a delegate suppressed both the route and the link on the
  **delegate-less** set. That protected nothing: under declaring-set authority the root `$expand` on
  the delegate-less set serves those rows **raw** regardless — the root resolves its treatment
  against the URL-named profile alone — so the withheld route only
  removed the paging escape hatch, leaving an over-ceiling bare `$expand` at a permanent `400` on a
  navigation the profile itself declared delegate-less, with `ExpandPagingEnabled` silently inert for
  that entity set. The continuation reads the parent profile's own `GetQueryable` under that set's
  own authorization, so the rows it serves are a strict subset of what the `$expand` beside it
  already returns to the same caller; nothing crosses an entity-set boundary. The related claim that
  a sibling delegate blanks a *root-level* `$expand` was measured false and is why
  [#415](https://github.com/en-gen/OhData/issues/415) was closed as refuted.
- **`Prefer: odata.maxpagesize` *is* honoured on the nested page size, as of
  [#412](https://github.com/en-gen/OhData/issues/412).** It **narrows** the nested page and is clamped
  down to `MaxExpandTop`, never up — the ceiling is the server's DoS bound and a request header may
  not lift it, exactly as the root collection clamps `maxpagesize` to `MaxTop`. It applies only where
  a continuation link is actually going out (a truly bare expand on a pageable navigation): trimming
  a collection that gets no link would be the silent truncation the M1 rule forbids, so a
  non-pageable over-ceiling shape keeps its `400` and ignores the header entirely. Both spellings are
  accepted (`odata.maxpagesize` is the OData 4.0 name, `maxpagesize` the 4.01 rename). The
  continuation route honours it too, so a client that keeps sending the header gets a consistent page
  size all the way down; a client that stops simply gets `MaxExpandTop`-sized pages from there on,
  and nothing is skipped or repeated either way because `$skip` is an absolute offset advanced by the
  rows each hop actually served. `Preference-Applied` is **unchanged** — §8.2.8.5 makes the echo a
  `MAY` and gives it a single value for the whole response ("the maximum page size applied"), so
  there is no per-collection echo to add. This is what closes #412's stated blocker: the spec says in
  terms that *"the client MAY specify a different value for this preference with every request
  following a next link"*, so the page size is expected to travel on the request rather than inside
  the link, and the `$skip`-only continuation surface did not have to widen.
- **The continuation *link* is for a pushed expansion only. The *ceiling* applies to every
  raw-served one, at every level, as a `400`** ([#418](https://github.com/en-gen/OhData/issues/418),
  widened by [#463](https://github.com/en-gen/OhData/issues/463) and
  [#464](https://github.com/en-gen/OhData/issues/464)). With `MaxExpandTop` set, a `GET /{Set}({key})`,
  a `GetAll`, a Priority-1 or a non-EF `GetQueryable` read whose expanded collection exceeds it
  returns `400` (`InvalidQueryOption`) instead of the whole collection — and so does a deeper level
  of any of them, or of a pushed branch the planner declined. `ExpandPagingEnabled` buys nothing on
  those: the link would need page 1 and the continuation to agree on an order, and there the
  framework composes neither side — the child rows arrive already materialized inside whatever the
  handler returned, while the continuation orders by the child key *in the database*.
  Re-sorting the serialized JSON cannot reconcile the two (a JSON compare is not the column's
  collation, and is not SQL Server's `uniqueidentifier` order), and a link over a disagreeing order
  skips and duplicates rows invisibly. So the M1 rule is satisfied with the `400`, per #418's own
  recommendation for exactly this case. **What the `400` does *not* buy is a materialization bound:**
  the collection was loaded by your own handler before the framework saw it, so this is a data
  ceiling only (the #299 trade). Size the eager loads in your `GetById`/`GetAll` accordingly, or do
  not eager-load at all — a `GetById` that does not `Include` the navigation serves `[]` and never
  trips it.

---

## Complexity limits (#202)

Five ceilings bound how expensive a single request's query options may be. Each is configurable globally via `WithDefaults` or per entity set on the profile (the profile value overrides the global default); a request that exceeds a limit is rejected with `400` before any handler runs. They apply on all three collection read paths (`GetQueryable`, `GetAll`, Priority-1). The table also carries `ExpandPagingEnabled`, which is **not** a ceiling and rejects nothing — it is listed here because it is meaningless apart from `MaxExpandTop` directly above it.

> **`MaxExpandTop` is the one entry whose reach is not uniform, so read its row with this table beside it.** It is not a single check but three mechanisms with three different enforcement points, and only the first is a pre-handler validation:
>
> | Mechanism | Where it is enforced | Which routes reach it |
> |---|---|---|
> | Explicit nested `$top` over the ceiling → `400` | pre-handler validation, walked over the whole `$expand` tree | `GetQueryable`, `GetAll`, Priority-1 **and** `GET /{Set}({key})` (#301). Not the `/$count` route, the delegate-backed navigation routes, or the `$skip` continuation route. |
> | Nested `$count` over the ceiling → `400` (#254) | the JSON shaping pass over the pushed-down expand tree | **A navigation the `$expand` pushdown engaged** — i.e. the `GetQueryable` collection route over an EF Core `IQueryable`. |
> | Bare `$expand` over the ceiling → `400`, or the `Nav@odata.nextLink` continuation when `ExpandPagingEnabled` is on (#313) | same shaping pass | Same. |
> | Any **raw-served collection** navigation over the ceiling → `400` (#418/#463/#464) | a size check over the serialized response, per level, after the expand pipeline | **Everything else, on every read path**: `GetAll`, Priority-1, a non-EF `IQueryable` (which `$search` also produces), a branch the pushdown declined, every level of `GET /{Set}({key})`, and every level below a raw-served parent. Never a link, whatever `ExpandPagingEnabled` says. |
>
> **The split is between how the collection was LOADED, not which route was called.** Rows 2 and 3 apply where the framework *composed* the child query, because that is also where it composed the child-key `ORDER BY` that lets page 1 and a `$skip` continuation agree. Row 4 covers everything the framework did not compose: the rows arrive already materialized inside whatever your handler returned, in that handler's own order, so a continuation link over them would silently skip and duplicate across the page boundary — which is why it is always a `400` and never a link (the full argument is #418's, and #463/#464 only widened where it is applied).
>
> **This was not always so, and the gap was the whole of [#463](https://github.com/en-gen/OhData/issues/463) and [#464](https://github.com/en-gen/OhData/issues/464).** Row 4 used to read "`GET /{Set}({key})` only", checked at **depth 1** only, against a set of navigations resolved once at startup from the root profile. So: with a ceiling of 2, `GET /Authors(2)?$expand=Books($expand=Chapters)` served every chapter (the depth axis), and `GET /Authors?$expand=Books` over a `GetAll`, a Priority-1 or a non-EF source served every book with no bound at all (the path axis) — while this document and `MaxExpandTop`'s own XML doc said the value bounded *every* collection `$expand` level. Both are closed; the ceiling now has no gap in depth or in path.
>
> **The consequence worth knowing before you rely on the ceiling as a DoS bound:** row 4 is a **data** ceiling, not a materialization bound. The related rows are loaded by your own handler — typically an EF `Include` inside `GetById`, or a `ToList()` behind `GetAll` — before the framework ever sees the entity, so the `400` is raised *after* that load. It stops the over-sized collection reaching the client, and stops it being served silently truncated, but it cannot stop the query. The mitigation is in the handler: do not eager-load an unbounded child collection (a `GetById` that does not `Include` it serves `[]`), or bound the load yourself. Rows 2 and 3 are the only ones that bound the *fetch*.
>
> **A navigation whose delegate actually RAN is outside all four rows** (#313 O6): those rows are what your `Handler`/`BatchHandler` returned, and the framework neither truncates nor rejects them. Bound them in the delegate.
>
> Read that as written — *ran*, not *is declared with a delegate*. The two differ below a raw-served parent, and the difference is not academic: the expand pipeline does not recurse into a delegate-less navigation's subtree (whatever your handler already materialized there **is** the answer), so a navigation declared with a delegate one level under it is **never invoked** and the rows in the payload came from the *parent's* handler. Those are bounded by row 4 like any other raw rows. Measured, cap 2, `GetAll`, `Author —Books(delegate-less)→ Book —Chapters(delegate)→`: `?$expand=Books($expand=Chapters)` served five chapters with the `Chapters` delegate invoked zero times. What stays exempt is a delegate-backed navigation the framework really did call — which is the depth-1 case, and is also the only place the walk could reach one, since it never descends into a delegate's subtree.
>
> **Row 4 bounds the collection; it does not APPLY the nested window.** On a raw-served expansion a nested `$top`/`$skip` *within* the ceiling is accepted and then ignored, so what comes back is the whole collection (now ceiling-bounded), not the window that was requested. That residue is tracked by [#352](https://github.com/en-gen/OhData/issues/352)/[#464](https://github.com/en-gen/OhData/issues/464).

| Limit | Default | Bounds |
|---|---|---|
| `MaxExpansionDepth` | `3` (hard ceiling **6**) | Nesting depth of `$expand`, and the ceiling `$levels` is resolved and capped to (`$levels=max` becomes exactly this value). **Enforced** as of #202 — a deeper `$expand`/`$levels` returns `400` rather than a silently-truncated result. Advertised per entity set in `$metadata` as `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` (#206). Raise it to allow deeper graph/hierarchy queries, or lower it to harden — but **not above `EntitySetDefaults.MaxExpansionDepthCeiling` (6)**, which throws `ArgumentOutOfRangeException` at startup (#328). See [The depth ceiling](#the-depth-ceiling-328) below for why the ceiling exists and why it is 6. |
| `MaxExpandTop` | `null` (no ceiling) | Per-navigation ceiling on how many related entities **any** collection `$expand` level may return, and on an explicit **nested** `$top` (`?$expand=Children($top=N)`). **The whole ceiling is opt-in (#313):** the default moved from `1000` to `null` because `1000` was an invented number — the framework cannot know how large a child collection is, so it ships the control point and lets the implementor set it. Until it is set there is no ceiling of any kind: `?$expand=Children($top=999999)` is answered rather than rejected, a nested `$count` materializes the related collection with no bound, and a bare `?$expand=Children` composes no SQL `Take` and gets no size check — byte-identical response *and* emitted SQL to the pre-#313 behavior. Set it (`WithDefaults(d => d.MaxExpandTop = N)`, or per profile) to turn all of that on at once. With a value in force, three mechanisms engage, and **their reach differs — see the callout above the table.** (1) An over-large **explicit nested `$top`** returns `400` (`InvalidQueryOption`) at any depth, whether or not the navigation would have been pushed down, on all three collection read paths and on `GET /{Set}({key})` — the same "what may a client ask for" rule as the root `MaxTop`. (2) A **nested `$count`** whose related collection exceeds the ceiling returns `400` rather than a truncated count (§11.2.4.2, #254). (3) As of #313, so does the **remaining shape** — a level with **neither** a nested `$count` **nor** an explicit nested `$top`, which includes the plain `$expand=Nav` and anything carrying only `$select`/`$orderby`/`$filter`/`$skip`, and every level of a `$levels=N` recursion. **(2) and (3) are enforced in the pushdown's JSON shaping pass, so they apply to a navigation the `$expand` pushdown actually engaged — the `GetQueryable` collection route over an EF Core `IQueryable`.** (4) Every **other** expanded delegate-less collection navigation is size-checked against the same ceiling, at every level of the `$expand` tree and on every read path, and always as a `400` ([#418](https://github.com/en-gen/OhData/issues/418), widened by [#463](https://github.com/en-gen/OhData/issues/463) and [#464](https://github.com/en-gen/OhData/issues/464)): `GetAll`, Priority-1, a non-EF `IQueryable` (`$search` produces one), `GET /{Set}({key})`, a branch the pushdown declined, and every level below a raw-served parent. That check covers every nested shape rather than only the bare one because a raw-served navigation has **no** nested option applied to it — `$filter`, `$orderby`, `$select`, `$skip`, `$top` and `$count` are all silently ignored there, unlike on the pushed path — so a bare-only ceiling would be bypassable by appending any one of them. Before #463/#464, (4) was checked at **depth 1 of the single-entity read only**, so `?$expand=Books($expand=Chapters)` and every non-EF collection path served unbounded collections under a doc that claimed otherwise. A navigation whose delegate the framework actually **invoked** is never capped by any of the four (#313 O6: it does not truncate — or reject — a delegate's answer); a navigation merely *declared* with a delegate but reached under a raw-served parent, where the pipeline never recurses and so never calls it, carries the parent handler's own rows and is capped by (4) like any other raw collection. The **root** entity set's resolved value governs at every nesting depth, exactly like `MaxExpansionDepth`. On a profile, `MaxExpandTop = null` means *inherit* the resolved default, not "uncapped" — a profile cannot opt out of a ceiling set in the defaults. Setting a value also composes the nested **key tiebreaker** on shapes that previously had none, so it governs the nested wire *order* as well as the status code (see the nested-paging caveat above). **Cost caveat (#299):** where the ceiling applies it is always *correct* — the request `400`s rather than returning a truncated count or a silently-clipped page — but not always *cheap* to enforce. At a projection **leaf** it is a SQL `Take(MaxExpandTop + 1)`, so a breach is detected without transferring the collection. At a level with its own nested `$expand`, or anywhere inside a `$levels` recursion, it can't be pushed into SQL as a `Take` (the same `APPLY`/`LATERAL` translation problem the nested-`$count` caveat above describes), so the `400` is thrown only **after** the full related collection — for `$levels`, the full recursive hierarchy — is materialized in memory. A hostile `$expand=Children($levels=N)` therefore buys that full materialization before being rejected on breach — a broad but *under*-cap hierarchy just materializes fully and returns `200` like any other under-cap page; the cost only bites once the collection actually exceeds the ceiling. |
| `ExpandPagingEnabled` | `false` | **Not a ceiling — the companion opt-in to `MaxExpandTop` (#313).** Whether a *truly bare* collection `$expand` (one carrying no nested options at all) whose child collection exceeds the resolved `MaxExpandTop` is served as its first `MaxExpandTop` children plus a `Nav@odata.nextLink` continuation, instead of being rejected with `400`. Inert unless `MaxExpandTop` is also set — with no ceiling there is no boundary at which a continuation could begin — and `MaxExpandTop` is also the page size, for the first page and every continuation alike. There is deliberately no second page-size knob. It is a *separate* opt-in from the ceiling because a continuation link is **worse** than a `400` for a client that does not read nested annotations: that client sees a complete-looking collection that has been silently truncated. Only enable it if you know your clients follow `Nav@odata.nextLink`. On a profile it is a `bool?`, so a profile-level `false` genuinely opts **out** of a server-wide `ExpandPagingEnabled = true` — unlike `MaxExpandTop`, whose profile-level `null` means *inherit*. When it is on (with a ceiling set) it registers `GET /{Set}({key})/{Nav}?$skip=N` for each pageable navigation and emits the link; with it off — or on with no ceiling — no route is registered and no annotation is emitted. Turning it on changes **nothing** outside the truly-bare over-ceiling subset: with `MaxExpandTop` unset, and for every non-bare shape with it set, the status, the response body and the emitted SQL are byte-identical either way, and `$metadata` is byte-identical in every configuration. Full rules, the exact pageable set and the deliberate limits: [Nested server-driven paging](#nested-server-driven-paging-expandpagingenabled-313). |
| `MaxExpandBreadth` | `50` | Number of navigation expansions in a request's `$expand`, counted across **every level of the tree** (a `$levels=N` expansion counts as `N`). Over the limit is `400` (`InvalidQueryOption`) before any handler runs, on every read path that applies `$expand` — the three collection routes and `GET /{Set}({key})`. Depth-independent and pushdown-independent: it is a statement about what the client may *ask for*. See [The breadth guard](#the-breadth-guard-429) below. |
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

The node-count defaults are unchanged from what OhData already applied (they were previously hardcoded); #202 makes them lowerable. Note that a **root** `$top`/`$skip` is governed separately by `MaxTop` (see above), not by these node counts; a **nested** `$top` inside a `$expand` is governed by `MaxExpandTop`.

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxExpandTop = 200)   // opt in to the ceiling; the default is null (none)
    .AddEntitySetProfile<OrderProfile>());
```

### The depth ceiling (#328)

`MaxExpansionDepth` is capped at **`EntitySetDefaults.MaxExpansionDepthCeiling`, which is 6**.
Configuring a larger value — in `WithDefaults` or on a profile — throws
`ArgumentOutOfRangeException` at startup, not at request time.

**Why a ceiling exists.** Relational query translation for a pushed nested projection is
`Θ(3ⁿ)` in the nesting depth. EF Core re-translates each nested-collection subtree three times with
no memoization, so every extra level triples the CPU spent *building* the query — before a single
row is read. This is not a data-volume problem: it reproduces with no database, no connection and no
rows, purely through `ToQueryString()`. Measured on a 16-node self-referential chain returning a
~6 KB body, one navigation per level:

| depth | translation |
|---:|---:|
| 5 | 0.09 s |
| **6** | **0.24 s** ← the ceiling |
| 8 | 3.8 s |
| 10 | 32 s |
| 12 | 291 s |

291 seconds is 4.9 minutes of single-core CPU for **one unauthenticated request with no body**, and
the growth is a clean ×3.0 per level with no discontinuity — there is no cliff to stay below, only a
curve to stop climbing.

**Why 6 and not 3.** The blow-up is at 10+, not at 5. Depth 5 costs ~90 ms, and this document's own
example above uses `MaxExpansionDepth = 5`, as do two of the framework's own tests. Capping at the
default of 3 would invalidate a documented configuration for a shape that is not expensive. 6 leaves
headroom above 5 while keeping the worst *configurable* depth under a quarter-second on the depth
axis.

**This is a mitigation, not a fix.** Nothing about `$levels=12` over a 16-node chain returning 6 KB
is unreasonable — it is expensive only because of upstream re-translation. The real answer is one
flat query per level instead of one nested projection, tracked in
[#430](https://github.com/en-gen/OhData/issues/430). Until then the ceiling bounds the damage.

**If you need a deeper graph**, fetch it as separate requests, or expand a **delegate-backed**
navigation (`HasMany(x => x.Children, getAll: ...)`) — a delegate-backed navigation is loaded once
per level by the expansion pipeline rather than composed into one nested projection, so it does not
pay the `3ⁿ` translation cost at all.

**Depth is only one axis.** Breadth multiplies on top of it and is bounded separately by
[`MaxExpandBreadth`](#the-breadth-guard-429).

### The breadth guard (#429)

`MaxExpandBreadth` (default **50**) caps how many navigation expansions one request's `$expand` may
contain, **counted across every level of the tree**. Over the limit is `400` (`InvalidQueryOption`)
before any handler runs.

**Why depth alone is not enough.** Translation cost multiplies by ~3 per level *and* by the number
of navigations expanded at each level. Measured at the **default** `MaxExpansionDepth` of 3, on a
model with six collection navigations, before this guard existed:

| navigations per level | wall clock | response |
|---:|---:|---:|
| 1 | 240 ms | 1,440 B |
| 4 | 1,010 ms | 1,696 B |
| 6 | 4,084 ms | 1,952 B |

4.1 seconds of single-core CPU for a 1,952-byte response, at defaults, unauthenticated. And the
compiled-query cache is no defence: each distinct navigation **subset** is a distinct EF cache key,
so a client cycling subsets never warms it and pays full translation cost on every request.

> The table above is the original #429 measurement; the "why 50" figures below were taken later on a
> faster machine (the same shape reproduces at ~1.6 s there). Compare each set internally — the
> ratios hold across both — not across the two.

**Why the count spans the whole tree.** A per-level cap of `B` under a depth ceiling of `D` still
admits `B^D` expansions — 55,986 at `B=6, D=6`. Counting every node bounds both axes at once.
Counting *distinct navigation names* would be weaker still: the most expensive shapes measured reuse
six names over six levels.

**Why 50.** It is far above any realistic request — a three-level chain expanding three navigations
at every level is 39 nodes and is already unusual; typical rich requests are well under 15 — and it
keeps the worst legal request measurable. At the default depth of 3 a 50-node `$expand` measures
~0.4 s (interpolated between 39 nodes = 308 ms and 84 nodes = 699 ms). At the *maximum legal* depth
of 6, a systematic sweep of every branching vector within the budget put the worst legal request at
**1.0–1.4 s** — shape `[1,1,1,1,2,6]`, only 18 nodes, because deep-and-narrow is more expensive per
node than flat-and-wide. Unguarded, the same model reaches 2,850 nodes and **36 s** for a 111-byte
error response; that same request now returns `400` in **56 ms**, essentially all of it URL parsing.

It is a knob precisely because 50 is a judgement call rather than a law:

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxExpandBreadth = 20)   // harden every entity set
    .AddEntitySetProfile<OrderProfile>());

public class GraphProfile : EntitySetProfile<int, Node>
{
    public GraphProfile() { MaxExpandBreadth = 200; /* this set genuinely needs a wide graph */ }
}
```

A `$levels=N` expansion counts as `N` — its resolved level count — because that is what it costs:
one nested projection level each, exactly like the equivalent explicit chain. Everything else counts
as one.

---

## `$search`

Register a `Search` handler to support free-text search:

```csharp
Search = async (term, ct) => OhDataResult.Success<IEnumerable<Product>>(
    await db.Products
        .Where(p => p.Name.Contains(term) || p.Description.Contains(term))
        .ToListAsync(ct));
```

```
GET /odata/Products?$search=widget
```

Without a `Search` handler, `$search` requests return `400 Bad Request` (`UnsupportedQueryOption`). The interpretation of the search term is entirely up to the handler.

On the `GetQueryable` path, `$search` composes with the other query options: the handler's results become the base sequence, and `$filter`, `$orderby`, `$top`, and `$skip` are then applied on top of the search results (in that order). On the `GetAll` path, `$search` composes the same way with the options `GetAll` supports: the handler's results become the base sequence, and `$top`/`$skip` are applied on top of them (`$filter`/`$orderby` remain unsupported on this path regardless of `$search`).

**The `Search` handler belongs to those two paths only, and a `Priority-1` profile that sets one is refused at startup (#465).** Both compositions above work the same way — the handler *replaces the source*, and the framework then applies the remaining options on top — and that is only possible where the framework owns the pipeline. `GetODataQueryable` inverts the contract: the profile receives the whole `ODataQueryOptions<TModel>` and applies them itself, so there is nowhere to feed a search-derived source in. Honouring `$search` there would mean bypassing the profile outright, which would drop `$filter`/`$orderby` on exactly the requests that carry `$search`, and route around whatever row-level scoping the handler applies. So `$search` on the Priority-1 path is the profile's own business, reachable as `options.Search` inside `GetODataQueryable` exactly like every other option it is handed — and a `Search` handler beside it is dead configuration, refused with an `InvalidOperationException` from `MapOhData()` rather than silently ignored. It used to be silently ignored *and* advertised in the generated OpenAPI description.

---

## `$skiptoken` (server-driven paging)

When a response includes `@odata.nextLink` (emitted once the page size reaches `MaxTop` or the client-requested `maxpagesize`), the link contains a `$skiptoken` value:

```
GET /odata/Products?$top=20
→ "@odata.nextLink": "https://host/odata/Products?$top=20&$skiptoken=MjA="
```

**`$skiptoken` is a Base64-encoded raw 4-byte little-endian integer - the literal skip offset - not an opaque or cryptographically-protected cursor.** A client (or anyone who intercepts a link) can trivially decode, predict, or forge a token to jump to an arbitrary offset; it provides no more protection than sending `$skip` directly. Don't rely on it to gate access to specific pages or ranges of data - apply authorization/filtering in the handler itself if that matters.

A malformed or corrupted `$skiptoken` (wrong length, invalid Base64) returns `400 Bad Request` (`InvalidSkipToken`). If both `$skip` and `$skiptoken` are present, `$skip` takes precedence - and, as of #360, an explicit client `$skip` is carried into the emitted token, so paging that starts at a non-zero offset advances from there instead of rewinding to it.

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

---

## Unsupported system query options are rejected (#359, #380, #353)

OData Part 1 §11.2.5: *"If a data service does not support a system query option, it MUST fail any
request that contains the unsupported option."* Every read route enforces that, and enforces it by
the **`$` sigil** rather than by a list of names it happens to know about.

Concretely, a request is refused with **`501 Not Implemented`** (`UnsupportedQueryOption`) when it
carries a query key that begins with `$` and is not in the route's own implemented set:

```
GET /odata/Products?$unknown=1        -> 501  "The query option '$unknown' is not supported."
GET /odata/Products?$slect=Name       -> 501
GET /odata/Products?$levels=2         -> 501   ($levels is a real option, but only inside $expand)
GET /odata/Products(1)?$filter=…      -> 501   (a single entity has nothing to filter)
GET /odata/Products/$count?$search=x  -> 501
GET /odata/Products?$apply=…          -> 501   (was 400 from 1.0.0 through 1.6.0)
```

Until 1.7.0 each of these returned `200` with the option parsed and thrown away - and on the
collection routes the discarded option was echoed back into the `@odata.nextLink` the server
generated. A `200` from `?$filter=…` reasonably tells a client that the filter was applied.

### `501` or `400`: which, and why

Both statuses are correct, for different conditions, and OData decides between them.

- **§9.3.1** (a MUST): *"If the client requests functionality not implemented by the OData Service,
  the service MUST respond with 501 Not Implemented and the response body SHOULD describe the
  functionality not implemented."*
- **§13.1.1 item 7**, inside the Minimal Conformance MUST list: *"MUST successfully parse the
  request according to [OData-ABNF] for any supported system query string options and either follow
  the specification or return 501 Not Implemented (section 9.3.1) for any unsupported
  functionality"*.
- **§11.2.5**'s own status advice is only a SHOULD, but it points the same way.

OhData claims Minimal conformance, so the `501` is not optional. In five words:

> **`501` is "can't". `400` is "won't".**

The test for which side a refusal falls on is mechanical: **could any setting on the profile make
this same request succeed on this same route?** Yes -> `400`. No -> `501`.

| Condition | Status | Code |
|---|---|---|
| An unrecognized `$`-name, or an option this build implements nowhere (`$apply` `$compute` `$index` `$deltatoken`) | `501` | `UnsupportedQueryOption` |
| An option the addressed **route** does not implement (`$filter` on `GET /Set({key})`, `$search` on a `/$count`, `$select` on a single-valued navigation) | `501` | `UnsupportedQueryOption` |
| `$filter`/`$orderby` on the `GetAll` path, and `$filter` on the `GetAll`-backed `/$count` | `501` | `UnsupportedQueryOption` |
| A capability flag left `false` (`FilterEnabled`, `OrderByEnabled`, `SelectEnabled`, `ExpandEnabled`, `CountEnabled`) | `400` | `UnsupportedQueryOption` |
| A property allowlist rejection (`FilterProperties` and friends) | `400` | `InvalidQueryOption` |
| `$search` with no `Search` handler, on a route that has a `$search` leg | `400` | `UnsupportedQueryOption` |
| A malformed or empty option **value** on a route that implements the option (`$top=abc`, `$skiptoken=`) | `400` | `InvalidQueryOption` |
| A value outside a configured bound (`MaxTop`, `MaxExpandTop`) | `400` | `InvalidQueryOption` |

`$search` shows both sides of the line for one option: with no `Search` handler it is a `400` on the
collection `GET`s, which really do invoke one when configured, and a `501` on `/$count` and
`GET /Set({key})`, which have no `$search` leg at all.

`$filter`/`$orderby` on `GetAll` is "can't". Its message names a configuration change
(*"Configure GetQueryable…"*), which reads like the `400` side, but the refusal is
flag-**independent**: there is no `IQueryable` on that path and therefore no filter code, so
`FilterEnabled = true` changes nothing, and the remedy supplies a **different handler** — it has the
request served by a different route implementation rather than switching this one on. That is
§9.3.1's *"functionality not implemented"*, and the existing message is already what its `SHOULD`
asks the body to do: it describes the unimplemented functionality.

> **The same option can be `501` on one entity set and `400` on another**, decided by which read
> handler the profile supplies. `$filter` on a `GetAll`-backed set is `501` — the framework *can't*
> filter it, under any configuration. `$filter` on a `GetQueryable`-backed set with
> `FilterEnabled = false` is `400` — it *can*, and you chose not to expose it. That is correct, not
> an inconsistency: conformance is per-**resource**, and the two answers tell a client genuinely
> different things — *"no configuration of this endpoint will ever do that"* versus *"this endpoint
> could, and is not offering it"*.

> **⚠ BREAKING for `$apply`/`$compute`/`$index`/`$deltatoken`.** Those four have answered
> `400 UnsupportedQueryOption` since 1.0.0 and now answer `501`. The error **code** and the message
> **bytes** are unchanged, so a client matching on the envelope keeps working; code branching on
> `StatusCode == 400` for this condition must add `501`.

### What is *not* touched

- **Custom query options.** Part 2 §5.2 requires a custom query option to *not* begin with `$`, so
  any key without the sigil is passed through untouched: `?myTenant=acme` is your business, and the
  framework's own `ohdata-skiptoken` continuation offset is deliberately spelled without a `$` for
  the same reason.
- **Parameter aliases** (§5.3) begin with `@`, not `$`, and are likewise untouched.
- **Mixed-case spellings of real options.** `$Select` and `$TOP` are honoured, as they always have
  been. `Microsoft.AspNetCore.OData` lowercases an option name before matching it whenever the URI
  resolver enables case-insensitivity - the default - so this is alignment with the stack OhData
  sits on, not leniency. The inconsistency #359 reported (`$Select` applied, `$slect` ignored,
  neither rejected) is resolved by rejecting `$slect`.
- **`$format`.** Accepted on every route: §11.2.10 content negotiation is implemented once, on the
  group filter that wraps the whole OData surface, so it never reaches a route handler and cannot
  change a row. An unsupported `$format` *value* is still rejected there.
- **Routes outside the table, which still ignore every query option.** Read the table as a list of
  what *is* gated, not as the whole URL surface. The structural-property **writes**
  (`PUT|PATCH|DELETE /{Set}({key})/{Prop}`), the service document (`GET /{prefix}`) and
  `GET /{prefix}/$metadata` are ungated: `GET /odata?$unknown=1` answers `200` with the service
  document. None of them builds a link, so none can echo an option back the way #359 reported. The
  property writes are ungated *consistently with the entity writes* — `PUT|PATCH|DELETE
  /{Set}({key})` are not gated either, so no two routes over one resource disagree.

  The structural-property **reads** were in this list until #560 and are now gated:
  `GET /{Set}({key})/{Prop}` and its `/$value` implement `$format` and nothing else, so every other
  `$`-option is `501`. They were the one residual that produced the split this whole rule exists to
  remove — `GET /Widgets(1)?$filter=…` answered `501` while `GET /Widgets(1)/Name?$filter=…`
  answered `200` with the filter silently dropped. Note `$select` and `$expand` are refused here
  although the sibling entity route implements them: the property handler goes straight from the
  property accessor to the envelope and reads no option at all.

### The per-route sets

The sets differ, and that is the point - `$filter` is implemented on a collection GET and
meaningless on a single entity.

| Route | Accepted |
|---|---|
| `GET /{Set}` (`GetQueryable`) | `$filter` `$orderby` `$top` `$skip` `$select` `$expand` `$count` `$search` `$skiptoken` `$format` |
| `GET /{Set}` (`GetODataQueryable`, Priority-1) | **whatever the profile declares** in `HonouredQueryOptions`, plus `$format`. The default is what `ODataQueryOptions.ApplyTo` honours - the row above **minus `$search`**, because `ApplyTo` drops `$search` when no `ISearchBinder` is registered (#475) |
| `GET /{Set}` (`GetAll`) | the same, **minus `$skiptoken`** - this path continues with `$skip` and never read a `$skiptoken` |
| `GET /{Set}/$count` | `$filter` `$top` `$skip` `$orderby` `$expand` `$select` `$format` - only `$filter` is applied; §11.2.9 requires the rest to be ignored, and since #580 the segment negotiates nothing at all (any `Accept`, any `$format` value, always `text/plain`) |
| `GET /{Set}({key})` | `$select` `$expand` `$format` |
| `GET /{Set}({key})/{Nav}` — **collection**-valued (`HasMany`) | `$select` `$orderby` `$skip` `$top` `$count` `$format` |
| `GET /{Set}({key})/{Nav}` — **single**-valued (`HasOptional`/`HasRequired`) | `$format` only |
| `GET /{Set}({key})/{Nav}/$count` | `$top` `$skip` `$orderby` `$expand` `$select` `$format` - it applies **none** of them, and refuses `$filter` as well as `$search` |
| `GET /{Set}({key})/{Nav}?$skip=N` (the #313 `$expand` continuation) | `$skip` `$format` |
| `GET /{Set}({key})/{Prop}` and `GET /{Set}({key})/{Prop}/$value` (#560) | `$format` only |
| `GET\|POST /{Set}/{Op}` and `GET\|POST /{Set}({key})/{Op}` (bound operations) | `$top` `$skip` `$format` |
| `GET\|POST /{Op}` (unbound operations) | `$format` only |

Being in a set means *the route implements the option*, not that this profile permits it: a
`$filter` on a set with `FilterEnabled = false` is still a `400`, with the capability flag's own
message naming the flag. A recognized-but-not-implemented-here option and a completely unrecognized
one share one code, because the client's remedy is identical.

Two things in that table are worth reading twice.

**The two navigation rows are one URL shape with two handlers.** `GET /{Set}({key})/{Nav}` is
mapped once, and which branch runs is decided by whether the navigation was declared with
`HasMany` or with `HasOptional`/`HasRequired`. Only the collection branch applies query options:
the single-valued branch serializes the related entity and reads nothing off the query string,
not even `$select`. It therefore accepts `$format` and refuses everything else — including
`$select`, which its collection sibling really does implement. If you need a projection of a
single related entity, read it from its own entity set (`GET /{ChildSet}({childKey})?$select=…`).

**Bound operations honour `$top`/`$skip`, unbound ones do not.** A bound function or action that
returns a collection of the profile's model type is bounded by `MaxTop` and pages with a
`$skip` continuation (#357 for a function, #543 for an action), so `$top`/`$skip` are real
there and are listed unconditionally — the server can emit a `$skip` link on any of those routes, and refusing the option would mean
refusing a link the server itself issued. Unbound operations have no such pipeline. An
operation's **own parameters** are query-string keys without a `$` (functions) or JSON body
members (actions), so the sigil rule never examines them.

**The two `/$count` rows are governed by §11.2.9 rather than by this feature's general rule, and
they are the one place an accepted option is deliberately ignored.** That clause partitions the
system query options for a count segment: the count is taken *after applying any `$filter` or
`$search`*, and it *MUST NOT be affected by `$top`, `$skip`, `$orderby`, or `$expand`*. So the
options in the first class are **applied where the route can and refused where it cannot** —
ignoring one would answer a wrong number under a `200` — and the options in the second class are
**accepted and ignored**, because that is the behaviour the clause specifies rather than a
shortfall to confess with a `501`.

The two rows differ only in how much the route can apply. The entity-set segment applies `$filter`
and refuses `$search`; the navigation segment invokes the navigation delegate and counts what comes
back, so it can apply neither and refuses both. `$select` is not named by §11.2.9 but is ignored on
the same reasoning as the four that are: it changes an item's shape, never its membership, and the
response is a bare scalar. `$format` is accepted-and-ignored too — §11.2.9 disallows content
negotiation on this segment, so unlike every other row in the table it does not mean "negotiated
here". See the [`/$count` table](#count) above.

### Why `501`, and what it costs

§11.2.5's status advice is a SHOULD, and an earlier revision of this feature leaned on that to
answer `400` throughout. Two other clauses settle it the other way: **§9.3.1** makes `501` a MUST
for *"functionality not implemented by the OData Service"*, and **§13.1.1 item 7** puts that same
`501` inside the Minimal Conformance MUST list, which this project claims. See the table above for
the `501`/`400` split.

The cost is a wire break on `$apply`/`$compute`/`$index`/`$deltatoken`, which had answered `400`
since 1.0.0. It was accepted because the alternative is failing a MUST in the conformance level the
project advertises. What is preserved instead is the **envelope**: the error `code`
(`UnsupportedQueryOption`) and the message bytes are identical to the `400` they replace, on every
route, so one condition still produces one body and only the status line moves.
