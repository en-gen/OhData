# ETags and Optimistic Concurrency

OhData supports HTTP ETags for optimistic concurrency control via `GetETag` on the profile.

## Setup

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        // Opt in by providing an ETag computation function
        GetETag = product => Convert.ToBase64String(product.RowVersion);

        GetById  = (id, ct) => ...;
        PutById  = (id, product, ct) => ...;
        Delete   = (id, ct) => ...;
    }
}
```

`GetETag` is `Func<TModel, string>?`. Setting it opts in to ETag behavior for that entity set.

## Response headers

When `GetETag` is set, the framework adds an `ETag` header to:
- `GET /{EntitySet}({key})` — 200 response
- `POST /{EntitySet}` — 201 response
- `PUT /{EntitySet}({key})` — 200 response
- `PATCH /{EntitySet}({key})` — 200 response

The ETag value is `"<result of GetETag>"` (double-quoted per HTTP spec).

## `If-Match` checking on write operations

On `PUT`, `PATCH`, and `DELETE`, if the request includes an `If-Match` header and `GetETag` is set, the framework:
1. Fetches the current entity via `GetById`
2. Computes the current ETag
3. Compares against the `If-Match` value (strips surrounding quotes)
4. If they differ: returns `412 Precondition Failed`
5. If they match (or `If-Match: *`): proceeds with the operation

**This requires `GetById` to be set alongside `GetETag` for write checks to work.**

```http
PUT /odata/Products(1)
If-Match: "dGVzdA=="
Content-Type: application/json

{ "name": "Updated Widget", "price": 12.99, "category": "Hardware" }
```

→ `412 Precondition Failed` if the ETag has changed since the client last read the entity.

## `If-None-Match: *` on POST

Not currently supported. The framework cannot extract the entity key from the POST body at the framework level without knowing the key property at request time. Implement this check in the `Post` handler if needed.

## Example: row version ETag

```csharp
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
}

// In profile:
GetETag = product => Convert.ToBase64String(product.RowVersion);
```

## Example: simple hash ETag

```csharp
GetETag = product =>
    Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{product.Id}|{product.Name}|{product.Price}")));
```
