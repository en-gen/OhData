# ETags and Optimistic Concurrency

OhData supports HTTP ETags for optimistic concurrency control. Opt in per entity set by calling `UseETag` inside the profile constructor.

## Setup

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        // Hash one or more properties into the ETag
        UseETag(x => x.RowVersion);   // byte[] row-version column

        GetById = (id, ct) => ...;
        Put     = (id, product, ct) => ...;  // If-Match checked before proceeding
        Patch   = (id, product, ct) => ...;  // same
        Delete  = (id, ct) => ...;           // same
    }
}
```

`UseETag` accepts one or more property selectors. The framework SHA-256 hashes their values and Base64-encodes the result. Binary buffers are hashed directly (ideal for SQL row-version columns) - `byte[]`, `ImmutableArray<byte>`, `ReadOnlyMemory<byte>`, `Memory<byte>` and `ArraySegment<byte>` are all treated identically. Every other value is hashed as its UTF-8 string representation, formatted as described below.

Hash multiple fields together - the ETag changes if any of them changes:

```csharp
UseETag(x => x.Name, x => x.Price, x => x.UpdatedAt);
```

### Which selector types are allowed

`MapOhData()` throws `InvalidOperationException` if a `UseETag` selector returns a type the hash
cannot faithfully represent. Supported types are:

- a binary buffer - `byte[]`, `ImmutableArray<byte>`, `ReadOnlyMemory<byte>`, `Memory<byte>`,
  `ArraySegment<byte>`
- `string`, `bool`, an enum, or any type implementing `IFormattable` (which covers every numeric
  type, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid` and `char`)
- a `Nullable<T>` of any of the above

Anything else - a navigation property, an entity reference, a `List<T>`, a POCO, `object` - is
rejected. The reason is that such a type usually has no `ToString()` override, so it would format
to its own *type name*: the same string for every row, giving every entity in the set one shared
ETag and turning `If-Match` into a check that always passes. Nothing in any response reveals that;
the only symptom is a lost update, so it fails at startup instead. The fix is always to select a
scalar projection - `x => x.Related.Id`, `x => x.Related.RowVersion`.

The check sees only the *declared* type. A selector declared as `IFormattable` (or any base type)
is accepted, and it is then the type's responsibility to render culture-independently.

### Selectors that close over profile state

A selector that reads only its lambda parameter (`x => x.RowVersion`) is compiled **once per
process** and shared by every request-scoped instance of the profile.

A selector that closes over anything else — a field of the profile, an injected dependency, a
captured local — is compiled **per profile instance** instead. That costs one
`Expression.Compile()` per request for that entity set, and it is what makes the selector correct:
sharing it would freeze whatever the *startup-scope* instance captured (a `DbContext` resolved in a
scope disposed immediately after registration) into every later request. Nothing is rejected and no
configuration changes; only the caching does. The same rule applies to the key selector passed to
the profile's base constructor.

### How values are formatted

Non-binary values are formatted **round-trippably and under `InvariantCulture`** before hashing,
so the ETag is a faithful function of the entity state and nothing else:

| Value type | Formatting | Why |
|---|---|---|
| `DateTime` (`Utc`, `Unspecified`) | `"O"` (ISO-8601 round-trip) | Keeps all seven fractional-second digits, plus the `Z`/no-suffix that discriminates the two Kinds. |
| `DateTime` (`Local`) | `"O"` with the offset suppressed, plus a Kind marker | See the note below - `"O"` would append the *server's* UTC offset. |
| `DateTimeOffset` | `"O"` | Full sub-second precision plus the value's own offset. A `DateTimeOffset.UtcNow` timestamp changes the ETag even when two writes land in the same second. |
| `DateOnly`, `TimeOnly` | `"O"` | `TimeOnly`'s general format drops seconds as well as the fraction. |
| `TimeSpan` | `"c"` | Full tick precision. `"O"` is not a valid `TimeSpan` specifier - it throws. |
| `Guid` | `"D"` | Canonical hyphenated form. `"O"` is not a valid `Guid` specifier - it throws. |
| `float`, `double` | invariant, default | The shortest *round-trippable* form - two values that differ by one bit hash differently. |
| `decimal` | invariant, default | Exact, and preserves scale (`1.50m` differs from `1.5m`). |
| integers, `char`, enums | invariant, default | Exact by construction; invariant culture pins the sign character, which differs in some locales. |
| `string` | as-is | |
| `bool` | `ToString()` | `bool` does not implement `IFormattable`; its `ToString()` ignores any format provider and always yields `True`/`False`. |
| anything else | `IFormattable` under invariant culture, else `ToString()` | Reachable only for a selector declared as a base type or interface - see the allowlist above. |

Invariance is what lets a `de-DE` and an `en-US` server behind the same load balancer agree on the
ETag for identical entity state. Values are additionally length-prefixed and tagged with their CLR
type before hashing, so adjacent properties cannot be reinterpreted across the boundary
(`("ab","c")` vs `("a","bc")`), `null` never hashes the same as `""`, and the string `"1"` never
collides with the integer `1`.

Two consequences worth knowing about:

- **`DateTime` with `Kind == Local` is hashed by its wall-clock reading, not its instant.** The
  round-trip `"O"` format appends `TimeZoneInfo.Local`'s offset for a `Local` value, which would
  make the ETag a function of the *server's* timezone configuration: a client reading from a
  `TZ=UTC` node and writing to a `TZ=America/Chicago` node would get `412` forever, and a tzdata
  update that changes a future DST rule would rotate every outstanding ETag. So the offset is
  suppressed and the `DateTimeKind` is recorded instead - the value stays lossless and
  machine-independent. (Storing UTC, as `DateTimeOffset.UtcNow` or `DateTime.UtcNow` does, sidesteps
  the question entirely and remains the recommendation.)
- **Two `DateTimeOffset` values that are `==` can have different ETags.** `DateTimeOffset.Equals`
  compares instants, so `10:00Z` equals `12:00+02:00` - but they are different representations and
  serialize differently, so they hash differently. Normalize (`.ToUniversalTime()`) in your model if
  you need offset-insensitive comparison.

The type discriminator is derived from type *names* only, never assembly identity, so the `net8.0`
and `net10.0` builds of the package produce the same ETag for the same data and an application
version bump does not rotate anything. Renaming or moving a type that appears in an ETag selector
*does* change that entity set's ETags - treat it like any other representation change.

> **Upgrading:** these rules changed in the release noted in the [CHANGELOG](https://github.com/en-gen/OhData/blob/develop/CHANGELOG.md), and
> every previously-issued ETag value changes with them. Clients holding an older ETag get a `412`
> on a conditional write (or a full `200` instead of `304` on a conditional read) and re-fetch -
> the safe direction. No configuration is involved and no ETag is comparable across the upgrade.

## Response headers

When `UseETag` is configured, the `ETag` response header is added to:

| Operation | Status | Header |
|-----------|--------|--------|
| `GET /{EntitySet}({key})` | 200 | `ETag: "dGVzdA=="` |
| `POST /{EntitySet}` | 201 | `ETag: "..."` |
| `PUT /{EntitySet}({key})` | 200 | `ETag: "..."` |
| `PATCH /{EntitySet}({key})` | 200 | `ETag: "..."` |

The ETag value is double-quoted per the HTTP spec: `"<base64-sha256>"`.

The `@odata.etag` annotation is also included in the response body for each entity.

## Conditional write operations

### Which routes honor `If-Match`

Every state-changing route the framework owns **and can key** evaluates the precondition:

| Route | |
|---|---|
| `PUT /{EntitySet}({key})` | replace the entity |
| `PATCH /{EntitySet}({key})` | merge the entity |
| `DELETE /{EntitySet}({key})` | delete the entity |
| `PUT` / `PATCH` / `DELETE /{EntitySet}({key})/{Property}` | [structural-property writes](property-access.md) |
| `POST /{EntitySet}({key})/{Nav}/$ref` | add a link to a collection navigation |
| `PUT /{EntitySet}({key})/{Nav}/$ref` | set a single-valued navigation's link |
| `DELETE /{EntitySet}({key})/{Nav}/$ref` | remove a link |
| `POST /{EntitySet}({key})/{Nav}` | create a related entity |
| `POST /{EntitySet}({key})/{Action}` | invoke an entity-bound action |

The four `$ref` / navigation-`POST` rows are new; before that, those routes silently **discarded**
a received `If-Match` and performed the write with a `204`/`201`. That was a lost update on
relationship state which the handler could not prevent - the `addRef`/`setRef`/`removeRef`/`post`
delegates receive only the key and the payload, so there was nowhere to check the header.

The entity-bound **action** row is newer still (#566) and closed a MUST violation; see
[the remaining exclusion below](#collection-bound-and-unbound-actions-are-still-excluded) for the
two families that genuinely cannot be keyed.

### How the check works

If the request includes an `If-Match` header:

1. The framework fetches the current entity via `GetById`
2. If no entity exists at that key, returns `412 Precondition Failed` immediately (RFC 7232 §3.1 /
   Protocol §11.4.1.1 - a missing resource never satisfies `If-Match`, not even `*`) - it does
   **not** fall through to whatever `404` the operation would otherwise produce for a missing key
3. Computes the current ETag
4. Checks whether it appears in the `If-Match` list (comma-separated ETags per RFC 7232)
5. Returns `412 Precondition Failed` if no match; proceeds if matched

`If-Match: *` matches any *existing* representation - it still fails with `412` (not `404`) when
the resource does not exist.

When the precondition fails, it fails **before** the route's handler delegate runs and before the
request body is deserialized, so a refused write never mutates anything and never reaches your code.

### Weak validators are rejected by `If-Match`

RFC 9110 §13.1.1 requires **strong** comparison for `If-Match`, and §8.8.3.2 says a weak validator
can never participate in one. So `If-Match: W/"<current-etag>"` returns `412` even though the
unwrapped value is the current ETag. A weak entry in a comma-separated list is dropped rather than
poisoning the list - `If-Match: W/"x", "<current>"` still succeeds on the strong entry.

`If-None-Match` uses **weak** comparison (§13.1.2), so `W/"<current>"` *does* match there. The two
headers deliberately behave differently on the same input.

OhData never emits a weak ETag, so this only affects a client that constructs one itself.

> **Migrating from `Microsoft.AspNetCore.OData`?** This is a deliberate difference. MS emits weak
> ETags unconditionally (`DefaultODataETagHandler` constructs every tag with `isWeak: true`) and
> then compares them ignoring weakness, so `If-Match: W/"..."` is accepted there. A client that
> was written against an MS OData server, or that echoes back a `W/`-prefixed tag it received from
> one, will get `412` from OhData. Strip the `W/` prefix, or better, send back exactly the `ETag`
> header value OhData gave you.

### `If-None-Match` on a write

`If-None-Match` is honored on the same routes as `If-Match` and means the inverse (RFC 9110
§13.1.2): the request is refused with `412 Precondition Failed` when a listed validator matches
the current ETag, or when `*` is given and the entity exists. It proceeds when nothing matches or
the entity does not exist.

When a request carries **both** headers, `If-Match` wins outright and `If-None-Match` is not
evaluated at all - RFC 9110 §13.2.2 fixes that order. Two headers naming the current ETag is a
success, not a contradiction.

#### `If-None-Match: *` as a create-guard on `PUT`

When `AllowUpsert` is enabled, `PUT` also honors `If-None-Match: *` as a create-guard (§11.4.4):
if the entity already exists at the target key, the request fails with `412 Precondition Failed`
instead of overwriting it; otherwise the `PUT` proceeds as an insert. This is a no-op when the
header is absent, and is independent of the `If-Match` handling above. Unlike the general
`If-None-Match` handling, this guard does not require `UseETag` - it only asks whether a
representation exists.

### Collection-bound and unbound actions are still excluded

An **entity-bound** action (`POST /{EntitySet}({key})/{Action}`) honours `If-Match` and
`If-None-Match` like every other keyed write. **Collection-bound** actions
(`POST /{EntitySet}/{Action}`) and **unbound** actions (`POST /{Action}`) do not, and cannot: there
is no `{key}` segment and no addressed entity, so there is nothing to load an entity tag from.
§11.5.4.1's *"or collection of entities"* half would need a **collection** ETag, which this
framework does not compute.

For those two families, a conditional header is ignored and the action runs.

> **History, recorded so the exclusion is not reinstated.** Until 2.0.0, *entity-bound* actions
> were excluded too, defended by the claim that an action-invocation resource *"has no
> representation and therefore no entity tag"*, citing Protocol §11.5.4. **That phrase does not
> appear anywhere in Part 1** — `grep -ic "no representation"` over the specification returns `0` —
> and four clauses say the opposite:
>
> - **§11.4.1.1** (a MUST): *"If an ETag value is specified in an `If-Match` or `If-None-Match`
>   header of a Data Modification Request **or Action Request**, the operation MUST only be invoked
>   if the if-match or if-none-match condition is satisfied."*
> - **§8.2.4**: a mismatched `If-Match` *"for a Data Modification Request **or Action Request**"*
>   MUST answer `412` and MUST ensure no observable change occurs.
> - **§8.3.1**: the `ETag` value may be used *"in updating, deleting, **or invoking the action
>   bound to the entity**."*
> - **§11.5.4.1** instructs the client to send it: *"To request processing of the action only if
>   the binding parameter value … is unmodified, the client includes the `If-Match` header."*
>
> Measured on the TestBench before the fix: `POST /v1/Movies(3)/Rate` with a stale `If-Match`
> answered `200` and mutated the entity, while `PATCH /v1/Movies(3)` carrying the **same header**
> answered `412`.

If you need a collection-bound or unbound action to be conditional, you have to implement it
yourself. An action handler does not receive `HttpContext` — its parameters are bound from the
request body and query string — so reaching the header means injecting `IHttpContextAccessor` into
the profile (profiles are registered scoped, so this works once the app calls
`AddHttpContextAccessor()`):

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    private readonly IHttpContextAccessor _http;

    public ProductProfile(IHttpContextAccessor http) : base(x => x.Id)
    {
        _http = http;
        BindAction(ReorderAll);       // collection-bound: no {key}, so no server-side gate
    }

    private async Task ReorderAll(int quantity, CancellationToken ct)
    {
        var ifMatch = _http.HttpContext?.Request.Headers.IfMatch;
        // ... compare against whatever your own concurrency token is, and fail with your own 412.
    }
}
```

```http
PUT /odata/Products(1)
If-Match: "dGVzdA=="
Content-Type: application/json

{ "id": 1, "name": "Updated Widget", "price": 12.99 }
```

**`GetById` must be configured for If-Match checking to work on write operations.**

## Conditional reads

On `GET /{EntitySet}({key})`, if the request includes an `If-None-Match` header:

- If the current ETag matches any value in `If-None-Match`, returns `304 Not Modified` (no body)
- Otherwise proceeds normally and returns the full entity

This lets clients avoid re-downloading unchanged data.

`If-None-Match` is also honored on [individual property reads](property-access.md#etags)
(`GET /{EntitySet}({key})/{Property}`) when `UseETag` is configured - a match returns
`304 Not Modified` with the same `ETag` header the entity-level `GET` would produce.
`GET .../{Property}/$value` does not set or check an ETag.

## Client-side ETag support

`OhData.Client` exposes ETag-aware methods on `KeyedEntitySetClient<T>`.

### Fetch entity with ETag

```csharp
var (product, etag) = await client.For<Product>().Key(42).GetWithETagAsync();
```

Returns a `(T? Entity, string? ETag)` tuple. `ETag` is the raw header value (double-quoted, e.g. `"dGVzdA=="`), or `null` if the server did not send an `ETag` header.

### Conditional GET with `If-None-Match`

`GetIfChangedAsync` sends a previously-observed ETag as `If-None-Match` and tells you whether the
server confirmed `304 Not Modified` or returned a fresh representation - useful for cache
invalidation without re-fetching and re-deserializing data you already have:

```csharp
var (product, etag, _) = await client.For<Product>().Key(42).GetIfChangedAsync();

// ... later, using the cached etag ...
var (fresh, currentEtag, notModified) = await client.For<Product>().Key(42).GetIfChangedAsync(etag);
if (!notModified)
{
    product = fresh;   // server sent a new representation; currentEtag is its ETag
}
```

See [the client guide](client/single-entity.md#conditional-get-with-if-none-match) for the full return-tuple semantics.

### Conditional write operations

Pass the ETag as `ifMatch` to `PutAsync`, `PatchAsync`, or `DeleteAsync`. The server returns `412 Precondition Failed` if the entity has been modified since the ETag was fetched:

```csharp
// Fetch with ETag
var (product, etag) = await client.For<Product>().Key(42).GetWithETagAsync();

// Replace — fails with 412 if another client modified the entity
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(new Product { Id = product!.Id, Name = product.Name, Price = 9.99m }, ifMatch: etag);

// Partial update
Product? patched = await client.For<Product>().Key(42)
    .PatchAsync(new { Price = 9.99m }, ifMatch: etag);

// Delete
await client.For<Product>().Key(42).DeleteAsync(ifMatch: etag);
```

Pass `"*"` as `ifMatch` to skip the ETag check (match any current entity):

```csharp
await client.For<Product>().Key(42).DeleteAsync(ifMatch: "\"*\"");
```

---

## Concurrency note

The ETag check is a best-effort conflict signal, not an atomic operation. The framework fetches the entity in one database call, then the caller performs the write in a separate operation - another request may modify the entity between those two steps. For true atomic optimistic concurrency, use a database-level mechanism (e.g. SQL `WHERE RowVersion = @expected`) inside the handler itself and return `null` / throw on conflict.

This applies **unchanged** to the `$ref` and navigation-`POST` routes. Bringing them under the
precondition gate closed the case where the header was ignored outright; it did not make them
atomic, and the framework opens no transaction around them. A concurrent writer can still land
between the `GetById` the check performs and the `addRef`/`setRef`/`removeRef`/`post` delegate's
own write. What you get is the same guarantee the entity routes have always had: a *stale* ETag is
reliably refused, and a refused request provably never reaches your delegate. What you do not get
is serialization of the surviving window - that still has to come from the data store.

## Example: SQL row-version column

```csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

// Profile:
UseETag(x => x.RowVersion);
```

EF Core updates `RowVersion` automatically on every `SaveChanges`. The ETag changes on every write.
