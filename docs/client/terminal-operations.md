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

> `ODataPage<T>.NextLink` is the **collection's own** link — the next page of *this* entity set. A server can also page an **expanded** collection, which it signals with a per-entity `{Nav}@odata.nextLink` that this type does not carry. See [annotation-preserving reads](#annotation-preserving-reads) below.

## Annotation-preserving reads

`ToListAsync`, `ToPageAsync`, `ToAsyncEnumerable` and `GetAsync` bind four envelope members (`@odata.context`, `@odata.count`, `@odata.nextLink`, `value`) and **drop every other OData annotation**. System.Text.Json cannot bind an `@`-bearing member to a CLR property, so anything the server attached to an individual entity is discarded.

That is invisible until it isn't. A server configured with [`ExpandPagingEnabled`](../query-options.md#nested-server-driven-paging-expandpagingenabled-313) answers an over-large `$expand` with a **prefix** of the related collection plus a nested `{Nav}@odata.nextLink` saying so — and through the ordinary read path that truncated collection is indistinguishable from a complete one.

Three terminal operations preserve annotations instead:

| Method | On | Returns |
|---|---|---|
| `ToAnnotatedPageAsync` | `EntitySetClient<T>` | `ODataAnnotatedPage<T>` — one page |
| `ToAnnotatedAsyncEnumerable` | `EntitySetClient<T>` | `IAsyncEnumerable<ODataAnnotatedEntity<T>>` — all pages, lazily |
| [`GetAnnotatedAsync`](single-entity.md#getannotatedasync) | `KeyedEntitySetClient<T>` | `ODataAnnotatedEntity<T>?` — one entity by key |

```csharp
ODataAnnotatedPage<Author> page = await client.For<Author>()
    .Filter(a => a.Id == 1)
    .Expand(a => a.Books)
    .ToAnnotatedPageAsync();

foreach (ODataAnnotatedEntity<Author> entry in page.Entries)
{
    Uri? more = entry.NextLinkFor(a => a.Books);
    if (more is not null)
    {
        // entry.Entity.Books is a PREFIX. Follow `more` to retrieve the rest.
        Console.WriteLine($"{entry.Entity.Name}: partial ({entry.Entity.Books.Count} so far)");
    }
}
```

### `ODataAnnotatedPage<T>`

- `Entries` — the entities, each paired with its annotations (`IReadOnlyList<ODataAnnotatedEntity<T>>`)
- `Items` — the same entities without annotations, for code that does not need them
- `TotalCount` — `@odata.count`, or `null`. **See the `$count` note below.**
- `NextLink` — the collection's own `@odata.nextLink` as a `Uri?`, or `null`
- `Annotations` — envelope-level control information (`@odata.context` and the rest)

### `ODataAnnotatedEntity<T>`

- `Entity` — the deserialized entity
- `NextLinkFor(x => x.Nav)` → `Uri?` — the nested `{Nav}@odata.nextLink`. **Non-null means `Entity`'s copy of that collection is a prefix.**
- `CountFor(x => x.Nav)` → `long?` — the nested `{Nav}@odata.count`, the size of the *full* related collection
- `Annotations` — the raw set, for anything without a typed accessor

The expression accessors resolve the wire name through the client's own `PropertyNamingPolicy`, with `[JsonPropertyName]` winning over the policy — the same rule the client uses when it emits query options. Only a **direct** member access is accepted (`x => x.Books`, not a chained path); use the string overload on `Annotations` for anything else.

### `ODataEntityAnnotations`

Reached via `entry.Annotations`, and also carried on the page envelope.

- `NextLinkFor("Books")` / `CountFor("Books")` — the string-keyed forms, taking the name **as the server spells it on the wire**
- `TryGetValue("Books@odata.nextLink", out JsonElement value)` — any annotation by full wire name
- `Values` — the whole set as `JsonElement`s. A `JsonElement` is the ceiling on purpose: past `nextLink` and `count` the annotation vocabulary is open-ended (`@odata.etag`, `@odata.id`, `@Org.Example.customTerm`, …) and the client cannot guess a CLR type for it.
- `IsEmpty`

Only annotations **directly attached** to the entity are captured; the client does not walk into expanded child entities to collect theirs.

### `ToAnnotatedPageAsync` does not force `$count=true` — `ToPageAsync` does

This is the one behavioural difference between the two, and swapping one for the other **silently changes `TotalCount`**:

```csharp
await client.For<Author>().ToPageAsync();           // always sends $count=true
await client.For<Author>().ToAnnotatedPageAsync();  // sends it only if you asked

await client.For<Author>().IncludeCount().ToAnnotatedPageAsync();  // ← populates TotalCount
```

`ToPageAsync` forces the option because returning a total is its whole purpose. `ToAnnotatedPageAsync` honours the builder instead, which keeps the request answerable by a server whose `CountEnabled` is off — an unconditional `$count=true` would get a `400` there. **If you migrate a `ToPageAsync` call to `ToAnnotatedPageAsync`, add `IncludeCount()` or `TotalCount` becomes `null`.**

### Links are `Uri`, and what that costs

Every link in the annotation surface is a `Uri`: `ODataAnnotatedPage<T>.NextLink`, `NextLinkFor` on both types. The pre-existing `ODataPage<T>.NextLink` remains a `string` — it is shipped public API and changing it would break callers for no benefit. That is the one seam, and migrating `ToPageAsync` → `ToAnnotatedPageAsync` surfaces it as a **compile error**, not a silent difference.

A returned `Uri` may be **relative**: OData permits either form, and it resolves against the request URL. Use `OriginalString` when you need the link byte-for-byte as the server issued it.

### Cost, and what the client will not do for you

Preserving annotations means buffering the response body and reading it a second time — which is exactly why these are separate methods rather than a client-wide option. Nothing that does not call them pays for it.

`ToAnnotatedAsyncEnumerable` follows the **collection's own** `@odata.nextLink` across pages, exactly as `ToAsyncEnumerable` does. It never follows a **nested** link: that addresses a different resource with a different element type, so resuming it is your call to make with the `Uri` handed back.

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
