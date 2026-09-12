# Query Options

OhData supports the OData 4.0 system query options. Which ones are applied depends on the
collection handler you choose for the entity set — see
[choosing a read handler](read-handlers.md).

`$expand` has its own page: [`$expand` and nested paging](expand.md). The ceilings that bound how
expensive one request may be are in [complexity limits](complexity-limits.md). An option a route does
not implement is refused rather than ignored; the status codes are in
[unsupported system query options](unsupported-query-options.md).

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
> spelling.

The OpenAPI/Swagger companion packages (`OhData.AspNetCore.OpenApi`, `.NSwag`, `.Swashbuckle`)
follow this same policy: generated schema property names match the wire casing exactly — PascalCase
by default, camelCase when you opt in — instead of the host `HttpJsonOptions` casing the underlying
generators would otherwise use. A `[JsonPropertyName]` rename still wins, in the schema and on the
wire alike. So the generated document (and any client code generated from it) agrees with what
responses actually emit.

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

> **⚠ A path through a navigation requires that navigation to be in the queryable.** The predicate
> is composed onto whatever `GetQueryable` returns, so if that source projects a DTO whose
> navigation is served by a `batchGetAll` delegate rather than by the projection, there is nothing
> for the provider to translate `Lines/any(...)` against. That answers
> `400 Bad Request` (`InvalidQueryOption`, *"could not be translated by the underlying data
> provider"*) - `400` rather than `501` because a different `GetQueryable` **would** serve it, which
> is the `501`-vs-`400` test this framework applies everywhere: could any setting on the profile
> make this same request succeed on this same route? The client's remedy is to filter on a member
> the source exposes.
>
> Two shapes are deliberately left at `500`, because the framework cannot attribute the failure to
> the request: a **Priority-1** profile (which composes the whole query itself, so nothing the
> framework added can be blamed), and a source that cannot translate on its own before any option
> is applied - a server misconfiguration rather than a bad request.

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

**Priority-1 carries its continuation offset in a framework-private custom query option (`ohdata-skiptoken`), which the framework applies itself.** It is not `$skiptoken` - `ODataQueryOptions.ApplyTo` throws on a `$skiptoken` it has no handler for, which would break every profile that calls it. And it is deliberately no longer `$skip`: the framework emitted a `$skip` link but never applied it, leaving the skip entirely to the profile's own `ApplyTo`, so a profile that did **not** re-apply the incoming options served the identical first page forever and a client walking `@odata.nextLink` never terminated. The framework now applies its own offset on top of whatever the profile returns, so the continuation is correct whether or not the profile honors the standard options; the client's own `$skip` rides along verbatim on every hop and is re-applied by the profile (or not) identically each time, so there is no double-skip either way. Treat `@odata.nextLink` as opaque (§11.2.5.7) - the option name is an implementation detail. A profile that sets `ODataQueryResult.NextLink` itself is trusted to be paging on its own terms, and the framework does not add or override the cap in that case.

**A page that is exactly `MaxTop`/`maxpagesize` long is not assumed to have more behind it.** On `GetQueryable` and Priority-1 the framework fetches one row *past* the page and emits `@odata.nextLink` only if that probe row actually came back, then discards it. This costs no extra round-trip (it is the same single query, one row wider) and leaves `@odata.count` untouched - that is computed separately, pre-paging. Previously a collection whose row count was an exact multiple of the page size ended every walk with one spurious empty page. `GetAll` never had the problem: it already materializes its source and compares against the pre-paging total.

**A collection-returning bound FUNCTION is bounded the same way.** A bound (or entity-bound)
function whose result is a collection of the entity set's own type used to bypass `MaxTop`, the
client's `$top`/`$skip` and server-driven paging entirely - so the ceiling enforced on every route
above was fully bypassable through any such operation, and a `$top` sent against one was neither
applied nor rejected. It now follows the `GetAll` rules exactly (in-memory `Skip()`/`Take()` over the
materialized result, `$skip` continuation, `MaxTop` capping an explicit `$top` with the same message,
`Prefer: maxpagesize` honoured, `MaxTop = null` to opt out). **Breaking** for a function returning
more than `MaxTop` entities. Bound and unbound **actions** are excluded - see
[Bound operations](bound-operations.md#a-collection-returning-function-is-paged-like-any-other-collection).
No other system query option is applied to an operation result.

**`GetAll` now mirrors the "omitted `$top`" behavior above.** An omitted `$top` is capped to `MaxTop` (or a smaller `Prefer: maxpagesize`) with a `@odata.nextLink` for the remainder, so this path is safe-by-default like the others. The one difference from `GetQueryable` is the continuation shape: `GetAll` emits a `$skip` link (which it re-applies against its re-enumerated source) rather than the opaque `$skiptoken`. Because it has the pre-paging total in hand, it emits a link only when rows actually remain. Set `MaxTop = null` on the profile to opt out and return the full set in one response, however large - see the `GetAll` section above.

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
> **`$search` is `501` on `/$count` (`GET /odata/Products/$count?$search=alpha`).** Until 1.7.0
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
> See [Unsupported system query options are rejected](unsupported-query-options.md).

Behaviour depends on the handler path:

| Handler | `$count=true` behaviour |
|---|---|
| `GetODataQueryable` | Uses `TotalCount` from `ODataQueryResult<TModel>`. If not supplied **and the request paged** (`$top`, `$skip`, a framework continuation, or a profile-supplied `NextLink`), the total is unknowable on this path — the profile applied the paging and the pre-paging source never reached the framework — so the request fails with `500` rather than reporting the page length as the total ([#379](https://github.com/en-gen/OhData/issues/379); it silently reported the page length through 1.7.0). With no paging, `items` **is** the filtered set and its length is still used. Set `TotalCount` to the pre-paging total, or remove `OhDataSystemQueryOption.Count` from `HonouredQueryOptions` so `$count` is refused with `501` instead. |
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

### Projection pushdown
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

Enabled via `ExpandEnabled = true`. It has its own page — see
**[`$expand` and nested paging](expand.md)** for the option, the SQL pushdown that serves it, nested
options, `$levels`, server-driven nested paging, and the complexity ceilings.

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

**The `Search` handler belongs to those two paths only, and a `Priority-1` profile that sets one is refused at startup.** Both compositions above work the same way — the handler *replaces the source*, and the framework then applies the remaining options on top — and that is only possible where the framework owns the pipeline. `GetODataQueryable` inverts the contract: the profile receives the whole `ODataQueryOptions<TModel>` and applies them itself, so there is nowhere to feed a search-derived source in. Honouring `$search` there would mean bypassing the profile outright, which would drop `$filter`/`$orderby` on exactly the requests that carry `$search`, and route around whatever row-level scoping the handler applies. So `$search` on the Priority-1 path is the profile's own business, reachable as `options.Search` inside `GetODataQueryable` exactly like every other option it is handed — and a `Search` handler beside it is dead configuration, refused with an `InvalidOperationException` from `MapOhData()` rather than silently ignored. It used to be silently ignored *and* advertised in the generated OpenAPI description.

---

## `$skiptoken` (server-driven paging)

When a response includes `@odata.nextLink` (emitted once the page size reaches `MaxTop` or the client-requested `maxpagesize`), the link contains a `$skiptoken` value:

```
GET /odata/Products?$top=20
→ "@odata.nextLink": "https://host/odata/Products?$top=20&$skiptoken=MjA="
```

**`$skiptoken` is a Base64-encoded raw 4-byte little-endian integer - the literal skip offset - not an opaque or cryptographically-protected cursor.** A client (or anyone who intercepts a link) can trivially decode, predict, or forge a token to jump to an arbitrary offset; it provides no more protection than sending `$skip` directly. Don't rely on it to gate access to specific pages or ranges of data - apply authorization/filtering in the handler itself if that matters.

A malformed or corrupted `$skiptoken` (wrong length, invalid Base64) returns `400 Bad Request` (`InvalidSkipToken`). If both `$skip` and `$skiptoken` are present, `$skip` takes precedence - and, an explicit client `$skip` is carried into the emitted token, so paging that starts at a non-zero offset advances from there instead of rewinding to it.

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
