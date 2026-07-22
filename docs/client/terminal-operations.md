# Terminal operations

Part of the [OhData.Client guide](index.md). These methods execute a query built with the [querying](querying.md) builders and materialise the result.

## `ToListAsync`

Returns all matching items as a `List<T>`:

```csharp
List<Product> items = await client.For<Product>()
    .Filter(x => x.Price > 5)
    .OrderBy(x => x.Name)
    .ToListAsync();
```

## `ToPageAsync`

Returns items plus the total count (forces `$count=true`):

```csharp
ODataPage<Product> page = await client.For<Product>()
    .OrderBy(x => x.Id)
    .Top(20).Skip(0)
    .ToPageAsync();

Console.WriteLine($"Page 1 of {Math.Ceiling((double)page.TotalCount! / 20)}");
foreach (var p in page.Items) { ... }
```

`ODataPage<T>` has:
- `Items` — the entities on this page
- `TotalCount` — total matching entities (pre-pagination), `null` if the server didn't return `@odata.count`
- `NextLink` — the URL to follow for the next page of results (server-driven pagination), `null` when there are no more pages

**Server-driven pagination.** When the server enforces a page size via `MaxTop`, it includes `@odata.nextLink` in the response. Inspect `NextLink` to determine whether more pages exist. To follow the link, issue a new request using the URL from `NextLink` directly (it is a fully-qualified absolute URL).

## `FirstOrDefaultAsync`

Returns the first match or `null`. Applies `$top=1` automatically:

```csharp
Product? cheapest = await client.For<Product>()
    .OrderBy(x => x.Price)
    .FirstOrDefaultAsync();
```

## `CountAsync`

Hits `GET /{EntitySet}/$count` - returns the total count as a `long`:

```csharp
long count = await client.For<Product>()
    .Filter(x => x.Price < 5)
    .CountAsync();
```

## `AnyAsync`

Returns `true` if at least one entity matches:

```csharp
bool hasStock = await client.For<Product>()
    .Filter(x => x.StockLevel > 0)
    .AnyAsync();
```

## `FirstAsync`

Returns the first match. Applies `$top=1` automatically. Throws `InvalidOperationException` when the collection is empty (use `FirstOrDefaultAsync` if no results is a valid outcome):

```csharp
Product cheapest = await client.For<Product>()
    .OrderBy(x => x.Price)
    .FirstAsync();
```

## `SingleOrDefaultAsync`

Returns the single matching entity, or `null` when none match. Applies `$top=2` and throws `InvalidOperationException` when more than one entity matches:

```csharp
Product? active = await client.For<Product>()
    .Filter(x => x.Sku == "ABC-1")
    .SingleOrDefaultAsync();
```

## `SingleAsync`

Returns the single matching entity. Throws `InvalidOperationException` when zero or more than one entity matches:

```csharp
Product product = await client.For<Product>()
    .Filter(x => x.Sku == "ABC-1")
    .SingleAsync();
```

## `ToArrayAsync`

Returns all matching items as a `T[]`:

```csharp
Product[] items = await client.For<Product>()
    .Filter(x => x.IsActive)
    .ToArrayAsync();
```

---

Next: [Single-entity operations →](single-entity.md)
