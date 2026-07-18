# Individual Property Access

OhData supports reading a single structural property of an entity directly, without fetching the
whole entity (OData §11.2.6, JSON format Part 2 §4.6-4.7):

```
GET /{EntitySet}({key})/{Property}
GET /{EntitySet}({key})/{Property}/$value
```

Writing to an individual property is also supported (OData §11.4.9.1/.2/.3, spec items #30/#31):

```
PUT    /{EntitySet}({key})/{Property}
PATCH  /{EntitySet}({key})/{Property}
DELETE /{EntitySet}({key})/{Property}
```

See [Writing to a property](#writing-to-a-property) below.

## Enabling it

Property access is **on by default** for every entity set that configures `GetById` — no extra code required:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        GetById = (id, ct) => ...;   // enables GET /Products({key})/Name, /Price, /Description, ...
    }
}
```

Property routes ride the existing `GetById` handler — there is no separate delegate to write. A route is registered for every public, readable, non-indexer CLR property of the model **except** properties declared as navigations via `HasMany`/`HasOptional`/`HasRequired`.

To opt a single entity set out:

```csharp
protected bool? PropertyAccessEnabled { get; init; } = false;
```

or turn it off server-wide and opt individual sets back in:

```csharp
builder.WithDefaults(d => d.PropertyAccessEnabled = false);
```

`PropertyAccessEnabled` resolves to `true`/`false` the same way as `SelectEnabled`/`FilterEnabled` — a `null` profile-level value inherits the server default (`EntitySetDefaults.PropertyAccessEnabled`, default `true`).

If a profile does not configure `GetById`, no property routes are registered regardless of `PropertyAccessEnabled`.

## API documentation visibility

Property routes are numerous — up to four per structural property, per entity set (the two
reads, plus `PUT`/`PATCH`/`DELETE`) — and would otherwise dominate a generated Swagger/OpenAPI
document, drowning the primary CRUD, navigation, and bound-operation surface. So they are
**omitted from the generated API docs by default**, while remaining fully live at runtime.

This is documentation-only: the routes still respond exactly as described on this page whether or
not they appear in the docs. The default only changes what ASP.NET Core's ApiExplorer enumerates
(via `ExcludeFromDescription`), which is the shared upstream for every doc stack —
Microsoft.AspNetCore.OpenApi, Swashbuckle, and NSwag alike — so one setting covers all three.

To include property routes in the generated docs, set `PropertyRouteDocsEnabled`:

```csharp
// Per profile:
protected bool? PropertyRouteDocsEnabled { get; init; } = true;

// Or server-wide:
builder.WithDefaults(d => d.PropertyRouteDocsEnabled = true);
```

`PropertyRouteDocsEnabled` resolves the same way as the other capability flags — a `null`
profile-level value inherits the server default (`EntitySetDefaults.PropertyRouteDocsEnabled`,
default `false`). The flag only has an effect when property routes are actually registered (i.e.
`PropertyAccessEnabled` resolves `true` and the required handler is configured); otherwise there
is nothing to document and it is inert. It covers all property routes together — reads, writes,
and the immutable-key stubs.

## Response shape

`GET /{EntitySet}({key})/{Property}`:

```json
{
  "@odata.context": "https://host/odata/$metadata#Products(1)/Name",
  "value": "Widget"
}
```

| Condition | Status |
|---|---|
| Entity not found | `404 Not Found` |
| Property value is `null` | `204 No Content` (§11.2.6 — a single-valued null property) |
| Otherwise | `200 OK` with the envelope above |
| Unknown property name | `404 Not Found` (no route registered for that segment) |

## Raw value: `/$value`

`GET /{EntitySet}({key})/{Property}/$value` returns the property's value with no JSON envelope and no quoting — `text/plain` for primitives (formatted with invariant culture; `bool` as lowercase `true`/`false`; date/time types in ISO-8601 round-trip form), `application/octet-stream` for `byte[]`.

| Condition | Status |
|---|---|
| Entity not found | `404 Not Found` |
| Property value is `null` | `404 Not Found` (Part 2 §4.7 — the raw value of a null property does not exist) |
| Property is a complex type | `400 Bad Request` (no raw representation — see below) |
| Otherwise | `200 OK`, raw body |

## Complex properties

`GET /{EntitySet}({key})/{ComplexProperty}` works normally and returns the nested object in the envelope's `value`. `GET .../{ComplexProperty}/$value` always returns `400 Bad Request` — a complex value has no primitive raw-value representation.

A property is "complex" if its CLR type is anything other than a string, numeric type, `bool`, `Guid`, `DateTime`/`DateTimeOffset`/`DateOnly`/`TimeOnly`/`TimeSpan`, `byte[]`, or an enum (nullable variants included).

## ETags

When the entity set has `UseETag` configured, `GET /{EntitySet}({key})/{Property}` sets the entity's `ETag` response header (the same value `GET /{EntitySet}({key})` would produce) and honors `If-None-Match` — a match returns `304 Not Modified`. `/$value` does not set an ETag header.

## Authorization

Property routes inherit the entity set's `RequireAuthorization()`/`RequireRoles()` configuration, same as every other route for that entity set — there is no separate opt-in.

## Route-collision validation

Structural properties are computed as "every CLR property minus every navigation property name", so a property route can never collide with a navigation route by construction. The one real collision risk is an **entity-level bound function** (also `GET /{EntitySet}({key})/{name}`) sharing a name with a structural property. OhData detects this at startup — when `app.MapOhData()` runs — and throws `InvalidOperationException` naming the entity set and the conflicting name, rather than letting two routes register the same `(template, method)` pair (which would otherwise fail unpredictably at request time). Rename the bound function or the property to resolve it.

## Writing to a property

`PUT`/`PATCH`/`DELETE /{EntitySet}({key})/{Property}` (OData §11.4.9.1/.2/.3) let a client update a
single property without sending the whole entity. There is **no new handler delegate** — property
writes are built as a one-property `Delta<TModel>` and handed to the profile's existing `Patch`
handler, which already owns fetch-existing → apply → persist.

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile(AppDbContext db) : base(x => x.Id)
    {
        GetById = (id, ct) => db.Products.FindAsync(id, ct).AsTask();
        Patch   = (id, delta, ct) =>
        {
            var e = db.Products.Find(id);
            if (e is null) return Task.FromResult<Product?>(null);
            delta.Patch(e);
            db.SaveChanges();
            return Task.FromResult<Product?>(e);
        };
        // Patch enables both PATCH /Products({key}) *and* the property-write routes below.
    }
}
```

**Routes are registered when `PropertyAccessEnabled` resolves `true` AND `Patch` is configured.**
Unlike property *read* (which requires `GetById`), property *write* does not require `GetById` —
`Patch` does its own fetching. If a profile has `Put` but no `Patch`, no property-write routes are
registered (property writes are a partial update; `Put` is a full-entity replace and doesn't fit
the single-property shape). There is no `GetById`+`Put` composition path.

### Request body

```
PUT /{EntitySet}({key})/{Property}
Content-Type: application/json

{ "value": <newValue> }
```

`PATCH` uses the identical body shape. For a **primitive** property, `PATCH` is handled identically
to `PUT` (a primitive has no partial state to merge). For a **complex** property, `PUT` performs a
full replacement of the nested object; `PATCH` (partial merge into the existing complex value) is
documented non-support for 1.0.0 — see below.

### Response / status semantics

`PUT` / `PATCH /{EntitySet}({key})/{Property}`:

| Condition | Status |
|---|---|
| Success | `204 No Content` |
| Entity not found (`Patch` returns `null`) | `404 Not Found` |
| Entity not found **and** `If-Match` header present | `412 Precondition Failed` (the ETag existence check runs before the write — see below) |
| Target is the entity's key property | `400 Bad Request` (the key is immutable — §11.4.9) |
| Unknown property name | `404 Not Found` (no route registered for that segment) |
| Request body is not a JSON object, or missing the `value` member | `400 Bad Request` |
| `value` cannot be converted to the property's CLR type | `400 Bad Request` |
| `value` is `null` and the property is not nullable | `400 Bad Request` |
| `PATCH` targeting a complex property | `400 Bad Request` (`code: "NotSupported"` — see below) |
| `Content-Type` is not `application/json` | `415 Unsupported Media Type` |
| `If-Match` set and doesn't match the current ETag | `412 Precondition Failed` |

`DELETE /{EntitySet}({key})/{Property}` (§11.4.9.3 — sets the property to `null`):

| Condition | Status |
|---|---|
| Success | `204 No Content` |
| Entity not found (`Patch` returns `null`) | `404 Not Found` |
| Entity not found **and** `If-Match` header present | `412 Precondition Failed` (see below) |
| Target is the entity's key property | `400 Bad Request` |
| Property is not nullable | `400 Bad Request` — checked before touching the data source at all |
| Unknown property name | `404 Not Found` |
| `If-Match` set and doesn't match the current ETag | `412 Precondition Failed` |

The `404`-vs-`412` precedence follows the same rule as entity-level writes (see
[etags.md](etags.md#conditional-write-operations)): when `If-Match` is present, OhData checks for
the entity's existence *before* attempting the write, so a missing entity with `If-Match` set —
including `If-Match: *` — returns `412`, never `404`. Without an `If-Match` header, a missing
entity returns the plain `404` from the property-write handler itself. This check only runs when
`GetById` is also configured (`UseETag` + `Patch`-without-`GetById` profiles skip `If-Match`
checking entirely on property-write routes, same as entity-level writes — see
[etags.md](etags.md#conditional-write-operations)).

All error responses use the standard OData error envelope: `{"error":{"code":...,"message":...,"target":...}}`.

Every response is `204 No Content` on success — property-write routes do not honor
`Prefer: return=representation` (unlike entity-level `PUT`/`PATCH`); they always return an empty
body.

### Complex properties

`PUT /{EntitySet}({key})/{ComplexProperty}` replaces the entire nested object — send the full
complex value under `"value"`. `PATCH` on a complex property is **not supported**: partial merge
into an existing complex value was judged low-value relative to its complexity for 1.0.0, so it
returns `400 Bad Request` with `code: "NotSupported"` rather than silently guessing at a merge
strategy or a bare, envelope-less `405`. Use `PUT` to replace the whole value instead.
`DELETE` on a nullable complex property sets it to `null`, same as any other nullable property.

### ETags

When `UseETag` is configured, all three verbs honor `If-Match` exactly like the entity-level
`PUT`/`PATCH`/`DELETE` routes (via the same `CheckETagAsync` check) — a mismatch returns
`412 Precondition Failed`. On success, the `ETag` response header is set from the entity `Patch`
returns.

### Authorization

Property-write routes inherit the entity set's `RequireAuthorization()`/`RequireRoles()`
configuration, same as property reads and every other route for the entity set.

### Key property

The entity's key property gets `PUT`/`PATCH`/`DELETE` stub routes that always return
`400 Bad Request` (`target` set to the property name) rather than falling through to an unmatched
route — the key is structurally immutable per §11.4.9.

## Non-goals

- **`PATCH` (partial merge) on a complex property** — `PUT` full-replacement is supported; merge is
  not. Returns `400 Bad Request`.
- **`PUT /{EntitySet}({key})/{Property}/$value`** (raw-value write) — only the enveloped
  `PUT .../{Property}` form (`{"value": ...}`) is supported. Raw `/$value` remains read-only.
- **`POST` of a new related entity via a navigation property, and deep insert** — separate design
  items, unrelated to property access. See [navigation-routing.md](navigation-routing.md#creating-a-related-entity--post-parentskeychildren)
  and [deep-insert.md](deep-insert.md) respectively.
