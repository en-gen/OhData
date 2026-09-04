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
    private readonly AppDbContext _db;

    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        _db = db;   // named-method handlers below capture it via the field

        BindFunction(GetCheapest);      // GET /Products/GetCheapest?maxPrice=10.00
        BindAction(ApplyDiscount);      // POST /Products/ApplyDiscount  { "percent": 10 }

        GetAll = async (ct) =>
            OhDataResult.Success<IEnumerable<Product>>(await _db.Products.ToListAsync(ct));
    }

    private async Task<IEnumerable<Product>> GetCheapest(decimal maxPrice, CancellationToken ct) =>
        await _db.Products.Where(p => p.Price <= maxPrice).ToListAsync(ct);

    private async Task ApplyDiscount(decimal percent, CancellationToken ct)
    {
        var products = await _db.Products.ToListAsync(ct);
        foreach (var p in products) p.Price *= (1 - percent / 100);
        await _db.SaveChangesAsync(ct);
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
    private readonly AppDbContext _db;

    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        _db = db;   // named-method handlers below capture it via the field

        BindEntityFunction(GetLineCount);  // GET /Orders(id)/GetLineCount
        BindEntityAction(Cancel);          // POST /Orders(id)/Cancel

        GetById = async (id, ct) =>
            OhDataResult.Success(await _db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct));
    }

    // First param is the key - the framework extracts it from the URL
    private async Task<int> GetLineCount(Guid orderId, CancellationToken ct) =>
        await _db.Orders.Where(o => o.Id == orderId).Select(o => o.Lines.Count).FirstOrDefaultAsync(ct);

    private async Task Cancel(Guid orderId, CancellationToken ct)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is not null)
        {
            order.Status = "Cancelled";
            await _db.SaveChangesAsync(ct);
        }
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
private async Task<IEnumerable<Product>> GetCheapest(decimal maxPrice = 100m, CancellationToken ct = default) =>
    await _db.Products.Where(p => p.Price <= maxPrice).ToListAsync(ct);
```

Optional parameters and their defaults are reflected in `$metadata`.

## Return types

Almost any return type is supported - the result is serialized as JSON. Wrap in `Task<T>` for async operations. `ValueTask` and `ValueTask<T>` are also supported alongside `Task`/`Task<T>` - the framework detects the return type via reflection at startup and dispatches accordingly. Three shapes are **refused at bind time** and are listed in [Signatures the framework rejects at bind time](#signatures-the-framework-rejects-at-bind-time) below: a `void`-returning *function*, a `CancellationToken` that is not the last parameter (or a nullable one), and any return type implementing `IResult`.

```csharp
// Returns a single entity
private Task<Product?> GetCheapest(CancellationToken ct) => ...;

// Returns a collection
private Task<IEnumerable<Product>> GetAllOnSale(CancellationToken ct) => ...;

// No return value (action with side effect only)
private Task Archive(Guid orderId, CancellationToken ct) => ...;
```

### Actions may return nothing; functions may not

`void`/`Task`/`ValueTask` is a no-content response (`204 No Content`) and is valid for an **action**
only. CSDL requires a **function** to declare a return type, so a void-returning function cannot be
written into `$metadata` at all; `BindFunction`/`BindEntityFunction`/`AddFunction` reject one at bind
time with an `InvalidOperationException` naming the operation and pointing at the matching
`Bind*Action`/`AddAction`. (Before #498 this was not checked, and `GetEdmModel()` died instead with a
raw `ArgumentNullException: 'returnType'` that named neither the profile nor the operation.)

### Signatures the framework rejects at bind time

| Shape | Why |
|---|---|
| A `void`/`Task`/`ValueTask`-returning **function** | CSDL has no representation for it. Use an action. |
| A `CancellationToken` that is not the **last** parameter | The framework strips and supplies only a trailing token, while the EDM omits one at any position — so the route would demand a query parameter `$metadata` never declared, and which no value can satisfy. |
| A `CancellationToken?` anywhere | Not recognised as the framework's token, so it would be exposed as an OData parameter of a type that is not an EDM type. |
| A return type implementing `IResult` | OhData owns the HTTP envelope. An `IResult` would be serialized as a DTO (its `Value`/`StatusCode` properties as the response body) and its type written into `$metadata`. Return the value itself. |

A `byte[]` return is declared as `Edm.Binary` (not `Collection(Edm.Byte)`), matching what the route
actually serves.

**An action may return the entity set's own type (#539).** `Microsoft.OData.ModelBuilder`'s
`ActionConfiguration.Returns<T>()` / `.ReturnsCollection<T>()` refuse a CLR type already declared as
an entity type and direct the caller to `ReturnsFromEntitySet` /
`ReturnsCollectionFromEntitySet`, while the `FunctionConfiguration` twins accept it. OhData used to
call only the first pair, so a `BindAction` declared `Task<TModel>` or `Task<IEnumerable<TModel>>` —
`POST /Widgets/Archive` answering with the archived rows, an ordinary OData shape — failed at
`MapOhData()` quoting a method the developer could not reach. OhData now calls
`ReturnsFromEntitySet` / `ReturnsCollectionFromEntitySet` with the declaring profile's own entity
set whenever the (element) return type is that profile's model type, for **actions and functions
alike**; the CSDL a bound operation emits is byte-identical either way, so nothing on the function
side moved.

An operation that returns some *other* registered entity type still cannot be expressed — OhData can
only bind an operation's entity return to the entity set of the profile that declares it — but the
failure is now OhData's own message, naming the operation and the remedies, rather than
`Microsoft.OData.ModelBuilder`'s.

### A collection-returning FUNCTION is paged like any other collection (#357)

A **bound function** whose result is a collection of the entity set's own type is bounded by the
profile's `MaxTop` and served with a `@odata.nextLink` continuation, using exactly the semantics the
`GetAll` collection route uses (see
[Query options - `GetAll`](query-options.md#getall---simple-in-memory-path)):

| Request | Behaviour |
|---|---|
| no `$top` | capped to `MaxTop` (or a smaller `Prefer: maxpagesize`, echoed in `Preference-Applied`); `@odata.nextLink` carries the remainder as `$skip=N` |
| `$top=N`, `N <= MaxTop` | applied as-is; suppresses the default cap and emits no `@odata.nextLink` |
| `$top=N`, `N > MaxTop` | `400 InvalidQueryOption`, with the same message the collection route uses |
| `$skip=N` | applied |
| a malformed `$top`/`$skip` | `400 InvalidQueryOption` - never silently ignored |
| `MaxTop = null` | no cap, no `@odata.nextLink` - the full collection in one response |

Before this, such an operation bypassed `MaxTop`, `$top`/`$skip` and server-driven paging entirely,
so the ceiling the framework enforces on every ordinary collection route was bypassable through any
operation that returned a collection. **This is a breaking change** for a function that returns more
than `MaxTop` (default `1000`) entities: a client that reads `value` without following
`@odata.nextLink` now sees a truncated result. Set `MaxTop = null` to opt out.

No other system query option is applied to an operation result - `$filter`, `$orderby`, `$select`,
`$expand` and `$count` are still ignored there, as they always were.

### A collection-returning ACTION is bounded too, but cannot be continued (#543)

A **bound action** whose result is a collection of the entity set's own type honours `$top` and
`$skip` and validates them against `MaxTop` exactly as the function above does — same rules, same
`400` messages. The one row that differs is the first:

| Request | Behaviour |
|---|---|
| no `$top`, result **within** `MaxTop` | served in full, unchanged, with no `@odata.nextLink` |
| no `$top`, result **larger than** `MaxTop` | **`500`** + the OData error envelope, with the real reason logged |
| `$top=N`, `N <= MaxTop` | applied as-is; no `@odata.nextLink` |
| `$top=N`, `N > MaxTop` | `400 InvalidQueryOption`, same message as everywhere else |
| `$skip=N` | applied |
| a malformed `$top`/`$skip` | `400 InvalidQueryOption` |
| `MaxTop = null` | no ceiling — the full collection in one response |
| `Prefer: maxpagesize` | **not honoured**, and no `Preference-Applied` is emitted |

**Why a refusal and not a page.** A `@odata.nextLink` is a URL the client **GETs** (Protocol
§11.2.5.7), and an action is invoked by `POST` to its action URL (§11.5.4.1) — so there is no
GET-addressable continuation of an action invocation for a link to point at. A continuation link
there would answer `405`, and re-POSTing a side-effecting action to collect page 2 is not a
continuation in any case. Capping *without* a link would be silent
truncation, which the framework does nowhere. That leaves refusing, and the refusal is a `500`
rather than a `400` because the condition is decided entirely by server-side state — the profile
declared the ceiling and the handler returned more than fits under it, identically for every client
and every request. The only party who can act on it is the operator, so the log line carries the
count, the ceiling and the three remedies: return fewer entities, set `MaxTop = null`, or expose the
operation as a `BindFunction`, which is pageable.

`Prefer: maxpagesize` is deliberately ignored because it is a *server-driven-paging* preference and
there is no paging to drive. RFC 7240 makes preferences advisory and forbids claiming
`Preference-Applied` for one that was not applied, so ignoring it is spec-correct rather than a
silent drop.

Before #543 an action applied none of this: measured on `MaxTop = 10` over 25 rows, `POST /Set/Dump`
answered `200` with all 25 entities and no `@odata.nextLink`, and `$top=999`, `$top=5`, `$skip=20`
and `$top=abc` were all likewise `200` with the full 25 — while the sibling *function* capped at 10
with a continuation and `400`d `$top=999`.

## System query options on an operation route (#359)

All six operation routes — collection-bound and entity-bound function and action, and the two
unbound ones — refuse any `$`-prefixed query option they do not implement with
**`501 Not Implemented`** (`UnsupportedQueryOption`). The implemented sets are small:

| Route | Implemented `$` options | Everything else |
|---|---|---|
| `GET /{Set}/{Function}`, `GET /{Set}({key})/{Function}` | `$top`, `$skip`, `$format` | `501` |
| `POST /{Set}/{Action}`, `POST /{Set}({key})/{Action}` | `$top`, `$skip`, `$format` | `501` |
| `GET /{Function}`, `POST /{Action}` (unbound) | `$format` | `501` |

Three things worth knowing:

- **An operation's own parameters are unaffected.** A function's parameters are ordinary
  (non-`$`) query keys and an action's are in the body, so the gate never looks at them. Only
  `$`-prefixed keys are examined, per Part 2 §5.2's reservation of `$` for system query options.
- **`$top`/`$skip` are listed unconditionally**, not derived from the declared return type. The
  `MaxTop` ceiling is applied to whatever the handler returns at runtime, so a handler declared
  `Task<object>` really can produce a `$skip=N` continuation — and refusing the option would mean
  refusing a link the server itself had just issued. Where the result is not a collection they are
  accepted no-ops. On an **unbound** operation there is no collection branch and no continuation,
  so they are refused.
- **The gate runs before parameter binding and before the handler delegate**, so a refused
  **action** invocation provably mutates nothing.

Before #359 the operation routes had no gate at all, and `TryApplyOperationCollectionPaging` copies
the *whole* incoming query string into the `@odata.nextLink` it builds — so an unrecognized option
was echoed verbatim into a link the server generated. See
[query-options.md](query-options.md#unsupported-system-query-options-are-rejected-359-380-353) for
the full `501`-vs-`400` taxonomy.

## EDM and `$metadata`

Bound operations are registered in the EDM model and appear in `GET /$metadata`. Functions are registered on the entity set (or entity type for entity-bound), making them discoverable by OData-aware clients.

## Error handling

An exception thrown from the handler propagates up to the group-level exception filter and comes
back as a `500 Internal Server Error` with the standard OData error envelope
(`code: "InternalServerError"`, a generic message - the exception's own message/stack trace is
never echoed to the client, only logged). See
[Error responses](spec-compliance.md#error-responses) ("Unhandled handler exceptions" row) for the
full behavior.

**An operation handler cannot choose its own status code.** Earlier revisions of this page told you
to *"catch the failure yourself and return `ODataError`-shaped `Results.Json(...)` /
`Results.BadRequest(...)`"*. **Do not do that** — since #498 an `IResult` return type is refused at
bind time, so a handler written that way now throws `InvalidOperationException` from the profile
constructor and the app does not start. OhData owns the HTTP envelope for an operation route: it
writes the status, the `@odata.context` and the response shape.

What is available today:

| You want | Do this |
|---|---|
| To signal failure | `throw`. The group filter logs the real exception and answers `500` + the OData error envelope (`code: "InternalServerError"`, a generic message — nothing from your exception reaches the client). |
| A client-visible outcome that is not an exception | Model it in the **return value** — return a DTO with your own status/reason members. It comes back as a `200`. |
| A real `4xx` with an OData error envelope | Not expressible from an operation handler. Use an entity-set route (`Post`/`Patch`/…), whose handlers *can* refuse, or validate before invoking the operation. |

Returning `null` from an operation handler is not an error path either — it produces `204 No
Content`, the same as a `void`/`Task`/`ValueTask` action.

## Unbound functions and actions

`BindFunction`/`BindEntityFunction` and `BindAction`/`BindEntityAction` (above) are always attached to an entity set. OData also allows *unbound* functions and actions that live at the service root, with no entity set in the route at all. Register these on `OhDataBuilder` - inside the `AddOhData(...)` callback, not inside a profile:

| Kind | Route | HTTP |
|------|-------|------|
| Unbound function | `GET /{prefix}/{Name}?param=value` | GET |
| Unbound action | `POST /{prefix}/{Name}` | POST |

```csharp
builder.Services.AddOhData(o => o
    .AddEntitySetProfile<ProductProfile>()
    .AddFunction((Func<string, Task<string>>)(name => Task.FromResult($"Hello, {name}!")), "Greet")
    .AddAction((Func<int, int, Task<int>>)((a, b) => Task.FromResult(a + b)), "AddNumbers"));
```

```
GET  /odata/Greet?name=World        → "Hello, World!"
POST /odata/AddNumbers { "a": 3, "b": 4 }   → 7
```

`AddFunction(Delegate handler, string? name = null)` and `AddAction(Delegate handler, string? name = null)` take any delegate - unlike `BindFunction`/`BindAction`, a lambda is fine, since the route name is either taken from the delegate's method name or supplied explicitly via `name`. Pass `name` whenever the handler is a lambda (its compiler-generated method name isn't a usable route segment). Parameters, `CancellationToken` detection, optional-parameter defaults, and return-type *dispatch* (`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`/`void`) all follow the same rules as bound functions/actions described above.

**Response shape is not the same, though.** Bound functions/actions (both collection- and
entity-level) wrap their result per JSON §11: a `TModel` result gets the entity/collection
`@odata.context` treatment described above, and a recognized Edm-primitive result (string, numeric
types, `bool`, `Guid`, date/time types, `byte[]`) gets the individual-value envelope
(`{"@odata.context":".../$metadata#Edm.<Type>","value":<primitive>}`). Unbound functions/actions
do **not** get any of this: the handler's result is returned as a bare JSON body with no
`@odata.context` and no `value` envelope, even for a `TModel` or primitive result (`result is not
null ? Results.Ok(result) : Results.NoContent()`). This asymmetry is a known post-1.0 cleanup
candidate, not a bug fix planned for this release — treat unbound-operation responses as
unenveloped JSON when writing a client against them. Unbound operations are registered in the EDM
as `FunctionImport`/`ActionImport` and appear in `GET /$metadata`.

The **service document** lists only what can be invoked with nothing but its name (#468), and it is
generated from the same EDM container `$metadata` is written from, so the two cannot disagree:

- a **parameterless** function import carries `IncludeInServiceDocument="true"` in the CSDL and is
  listed with `"kind": "FunctionImport"`;
- a **parameterized** function import clears the flag and is omitted. Claiming otherwise is not
  merely inaccurate, it is invalid CSDL — `EdmValidator` flags
  `FunctionImportWithParameterShouldNotBeIncludedInServiceDocument` per CSDL 4.0 §13.6, and OhData
  now runs that validator at `MapOhData()`;
- an **action import** is never listed. It is not GET-addressable, and CSDL gives it no
  `IncludeInServiceDocument` attribute at all.

Assembly-scanning registration (`AddProfilesFrom`/`AddProfilesFromAssemblyOf`/`AddProfilesFromAssembly`) is documented in [docs/architecture.md](architecture.md#registering-profiles).

## Route collisions

Several distinct constructs can end up claiming the same `(route template, HTTP method)` pair. Since two endpoints can't otherwise register the same pair, every case below is caught by a startup validation pass — resolving the `OhDataRegistration` (which happens the first time `MapOhData()` runs) throws `InvalidOperationException` naming the conflicting pair, rather than deferring to an `AmbiguousMatchException` the first time a client hits the route:

| Collision | Route shape | Guard |
|---|---|---|
| Unbound function vs. another unbound function/action of the same kind | `GET`/`POST /{prefix}/{Name}` | Duplicate unbound operation name (case-insensitive) within a registration. |
| Unbound function/action vs. an entity set | `GET`/`POST /{prefix}/{Name}` vs. `GET`/`POST /{prefix}/{EntitySet}` | An unbound function's name matches an entity set that registers a collection `GET` by **any** of the three read paths (`GetAll`, `GetQueryable`, or the Priority-1 `GetODataQueryable`); an unbound action's name matches an entity set with a registered `Post`. |
| Two bound operations of the same kind at the same binding level | `GET`/`POST /{EntitySet}/{Name}` or `.../{EntitySet}({key})/{Name}` | Duplicate bound-operation name within one profile. Refused at **bind time**, in the `Bind*` call — note that C# overloads share one method name, so two overloads cannot both be bound. |
| Entity-level bound function vs. a structural property | `GET /{EntitySet}({key})/{Name}` | A bound function's name matches a structural (non-navigation) property name. |
| Entity-level bound function vs. a navigation **route** | `GET /{EntitySet}({key})/{Name}` | A bound function's name matches a navigation property declared with **any** handler (`getAll`/`get`, `post`, `addRef`, `removeRef`, or `refTargetEntitySet`) — every such navigation registers a `GET` route, including one whose only handler is a `post`. |
| Navigation property `post` handler vs. an entity-level bound action | `POST /{EntitySet}({key})/{Name}` | A navigation property configured with a `post` handler shares a name with an entity-level bound action. |

Every comparison above is **case-insensitive** (`OrdinalIgnoreCase`), because ASP.NET Core's literal
route-segment matching is: `price` and `Price` claim one template, so a case-differing pair is a
genuine collision, not a near miss.

Navigation vs. structural-property routes never collide by construction (structural properties are computed as "every public readable CLR property minus every declared navigation property name"), so there is no guard for that pairing. A navigation declared with **no** handler registers no route at all, so a bound function may share its name — unless `ExpandPagingEnabled` and `MaxExpandTop` bring the `$expand` continuation route into existence for it, which has its own guard.
