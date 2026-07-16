# OhData.Client

A typed .NET client for OData 4.0 services. Provides a fluent, LINQ-style API for querying and mutating entity sets - no code generation required.

## Installation

```
dotnet add package EnGen.OhData.Client
```

## Setup

```csharp
// Create directly (owns the HttpClient - dispose when done)
var client = new OhDataClient("https://api.example.com/odata");

// Or wrap a caller-supplied HttpClient (recommended for IHttpClientFactory)
var client = new OhDataClient(httpClient);

// With custom JSON options
var client = new OhDataClient(httpClient, new OhDataClientOptions
{
    JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // ...
    }
});
```

### With `IHttpClientFactory` (ASP.NET Core)

```csharp
// Program.cs
builder.Services.AddHttpClient("products", c =>
    c.BaseAddress = new Uri("https://api.example.com/odata/"));

// In a service:
public class ProductService(IHttpClientFactory factory)
{
    private readonly OhDataClient _client = new(factory.CreateClient("products"));
}
```

## Entity set name resolution

The client resolves the entity set name automatically via:

1. `[ODataEntitySet("CustomName")]` attribute on the model class, or
2. Simple pluralisation of the class name (`Product` → `Products`, `Category` → `Categories`)

```csharp
[ODataEntitySet("MyCategories")]  // overrides pluralisation
public class Category { ... }
```

Pass an explicit name to `For<T>` if needed:

```csharp
client.For<Category>("MyCategories")
```

---

## Querying

`For<T>()` returns an `EntitySetClient<T>`. All builder methods are immutable - each call returns a new instance, making it safe to compose partial queries:

```csharp
var base = client.For<Product>().Filter(x => x.IsActive);

var cheap  = await base.Filter(x => x.Price < 10).ToListAsync();
var pricey = await base.Filter(x => x.Price > 100).OrderBy(x => x.Name).ToListAsync();
```

**Property-name casing.** Every typed (expression-based) builder — `Filter`, `Select`, `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`, and `Expand` — runs each property name through `OhDataClientOptions.JsonOptions.PropertyNamingPolicy` before emitting it. The default policy is camelCase (matching the OhData server's JSON casing), so `x => x.Price > 10` emits `$filter=price gt 10`. Set `PropertyNamingPolicy = null` to emit the CLR PascalCase names for servers with PascalCase metadata. The raw-string overloads (`Filter(string)`, `Select(params string[])`, `Expand(params string[])`) are never rewritten — those names are sent exactly as you typed them. For readability, the examples below show the CLR property names; with the default options they are emitted camelCase.

### `$filter`

Filter with a LINQ predicate - translated to an OData `$filter` string at call time:

```csharp
// Comparison and logical operators
.Filter(x => x.Price > 10 && x.Name.StartsWith("W"))
// → $filter=Price gt 10 and startswith(Name,'W')

// Navigation path
.Filter(x => x.Category.Name == "Electronics")
// → $filter=Category/Name eq 'Electronics'

// String methods
.Filter(x => x.Name.Contains("cog") || x.Description.EndsWith("v2"))
// → $filter=contains(Name,'cog') or endswith(Description,'v2')

// Captured variables (evaluated immediately at translation time)
decimal min = 5m;
.Filter(x => x.Price >= min)
// → $filter=Price ge 5
```

**Supported operators and functions:**

| LINQ | OData |
|------|-------|
| `==`, `!=`, `>`, `>=`, `<`, `<=` | `eq`, `ne`, `gt`, `ge`, `lt`, `le` |
| `&&`, `\|\|`, `!` | `and`, `or`, `not` |
| `+`, `-`, `*`, `/`, `%` | `add`, `sub`, `mul`, `div`, `mod` |
| `.Contains(s)` | `contains(prop,'s')` |
| `.StartsWith(s)` | `startswith(prop,'s')` |
| `.EndsWith(s)` | `endswith(prop,'s')` |
| `.ToLower()`, `.ToUpper()` | `tolower(prop)`, `toupper(prop)` |
| `.Trim()` | `trim(prop)` |
| `string.IsNullOrEmpty(x.P)` | `(x.P eq null or x.P eq '')` |
| `.Length` (string) | `length(prop)` |
| `.Year` / `.Month` / `.Day` (DateTime, DateTimeOffset, DateOnly) | `year(prop)` / `month(prop)` / `day(prop)` |
| `.Hour` / `.Minute` / `.Second` (DateTime, DateTimeOffset, TimeOnly) | `hour(prop)` / `minute(prop)` / `second(prop)` |
| `.Any(t => ...)` / `.All(t => ...)` (collection property) | `prop/any(t: ...)` / `prop/all(t: ...)` |

Inside an `Any`/`All` lambda you can reference the outer entity — the translator emits the
OData implicit iteration variable `$it` for it:

```csharp
.Filter(x => x.Tags.Any(t => t.Name == x.Name))
// → $filter=Tags/any(t: t/Name eq $it/Name)
```

Expressions that reference a lambda range variable in a way that has no OData path equivalent
(e.g. a member access on a ternary) throw `NotSupportedException` at translation time rather
than silently producing a wrong query.

For unsupported patterns, pass a raw OData string:

```csharp
.Filter("round(Price) eq 5")
```

### `$select`

```csharp
// Anonymous projection (most common)
.Select(x => new { x.Id, x.Name, x.Price })
// → $select=Id,Name,Price

// Multiple members
.Select(x => x.Id, x => x.Name)
// → $select=Id,Name

// Navigation path
.Select(x => new { x.Category.Name })
// → $select=Category/Name

// String overload
.Select("Id", "Name", "Category/Name")
```

### `$expand`

```csharp
// Single navigation
.Expand(x => x.Category)
// → $expand=Category

// Multiple
.Expand(x => x.Category, x => x.Tags)
// → $expand=Category,Tags

// Nested options (string overload)
.Expand("Category($select=Name;$expand=Parent($select=Id))")
```

### `$orderby`

```csharp
.OrderBy(x => x.Name)
// → $orderby=Name

.OrderByDescending(x => x.Price)
// → $orderby=Price desc
```

Chain secondary sorts with `ThenBy` / `ThenByDescending`:

```csharp
.OrderBy(x => x.Category).ThenByDescending(x => x.Price)
// → $orderby=Category,Price desc

.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Name)
// → $orderby=UpdatedAt desc,Name
```

### `$top` and `$skip`

```csharp
.Top(20).Skip(40)
// → $top=20&$skip=40
```

Both validate `>= 0` and throw `ArgumentOutOfRangeException` otherwise.

### `IncludeCount`

Appends `$count=true` to the request so the server includes the total matching count in the response envelope. The count is available on `ODataPage<T>.TotalCount` when you call `ToPageAsync`:

```csharp
ODataPage<Product> page = await client.For<Product>()
    .Filter(x => x.IsActive)
    .IncludeCount()
    .Top(20)
    .ToPageAsync();

Console.WriteLine($"{page.TotalCount} active products");
```

Note: `ToPageAsync` always forces `$count=true` regardless of whether `IncludeCount` was called. `IncludeCount` is useful when composing query state before calling `ToPageAsync` from a helper.

---

## Terminal operations

### `ToListAsync`

Returns all matching items as a `List<T>`:

```csharp
List<Product> items = await client.For<Product>()
    .Filter(x => x.Price > 5)
    .OrderBy(x => x.Name)
    .ToListAsync();
```

### `ToPageAsync`

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

### `FirstOrDefaultAsync`

Returns the first match or `null`. Applies `$top=1` automatically:

```csharp
Product? cheapest = await client.For<Product>()
    .OrderBy(x => x.Price)
    .FirstOrDefaultAsync();
```

### `CountAsync`

Hits `GET /{EntitySet}/$count` - returns the total count as a `long`:

```csharp
long count = await client.For<Product>()
    .Filter(x => x.Price < 5)
    .CountAsync();
```

### `AnyAsync`

Returns `true` if at least one entity matches:

```csharp
bool hasStock = await client.For<Product>()
    .Filter(x => x.StockLevel > 0)
    .AnyAsync();
```

### `FirstAsync`

Returns the first match. Applies `$top=1` automatically. Throws `InvalidOperationException` when the collection is empty (use `FirstOrDefaultAsync` if no results is a valid outcome):

```csharp
Product cheapest = await client.For<Product>()
    .OrderBy(x => x.Price)
    .FirstAsync();
```

### `SingleOrDefaultAsync`

Returns the single matching entity, or `null` when none match. Applies `$top=2` and throws `InvalidOperationException` when more than one entity matches:

```csharp
Product? active = await client.For<Product>()
    .Filter(x => x.Sku == "ABC-1")
    .SingleOrDefaultAsync();
```

### `SingleAsync`

Returns the single matching entity. Throws `InvalidOperationException` when zero or more than one entity matches:

```csharp
Product product = await client.For<Product>()
    .Filter(x => x.Sku == "ABC-1")
    .SingleAsync();
```

### `ToArrayAsync`

Returns all matching items as a `T[]`:

```csharp
Product[] items = await client.For<Product>()
    .Filter(x => x.IsActive)
    .ToArrayAsync();
```

---

## Single-entity operations

`Key(value)` returns a `KeyedEntitySetClient<T>` scoped to a specific entity:

```csharp
var keyed = client.For<Product>().Key(42);
```

Key values are formatted as OData literals automatically:
- `int`/`long` → `Products(42)`
- `string` → `Products('value')`, single quotes escaped
- `Guid` → `Products(3f2504e0-...)`

A generic overload provides compile-time type safety for the key value:

```csharp
var keyed = client.For<Product>().Key<int>(42);
```

### Get

```csharp
// Returns null on 404
Product? product = await client.For<Product>().Key(42).GetAsync();
```

### Get with ETag

Retrieves the entity and the server's current ETag in one call. Pass the ETag to `PutAsync` or `PatchAsync` for optimistic concurrency:

```csharp
var (product, etag) = await client.For<Product>().Key(42).GetWithETagAsync();

// Later — fail with 412 if another client modified the entity in between
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(new Product { Id = product!.Id, Name = product.Name, Price = 5.49m }, ifMatch: etag);
```

### Conditional GET with If-None-Match

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
See [etags.md](etags.md#conditional-reads) for the server-side behavior this pairs with.

### Insert (POST)

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

### Replace (PUT)

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

### Partial update (PATCH)

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

### Delete

```csharp
// Throws ODataClientException on 404
await client.For<Product>().Key(42).DeleteAsync();
```

Pass an ETag to enforce optimistic concurrency — returns `412 Precondition Failed` if the entity has been modified:

```csharp
await client.For<Product>().Key(42).DeleteAsync(ifMatch: etag);
```

---

## Error handling

Non-2xx responses throw `ODataClientException`, which parses the OData error envelope:

```csharp
try
{
    await client.For<Product>().Key(99999).GetAsync();
}
catch (ODataClientException ex) when (ex.StatusCode == 404)
{
    Console.WriteLine(ex.ODataErrorMessage);  // "Widget with key '99999' was not found."
}
catch (ODataClientException ex)
{
    Console.WriteLine($"HTTP {ex.StatusCode}: [{ex.ODataErrorCode}] {ex.ODataErrorMessage}");
}
```

`ODataClientException` properties:
- `StatusCode` — the HTTP status code as an `int` (e.g. `404`, `412`)
- `ODataErrorCode` — the `"code"` field from the OData error body, or an empty string if the response was not an OData error envelope
- `ODataErrorMessage` — the `"message"` field, or the raw response body if not a valid OData error

---

## Literal type support

The filter translator and key formatter handle these CLR types:

| CLR type | OData literal | Example |
|----------|--------------|---------|
| `string` | `'value'` (single-quoted, `'` → `''`) | `Name eq 'it''s'` |
| `int`, `long`, `short` | unquoted decimal | `Id eq 42` |
| `decimal`, `float`, `double` | invariant-culture decimal | `Price gt 4.99` |
| `bool` | `true` / `false` | `IsActive eq true` |
| `Guid` | 36-char hex | `Id eq 3f2504e0-...` |
| `DateTime` / `DateTimeOffset` | ISO 8601, always with `Z` or an explicit offset | `CreatedAt gt 2024-01-01T00:00:00Z` |
| `DateOnly` | `'yyyy-MM-dd'` | `Date eq 2024-06-15` |
| `TimeOnly` | `'HH:mm:ss'` | `Time eq 09:30:00` |
| `Enum` | quoted member name | `Status eq 'Active'` |

**`DateTime` kind semantics.** OData `Edm.DateTimeOffset` literals always require a `Z` or an
explicit numeric offset, so the client never emits an offset-less value:
`DateTimeKind.Utc` is emitted as-is with `Z`; `DateTimeKind.Local` (e.g. `DateTime.Now`) is
converted to its UTC instant via `ToUniversalTime()`; `DateTimeKind.Unspecified` is treated as
UTC. If your `Unspecified` values represent local wall-clock time, convert them yourself (or
use `DateTimeOffset`, which always carries its own offset and passes through unchanged).

---

## Client options

`OhDataClientOptions` is passed to the `OhDataClient` constructor to customise behaviour.

### `JsonOptions`

Applied to both request serialization and response deserialization. Defaults: camelCase naming, case-insensitive reads, null values omitted on write.

```csharp
var client = new OhDataClient(httpClient, new OhDataClientOptions
{
    JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    }
});
```

### `NotFoundBehavior`

Controls how `404 Not Found` responses are handled for single-entity GET operations (`GetAsync`, `GetWithETagAsync`). Default is `NotFoundBehavior.ReturnNull`.

| Value | Behaviour |
|-------|-----------|
| `ReturnNull` (default) | Returns `null` when the entity is not found |
| `Throw` | Throws `ODataClientException` with status `404` |

```csharp
var client = new OhDataClient(httpClient, new OhDataClientOptions
{
    NotFoundBehavior = NotFoundBehavior.Throw
});
```
