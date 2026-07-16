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

`UseETag` accepts one or more property selectors. The framework SHA-256 hashes their values and Base64-encodes the result. `byte[]` properties are hashed directly (ideal for SQL row-version columns); all other values are hashed as their UTF-8 string representations.

Hash multiple fields together - the ETag changes if any of them changes:

```csharp
UseETag(x => x.Name, x => x.Price, x => x.UpdatedAt);
```

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

See [client.md](client.md#conditional-get-with-if-none-match) for the full return-tuple semantics.

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
