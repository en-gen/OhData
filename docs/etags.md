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
2. Computes the current ETag
3. Checks whether it appears in the `If-Match` list (comma-separated ETags per RFC 7232)
4. Returns `412 Precondition Failed` if no match; proceeds if matched

`If-Match: *` always passes (any current entity matches).

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
