# Querying

Part of the [OhData.Client guide](index.md). See also [Terminal operations](terminal-operations.md) for the methods that execute a query.

`For<T>()` returns an `EntitySetClient<T>`. All builder methods are immutable - each call returns a new instance, making it safe to compose partial queries:

```csharp
var base = client.For<Product>().Filter(x => x.IsActive);

var cheap  = await base.Filter(x => x.Price < 10).ToListAsync();
var pricey = await base.Filter(x => x.Price > 100).OrderBy(x => x.Name).ToListAsync();
```

**Property-name casing.** Every typed (expression-based) builder — `Filter`, `Select`, `OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`, and `Expand` — runs each property name through `OhDataClientOptions.JsonOptions.PropertyNamingPolicy` before emitting it. The default policy is `null` (PascalCase — the CLR names), matching the OhData server's PascalCase-default `$metadata` and responses, so `x => x.Price > 10` emits `$filter=Price gt 10`. Set `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` to emit camelCase for a server configured for camelCase. The raw-string overloads (`Filter(string)`, `Select(params string[])`, `Expand(params string[])`) are never rewritten — those names are sent exactly as you typed them. The examples below show the CLR property names, which are what the default options emit.

## `$filter`

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

## `$select`

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

## `$expand`

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

## `$orderby`

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

## `$top` and `$skip`

```csharp
.Top(20).Skip(40)
// → $top=20&$skip=40
```

Both validate `>= 0` and throw `ArgumentOutOfRangeException` otherwise.

## `IncludeCount`

Appends `$count=true` to the request so the server includes the total matching count in the response envelope. The count is available on `ODataPage<T>.TotalCount` when you call [`ToPageAsync`](terminal-operations.md#topageasync):

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

Next: [Terminal operations →](terminal-operations.md)
