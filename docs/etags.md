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

> **Upgrading:** these rules changed in the release noted in the [CHANGELOG](../CHANGELOG.md), and
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

On `PUT`, `PATCH`, and `DELETE`, if the request includes an `If-Match` header:

1. The framework fetches the current entity via `GetById`
2. If no entity exists at that key, returns `412 Precondition Failed` immediately (RFC 7232 §3.1 /
   Protocol §11.4.1.1 - a missing resource never satisfies `If-Match`, not even `*`) - it does
   **not** fall through to whatever `404` the operation would otherwise produce for a missing key
3. Computes the current ETag
4. Checks whether it appears in the `If-Match` list (comma-separated ETags per RFC 7232)
5. Returns `412 Precondition Failed` if no match; proceeds if matched

`If-Match: *` matches any *existing* representation - it still fails with `412` (not `404`) when
the resource does not exist.

### `If-None-Match: *` as a create-guard on `PUT`

When `AllowUpsert` is enabled, `PUT` also honors `If-None-Match: *` as a create-guard (§11.4.4):
if the entity already exists at the target key, the request fails with `412 Precondition Failed`
instead of overwriting it; otherwise the `PUT` proceeds as an insert. This is a no-op when the
header is absent, and is independent of the `If-Match` handling above.

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
