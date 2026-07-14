# Deep Insert

OhData supports creating related entities inline with a single `POST /{EntitySet}` request
(OData §11.4.2.2 — deep insert):

```json
POST /odata/Orders
{
  "customerName": "Alice",
  "lines": [
    { "productName": "Widget", "quantity": 2, "unitPrice": 9.99 }
  ]
}
```

No new route and no new handler delegate — deep insert rides the existing `POST /{EntitySet}`
route and the existing `Post` handler. What changes is **what the handler receives**, controlled
by a per-profile opt-in.

## Why an opt-in at all?

System.Text.Json already binds nested navigation values (e.g. `Order.Lines`) into the
deserialized model during the ordinary `POST` pipeline — that part of deep insert "just works"
today, for free. The framework gap is safety: a `Post` handler that doesn't expect a graph (e.g.
`db.Orders.Add(order); db.SaveChanges();` written before anyone thought about nested children)
would silently persist whatever System.Text.Json happened to bind, including a half-formed child
graph the handler never validated. `AllowDeepInsert` makes that an explicit, per-profile decision
instead of an accident of what the request body happened to contain.

## Enabling it

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        AllowDeepInsert = true;

        HasMany(x => x.Lines, batchGetAll: (orderIds, ct) => ...);

        Post = (order, _) =>
        {
            order.Id = Guid.NewGuid();
            db.Orders.Add(order);       // adds the whole graph — order + order.Lines
            db.SaveChanges();           // ONE atomic write; EF Core's relationship fixup
                                         // assigns each line's OrderId from the tracked nav
            return Task.FromResult<Order?>(order);
        };
    }
}
```

`AllowDeepInsert` is a `bool?` on the profile (default `false`), inheriting from
`EntitySetDefaults.AllowDeepInsert` (default `false`) when left `null` — the same
inherit-or-override pattern as `FilterEnabled`/`SelectEnabled`/etc. It is **entity-level, not
per-navigation-property**: there is no way to allow deep insert for one navigation property on an
entity but not another. Opt in per entity set (or server-wide via `builder.WithDefaults(d =>
d.AllowDeepInsert = true)`), not per property.

## Default behavior (`AllowDeepInsert = false`)

When a `POST` body contains nested values for **declared navigation properties** (properties
registered via `HasMany`/`HasOptional`/`HasRequired`, any overload), the framework strips them —
sets them to `null` — on the deserialized model **before** invoking `Post`. This applies to both
collection navigations (`Order.Lines`) and single-valued navigations (`Order.Category`).

```csharp
// AllowDeepInsert left at its default (false):
Post = (order, _) =>
{
    // order.Lines is null here even if the request body included a "lines" array —
    // the framework stripped it before this handler ran.
    db.Orders.Add(order);
    db.SaveChanges();
    return Task.FromResult<Order?>(order);
};
```

Nested values for properties that are **not** declared as navigations (a plain `List<string>
Tags`, for example) are left untouched — only CLR properties registered via
`HasMany`/`HasOptional`/`HasRequired` are stripped.

## Opt-in behavior (`AllowDeepInsert = true`)

The full deserialized graph — the parent plus every nested navigation value System.Text.Json
bound from the body — is passed to `Post` as-is. **The handler is contractually responsible for
persisting the whole graph atomically** (e.g. one EF Core `SaveChanges` call, or an explicit
transaction). The framework does not open a transaction on the handler's behalf — profiles carry
no ASP.NET/EF dependency, and the scoped-profile pattern already hands the handler its own scoped
`DbContext` to manage.

The `201 Created` response echoes whatever the handler returns, nested values serialized inline —
this is what satisfies §11.4.2.2's "return the created entity with related entities." No special
serialization logic is needed: `result` (the handler's return value) is just serialized normally,
and if the handler's return value carries populated navigation properties (as EF Core entities
typically do after `SaveChanges`), those appear in the response body automatically.

`Prefer: return=minimal` behaves exactly as it does for a non-deep-insert `POST`: `204 No Content`
with `Location`/`OData-EntityId`/`Preference-Applied` headers, no body. The handler still receives
and persists the full graph — only the *response* is suppressed.

## `@odata.bind` — not supported

`prop@odata.bind` (JSON format §8.5 — link to an **existing** entity instead of creating a new
one) is not implemented for 1.0.0. If the annotation appears anywhere in a `POST` body (top level
or nested inside a deep-insert child), the framework rejects the request with
`501 Not Implemented` rather than silently ignoring it:

```json
{
  "error": {
    "code": "NotImplemented",
    "message": "'@odata.bind' is not supported for POST /Orders. Use the $ref endpoints to link an existing entity, or enable AllowDeepInsert to create nested related entities inline (OData §11.4.2.2)."
  }
}
```

Use the [`$ref` endpoints](navigation-routing.md#ref---managing-links-between-entities) to link
an existing entity to a parent instead.

## Response semantics

| Condition | Response |
|-----------|----------|
| Success, no `Prefer` header | `201 Created` — body is whatever `Post` returned, serialized as-is (nested navigation values included when `AllowDeepInsert = true` and the handler populated them) |
| Success, `Prefer: return=minimal` | `204 No Content` with `Location`/`Content-Location`/`OData-EntityId`/`Preference-Applied` — same as a non-deep-insert `POST` |
| `@odata.bind` present anywhere in the body | `501 Not Implemented` (OData error, `code: "NotImplemented"`) |
| Malformed / empty JSON body | `400 Bad Request` (OData error) |
| Non-JSON `Content-Type` | `415 Unsupported Media Type` |
| `Post` handler returns `null` | `400 Bad Request` (`"Post handler returned null."`) — same as today |

## Not supported (documented non-goals for 1.0.0)

- **Per-navigation-property granularity** — `AllowDeepInsert` is a single entity-level switch;
  there is no `HasMany(..., allowDeepInsert: true)` for allowing deep insert on one navigation but
  not another on the same entity.
- **`@odata.bind`** — see above; use `$ref` to link existing entities.
- **Deep update** — nested graphs in `PUT`/`PATCH` (OData 4.01 Minimal #21) are out of scope; deep
  insert only applies to `POST`.
- **Capabilities-vocabulary advertisement** of deep-insert support (`InsertRestrictions`) — a 4.01
  metadata nicety, not built.
