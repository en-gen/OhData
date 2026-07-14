# Individual Property Access

OhData supports reading a single structural property of an entity directly, without fetching the
whole entity (OData §11.2.6, JSON format Part 2 §4.6-4.7):

```
GET /{EntitySet}({key})/{Property}
GET /{EntitySet}({key})/{Property}/$value
```

This is **read-only** for now — `PUT`/`PATCH`/`DELETE` on an individual property (spec items #30/#31) are a planned follow-up PR and are not registered by this version.

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

## Non-goals for this PR

- Writing to an individual property (`PUT`/`PATCH`/`DELETE /{EntitySet}({key})/{Property}`) — planned follow-up.
- `POST` of a new related entity via a navigation property, and deep insert — separate design items, unrelated to property access.
