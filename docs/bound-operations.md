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

Register with `BindEntityFunction` / `BindEntityAction`. The handler's first parameter (after excluding a trailing `CancellationToken`) **must** be the entity key (`TKey`) — this is validated at bind time: a handler with no parameters, or whose first parameter isn't `TKey`, throws `InvalidOperationException` naming the operation, its entity set, and the expected signature. (Before this validation existed, both cases registered without error and only failed at request time — a zero-parameter handler with an uncaught `IndexOutOfRangeException`, a wrong-first-parameter-type handler with a `DynamicInvoke` failure.)

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

## Unbound functions and actions

`BindFunction`/`BindEntityFunction` and `BindAction`/`BindEntityAction` (above) are always attached to an entity set. OData also allows *unbound* functions and actions that live at the service root, with no entity set in the route at all. Register these on `OhDataBuilder` - inside the `AddOhData(...)` callback, not inside a profile:

| Kind | Route | HTTP |
|------|-------|------|
| Unbound function | `GET /{prefix}/{Name}?param=value` | GET |
| Unbound action | `POST /{prefix}/{Name}` | POST |

```csharp
builder.Services.AddOhData(o => o
    .AddProfile<ProductProfile>()
    .AddFunction((Func<string, Task<string>>)(name => Task.FromResult($"Hello, {name}!")), "Greet")
    .AddAction((Func<int, int, Task<int>>)((a, b) => Task.FromResult(a + b)), "AddNumbers"));
```

```
GET  /odata/Greet?name=World        → "Hello, World!"
POST /odata/AddNumbers { "a": 3, "b": 4 }   → 7
```

`AddFunction(Delegate handler, string? name = null)` and `AddAction(Delegate handler, string? name = null)` take any delegate - unlike `BindFunction`/`BindAction`, a lambda is fine, since the route name is either taken from the delegate's method name or supplied explicitly via `name`. Pass `name` whenever the handler is a lambda (its compiler-generated method name isn't a usable route segment). Parameters, `CancellationToken` detection, optional-parameter defaults, and return-type handling all follow the same rules as bound functions/actions described above. Unbound operations are registered in the EDM as `FunctionImport`/`ActionImport` and appear in `GET /$metadata` and the service document.

Assembly-scanning registration (`AddProfilesFrom`/`AddProfilesFromAssemblyOf`/`AddProfilesFromAssembly`) is documented in [docs/architecture.md](architecture.md#registering-profiles).

## Route collisions

Several distinct constructs can end up claiming the same `(route template, HTTP method)` pair. Since two endpoints can't otherwise register the same pair, every case below is caught by a startup validation pass — resolving the `OhDataRegistration` (which happens the first time `MapOhData()` runs) throws `InvalidOperationException` naming the conflicting pair, rather than deferring to an `AmbiguousMatchException` the first time a client hits the route:

| Collision | Route shape | Guard |
|---|---|---|
| Unbound function vs. another unbound function/action of the same kind | `GET`/`POST /{prefix}/{Name}` | Duplicate unbound operation name (case-insensitive) within a registration. |
| Unbound function/action vs. an entity set | `GET`/`POST /{prefix}/{Name}` vs. `GET`/`POST /{prefix}/{EntitySet}` | An unbound function's name matches an entity set with a registered collection `GET` (`GetAll`/`GetQueryable`); an unbound action's name matches an entity set with a registered `Post` (case-insensitive). |
| Entity-level bound function vs. a structural property | `GET /{EntitySet}({key})/{Name}` | A bound function's name matches a structural (non-navigation) property name. |
| Navigation property `post` handler vs. an entity-level bound action | `POST /{EntitySet}({key})/{Name}` | A navigation property configured with a `post` handler shares a name with an entity-level bound action. |

Navigation vs. structural-property routes never collide by construction (structural properties are computed as "every public readable CLR property minus every declared navigation property name"), so there is no guard for that pairing.
