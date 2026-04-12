# OhData.Client

A typed .NET client for OData 4.0 services. Provides a fluent, LINQ-style API for querying and mutating entity sets — no code generation required.

## Installation

```
dotnet add package OhData.Client
```

## Setup

```csharp
// Create directly (owns the HttpClient — dispose when done)
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

`For<T>()` returns an `EntitySetClient<T>`. All builder methods are immutable — each call returns a new instance, making it safe to compose partial queries:

```csharp
var base = client.For<Product>().Filter(x => x.IsActive);

var cheap  = await base.Filter(x => x.Price < 10).ToListAsync();
var pricey = await base.Filter(x => x.Price > 100).OrderBy(x => x.Name).ToListAsync();
```

### `$filter`

Filter with a LINQ predicate — translated to an OData `$filter` string at call time:

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

For unsupported patterns, pass a raw OData string:

```csharp
.Filter("year(CreatedAt) eq 2024 and month(CreatedAt) ge 6")
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

.OrderBy(x => x.Category).ThenByDescending(x => x.Price)
// → $orderby=Category,Price desc
```

### `$top` and `$skip`

```csharp
.Top(20).Skip(40)
// → $top=20&$skip=40
```

Both validate `>= 0` and throw `ArgumentOutOfRangeException` otherwise.

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

### `FirstOrDefaultAsync`

Returns the first match or `null`. Applies `$top=1` automatically:

```csharp
Product? cheapest = await client.For<Product>()
    .OrderBy(x => x.Price)
    .FirstOrDefaultAsync();
```

### `CountAsync`

Hits `GET /{EntitySet}/$count` — returns the total count as a `long`:

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

### Get

```csharp
// Returns null on 404
Product? product = await client.For<Product>().Key(42).GetAsync();
```

### Insert (POST)

```csharp
// Returns the created entity with server-assigned values (e.g. generated Id)
Product created = await client.For<Product>()
    .InsertAsync(new Product { Name = "Cog", Price = 4.99m });
```

### Replace (PUT)

```csharp
// Returns the updated entity, or null if the server responds 204 No Content
Product? updated = await client.For<Product>().Key(42)
    .PutAsync(product with { Price = 5.49m });
```

### Partial update (PATCH)

Pass an anonymous object with only the properties to change:

```csharp
Product? patched = await client.For<Product>().Key(42)
    .PatchAsync(new { Name = "Cog v2", Price = 5.99m });
```

### Delete

```csharp
// Throws ODataClientException on 404
await client.For<Product>().Key(42).DeleteAsync();
```

---

## Error handling

Non-2xx responses throw `ODataClientException`, which parses the OData error envelope:

```csharp
try
{
    await client.For<Product>().Key(99999).GetAsync();
}
catch (ODataClientException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
{
    Console.WriteLine(ex.ODataErrorMessage);  // "Widget with key '99999' was not found."
}
catch (ODataClientException ex)
{
    Console.WriteLine($"HTTP {(int)ex.StatusCode}: [{ex.ODataErrorCode}] {ex.ODataErrorMessage}");
}
```

`ODataClientException` properties:
- `StatusCode` — the `HttpStatusCode`
- `ODataErrorCode` — the `"code"` field from the OData error body, or `null`
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
| `DateTime` / `DateTimeOffset` | ISO 8601 | `CreatedAt gt 2024-01-01T00:00:00Z` |
| `DateOnly` | `'yyyy-MM-dd'` | `Date eq 2024-06-15` |
| `TimeOnly` | `'HH:mm:ss'` | `Time eq 09:30:00` |
| `Enum` | underlying int | `Status eq 1` |

---

## Custom JSON options

`OhDataClientOptions.JsonOptions` is applied to both request serialization and response deserialization. Defaults: camelCase naming, case-insensitive reads, null values omitted on write.

```csharp
var client = new OhDataClient(httpClient, new OhDataClientOptions
{
    JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    }
});
```
