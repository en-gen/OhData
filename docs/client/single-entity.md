# Single-entity operations

Part of the [OhData.Client guide](index.md).

`Key(value)` returns a `KeyedEntitySetClient<T>` scoped to a specific entity:

```csharp
var keyed = client.For<Product>().Key(42);
```

Key values are formatted as OData literals automatically:
- `int`/`long` → `Products(42)`
- `string` → `Products('value')`, single quotes escaped
- `Guid` → `Products(3f2504e0-...)`
- `DateTime`/`DateTimeOffset` → the same full-precision, `Z`/offset-suffixed literal `$filter`
  uses (see [Literal type support](errors-and-types.md#literal-type-support)) — a key built from an entity's
  actual (sub-second-precision) key value now always round-trips back to the same entity instead
  of being silently truncated to whole seconds

An OhData server (from `1.0.0` on) builds its own response entity-id URLs
(`Location`/`Content-Location`/`OData-EntityId`/`@odata.id`) the same way: string keys are
single-quoted and percent-encoded, with embedded quotes doubled, so a `string`-keyed entity's
`Location` header can always be fed straight back into `Key(...)`/parsed by
`ODataKeyParser` without adjustment.

A generic overload provides compile-time type safety for the key value:

```csharp
var keyed = client.For<Product>().Key<int>(42);
```

## Get

```csharp
// Returns null on 404
Product? product = await client.For<Product>().Key(42).GetAsync();
```

## Get with ETag

Retrieves the entity and the server's current ETag in one call. Pass the ETag to `PutAsync` or `PatchAsync` for optimistic concurrency:

```csharp
var (product, etag) = await client.For<Product>().Key(42).GetWithETagAsync();

// Later — fail with 412 if another client modified the entity in between
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(new Product { Id = product!.Id, Name = product.Name, Price = 5.49m }, ifMatch: etag);
```

## Conditional GET with If-None-Match

`GetIfChangedAsync` sends a previously-observed ETag as `If-None-Match` (RFC 7232 §3.2 / OData
§8.2.5) and distinguishes a server-confirmed `304 Not Modified` from a fresh `200 OK`
representation, so you can skip re-deserializing data that hasn't changed:

```csharp
Task<(T? Entity, string? ETag, bool NotModified)> GetIfChangedAsync(
    string? ifNoneMatch = null, CancellationToken ct = default);
```

```csharp
var (product, etag, _) = await client.For<Product>().Key(42).GetIfChangedAsync();

// ... later, using the previously-observed etag ...
var (fresh, currentEtag, notModified) = await client.For<Product>().Key(42).GetIfChangedAsync(etag);

if (notModified)
{
    // Server returned 304 - fresh is null, currentEtag echoes the server's current value.
    // The cached `product` from the earlier call is still current.
}
else
{
    // Server returned 200 - fresh holds the up-to-date entity, currentEtag its new ETag.
    product = fresh;
}
```

Passing `ifNoneMatch: null` (the default) sends no conditional header and behaves like
`GetWithETagAsync` — `NotModified` is always `false` in that case. When the entity does not exist,
behavior matches `GetAsync`/`GetWithETagAsync`: returns `(null, null, false)`, or throws
`ODataClientException` with status `404` when `OhDataClientOptions.NotFoundBehavior` is `Throw`.
See [etags.md](../etags.md#conditional-reads) for the server-side behavior this pairs with.

## Insert (POST)

```csharp
// Returns the created entity with server-assigned values (e.g. generated Id)
Product created = await client.For<Product>()
    .InsertAsync(new Product { Name = "Cog", Price = 4.99m });
```

Pass `preferMinimal: true` to send `Prefer: return=minimal` and receive `204 No Content` instead of the full entity body:

```csharp
// Returns null when the server honours Prefer: return=minimal
Product? result = await client.For<Product>()
    .InsertAsync(new Product { Name = "Cog", Price = 4.99m }, preferMinimal: true);
```

## Replace (PUT)

```csharp
// Returns the updated entity, or null if the server responds 204 No Content
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(product with { Price = 5.49m });
```

Optional parameters:

| Parameter | Type | Description |
|-----------|------|-------------|
| `ifMatch` | `string?` | If-Match ETag value. Returns `412 Precondition Failed` if the server's current ETag does not match. |
| `preferMinimal` | `bool` | Send `Prefer: return=minimal`; server may respond with `204 No Content` (method returns `null`). |

The `ifMatch`/`ifNoneMatch` values are normalised to RFC 7232 entity-tag syntax on the wire:
an unquoted value (including the unquoted ETag that `GetWithETagAsync` returns) is wrapped in
double quotes, an already-quoted or `W/`-prefixed value is left intact, and `*` passes through
unchanged — so you can pass either form without double-quoting.

```csharp
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(product with { Price = 5.49m }, ifMatch: etag, preferMinimal: false);
```

## Partial update (PATCH)

Pass an anonymous object with only the properties to change:

```csharp
Product? patched = await client.For<Product>().Key(42)
    .PatchAsync(new { Name = "Cog v2", Price = 5.99m });
```

The same optional `ifMatch` and `preferMinimal` parameters as `PutAsync` are available:

```csharp
Product? patched = await client.For<Product>().Key(42)
    .PatchAsync(new { Price = 5.99m }, ifMatch: etag);
```

## Delete

```csharp
// Throws ODataClientException on 404
await client.For<Product>().Key(42).DeleteAsync();
```

Pass an ETag to enforce optimistic concurrency — returns `412 Precondition Failed` if the entity has been modified:

```csharp
await client.For<Product>().Key(42).DeleteAsync(ifMatch: etag);
```

---

Next: [Error handling & literal types →](errors-and-types.md)
