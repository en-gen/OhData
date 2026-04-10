# ETags and Optimistic Concurrency

OhData supports HTTP ETags for optimistic concurrency control via `UseETag` on the profile.

## Setup

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        // Opt in by specifying the property (or properties) to hash
        UseETag(x => x.RowVersion);

        GetById  = (id, ct) => ...;
        PutById  = (id, product, ct) => ...;
        Delete   = (id, ct) => ...;
    }
}
```

`UseETag` accepts one or more property selectors. The framework SHA-256 hashes their values and Base64-encodes the result. `byte[]` properties (e.g. row-version columns) are hashed directly; all other values are hashed as their UTF-8 string representations.

## Multiple properties

```csharp
// Hash several fields together
UseETag(x => x.Name, x => x.Price, x => x.UpdatedAt);
```

## Response headers

When `UseETag` is configured, the framework adds an `ETag` header to:
- `GET /{EntitySet}({key})` — 200 response
- `POST /{EntitySet}` — 201 response
- `PUT /{EntitySet}({key})` — 200 response
- `PATCH /{EntitySet}({key})` — 200 response

The ETag value is `"<base64-sha256>"` (double-quoted per HTTP spec).

## `If-Match` checking on write operations

On `PUT`, `PATCH`, and `DELETE`, if the request includes an `If-Match` header and `UseETag` is configured, the framework:
1. Fetches the current entity via `GetById`
2. Computes the current ETag
3. Compares against the `If-Match` value (strips surrounding quotes)
4. If they differ: returns `412 Precondition Failed`
5. If they match (or `If-Match: *`): proceeds with the operation

**This requires `GetById` to be set alongside `UseETag` for write checks to work.**

```http
PUT /odata/Products(1)
If-Match: "dGVzdA=="
Content-Type: application/json

{ "name": "Updated Widget", "price": 12.99, "category": "Hardware" }
```

→ `412 Precondition Failed` if the ETag has changed since the client last read the entity.

## `If-None-Match: *` on POST

Not currently supported. The framework cannot extract the entity key from the POST body at the framework level without knowing the key property at request time. Implement this check in the `Post` handler if needed.

## Advanced: custom ETag function

For cases that cannot be expressed as property selectors, set `GetETag` directly:

```csharp
GetETag = product => MyCustomHasher.Compute(product);
```

`GetETag` is `Func<TModel, string>?`. When both `UseETag` and `GetETag` are assigned, the last one set wins (they both write to the same backing field).

## Example: row version

```csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}

// In profile:
UseETag(x => x.RowVersion);
```

## Example: composite hash

```csharp
// Hash three fields together — ETag changes whenever any of them changes
UseETag(x => x.Name, x => x.Price, x => x.Category);
```
