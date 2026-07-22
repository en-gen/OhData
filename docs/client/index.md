# OhData.Client

A typed .NET client for OData 4.0 services. Provides a fluent, LINQ-style API for querying and mutating entity sets - no code generation required.

This guide is split across several pages:

- **Overview & setup** (this page) — installation, constructing the client, `IHttpClientFactory`, entity set name resolution, and client options.
- [Querying](querying.md) — `$filter`, `$select`, `$expand`, `$orderby`, `$top`/`$skip`, `IncludeCount`.
- [Terminal operations](terminal-operations.md) — `ToListAsync`, `ToPageAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync`, and the rest.
- [Single-entity operations](single-entity.md) — get, insert, replace, partial update, delete, ETags, and conditional GET.
- [Error handling & literal types](errors-and-types.md) — `ODataClientException` and the CLR-to-OData literal mapping.

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

// With custom JSON options — e.g. opt into camelCase for a camelCase-configured server
// (the default is PascalCase, matching the OhData server's default $metadata/responses)
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

## Client options

`OhDataClientOptions` is passed to the `OhDataClient` constructor to customise behaviour.

### `JsonOptions`

Applied to both request serialization and response deserialization. Defaults: PascalCase naming (`PropertyNamingPolicy = null`, matching the OhData server's PascalCase default), case-insensitive reads, null values omitted on write. Set `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` to target a camelCase server.

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

---

Next: [Querying →](querying.md)
