# Bound Functions and Actions

OData distinguishes between *functions* (side-effect-free, HTTP GET) and *actions* (may have side effects, HTTP POST). OhData supports both at the collection level and the entity level.

## Collection-bound operations

Bound to the entity set, not to a specific entity instance.

| Kind | Route | HTTP |
|------|-------|------|
| Function | `GET /{EntitySet}/{FunctionName}?param=value` | GET |
| Action | `POST /{EntitySet}/{ActionName}` | POST |

Register with `BindFunction` / `BindAction` inside the profile constructor. The method name becomes the operation name - **the handler must be a named method, not a lambda.** Passing a lambda (whose compiler-generated method name isn't a valid OData identifier) throws `InvalidOperationException` at startup:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        BindFunction(GetCheapest);      // GET /Products/GetCheapest?maxPrice=10.00
        BindAction(ApplyDiscount);      // POST /Products/ApplyDiscount  { "percent": 10 }

        GetAll = (ct) => Task.FromResult<IEnumerable<Product>>(store);
    }

    private Task<IEnumerable<Product>> GetCheapest(decimal maxPrice, CancellationToken ct) =>
        Task.FromResult(store.Where(p => p.Price <= maxPrice));

    private Task ApplyDiscount(decimal percent, CancellationToken ct)
    {
        foreach (var p in store) p.Price *= (1 - percent / 100);
        return Task.CompletedTask;
    }
}
```

## Entity-bound operations

Bound to a specific entity instance identified by key.

| Kind | Route | HTTP |
|------|-------|------|
| Function | `GET /{EntitySet}({key})/{FunctionName}?param=value` | GET |
| Action | `POST /{EntitySet}({key})/{ActionName}` | POST |

Register with `BindEntityFunction` / `BindEntityAction`. The handler's first parameter (after excluding `CancellationToken`) **must** be the entity key (`TKey`):

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile() : base(x => x.Id)
    {
        BindEntityFunction(GetLineCount);  // GET /Orders(id)/GetLineCount
        BindEntityAction(Cancel);          // POST /Orders(id)/Cancel

        GetById = (id, ct) => Task.FromResult(store.Find(id));
    }

    // First param is the key - the framework extracts it from the URL
    private Task<int> GetLineCount(Guid orderId, CancellationToken ct) =>
        Task.FromResult(store.Find(orderId)?.Lines.Count ?? 0);

    private Task Cancel(Guid orderId, CancellationToken ct)
    {
        var order = store.Find(orderId);
        if (order is not null) order.Status = "Cancelled";
        return Task.CompletedTask;
    }
}
```

## Parameters

### Functions - query string

Function parameters are read from the query string. Any CLR type that can be parsed from a string (including primitives, `Guid`, `DateTimeOffset`, enums) is supported:

```
GET /Products/GetCheapest?maxPrice=10.00
GET /Orders/CreatedBetween?from=2024-01-01&to=2024-03-31
```

### Actions - JSON body

Action parameters are read from a JSON request body as named properties:

```http
POST /Products/ApplyDiscount
Content-Type: application/json

{ "percent": 10.0 }
```

### `CancellationToken`

If the handler method includes a `CancellationToken` as its last parameter, the framework detects it and passes the request's `CancellationToken` automatically. It does not appear as an OData parameter.

### Optional parameters

Mark a parameter as optional with a default value:

```csharp
private Task<IEnumerable<Product>> GetCheapest(decimal maxPrice = 100m, CancellationToken ct = default) =>
    Task.FromResult(store.Where(p => p.Price <= maxPrice));
```

Optional parameters and their defaults are reflected in `$metadata`.

## Return types

Any return type is supported - the result is serialized as JSON. Wrap in `Task<T>` for async operations, or return `void`/`Task` for no-content responses. `ValueTask` and `ValueTask<T>` are also supported alongside `Task`/`Task<T>` - the framework detects the return type via reflection at startup and dispatches accordingly:

```csharp
// Returns a single entity
private Task<Product?> GetCheapest(CancellationToken ct) => ...;

// Returns a collection
private Task<IEnumerable<Product>> GetAllOnSale(CancellationToken ct) => ...;

// No return value (action with side effect only)
private Task Archive(Guid orderId, CancellationToken ct) => ...;
```

## EDM and `$metadata`

Bound operations are registered in the EDM model and appear in `GET /$metadata`. Functions are registered on the entity set (or entity type for entity-bound), making them discoverable by OData-aware clients.

## Error handling

Exceptions thrown from the handler propagate as `500 Internal Server Error`. To return OData error responses from within a handler, throw a structured exception or return an appropriate HTTP result - consider wrapping the operation in try/catch and returning `Results.Problem(...)` if granular error control is needed.
