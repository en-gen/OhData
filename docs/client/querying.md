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

The same rule covers **captured values**. A variable, field or property closed over by the
predicate is read at translation time and embedded as a literal — and if reading it *throws*
(a property getter that fails, a null instance part-way down a chain), the translator throws
`NotSupportedException` with the original exception attached. It does **not** fall back to
`null`: a failed evaluation is not a null value, and emitting one would run a different query
than you wrote against a null-valued column. A captured value that genuinely *is* `null` still
translates to `eq null` as it always has.

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

> **An expanded collection may come back incomplete, and the ordinary read path cannot tell you.** A server that pages an expansion (OhData's own [`ExpandPagingEnabled`](../expand.md#nested-server-driven-paging-expandpagingenabled), or any other OData 4 service) returns a **prefix** of the related collection and says so with a per-entity `{Nav}@odata.nextLink` — an annotation `ToListAsync`/`ToPageAsync`/`GetAsync` discard, leaving a truncated collection indistinguishable from a complete one. Whenever a query carries `Expand`, prefer the [annotation-preserving terminals](terminal-operations.md#annotation-preserving-reads) (`ToAnnotatedPageAsync`, `ToAnnotatedAsyncEnumerable`, `GetAnnotatedAsync`), which surface `NextLinkFor(x => x.Nav)` and `CountFor(x => x.Nav)`.

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

## `MaxPageSize`

```csharp
.MaxPageSize(100)
// → Prefer: odata.maxpagesize=100
```

Asks the server for at most that many entities per page. It is sent as a `Prefer` header on the first request and on every `@odata.nextLink` request, so [`ToListAsync`](terminal-operations.md#tolistasync), `ToAsyncEnumerable` and the annotated walkers keep the page size for the whole walk. It is a preference the server may undercut, and it is not `$top`, which bounds the total.

Any `Prefer` values already on the `HttpClient`'s default headers (such as `odata.include-annotations="*"`) are sent too; a default `odata.maxpagesize` yields to this one. Throws `ArgumentOutOfRangeException` for a value below 1.

## `WithQueryOption`

```csharp
.WithQueryOption("$search", "red & blue")
.WithQueryOption("tenant", "acme")
// → $search=red%20%26%20blue&tenant=acme
```

Appends one option the typed builder does not model: a custom option, or a system option such as `$search` or `$apply`. Options follow the ones the builder composes, in the order added, and go on the collection, `/$count` and keyed (`Key(...)`) requests of the query, including `GetPropertyAsync` and `GetRawValueAsync`. Name and value are URL-encoded; a leading `$` stays literal.

The server decides what the option means: an unimplemented system option is a `501` (`ODataClientException`, `ODataErrorCode` `UnsupportedQueryOption`), and an unknown custom option is ignored. A page reached through a server-issued `@odata.nextLink` carries whatever that link carries.

Throws `ArgumentException` for a blank name and for `$filter`, `$select`, `$expand`, `$orderby`, `$top`, `$skip` and `$count` (case-insensitive, whitespace trimmed), which have typed methods above. A name without the `$` is a custom option under OData 4.0 and is sent as given.

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
