# Deep Insert and Deep Update

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

The spec's companion feature, **deep update** — the same nested graph in a `PUT` or `PATCH` body
(OData 4.01 §11.4.3.1) — is a *separate*, Advanced-conformance feature and is
[**not supported**](#not-supported-documented-non-goals). The same opt-in decides whether such a
graph reaches your handler; with it off (the default), a nested navigation value is discarded on
every write verb rather than quietly forwarded. See
[Deep update is out of scope, and enforced](#deep-update-is-out-of-scope-and-enforced).

## Why an opt-in at all?

System.Text.Json already binds nested navigation values (e.g. `Order.Lines`) into the
deserialized model during the ordinary write pipeline — that part of deep insert "just works"
today, for free. The framework gap is safety: a `Post` handler that doesn't expect a graph (e.g.
`db.Orders.Add(order); db.SaveChanges();` written before anyone thought about nested children)
would silently persist whatever System.Text.Json happened to bind, including a half-formed child
graph the handler never validated. `AllowDeepWrites` makes that an explicit, per-profile decision
instead of an accident of what the request body happened to contain.

## Enabling it

```csharp
public class OrderProfile : EntitySetProfile<Guid, Order>
{
    public OrderProfile(AppDbContext db) : base(x => x.Id)
    {
        AllowDeepWrites = true;

        HasMany(x => x.Lines, batchGetAll: (orderIds, ct) => ...);

        Post = async (order, ct) =>
        {
            order.Id = Guid.NewGuid();
            db.Orders.Add(order);            // adds the whole graph — order + order.Lines
            await db.SaveChangesAsync(ct);   // ONE atomic write; EF Core's relationship fixup
                                             // assigns each line's OrderId from the tracked nav
            return order;
        };
    }
}
```

`AllowDeepWrites` is a `bool?` on the profile (default `false`), inheriting from
`EntitySetDefaults.AllowDeepWrites` (default `false`) when left `null` — the same
inherit-or-override pattern as `FilterEnabled`/`SelectEnabled`/etc. It is **entity-level, not
per-navigation-property**: there is no way to allow deep insert for one navigation property on an
entity but not another. Opt in per entity set (or server-wide via `builder.WithDefaults(d =>
d.AllowDeepWrites = true)`), not per property.

> **Renamed in 1.6.0.** This flag was called `AllowDeepInsert` through 1.5.0, when it governed the
> collection `POST` alone. It now governs nested-graph handling on **every** write verb — deep
> insert *and* deep update — so a name saying only "insert" described one of the two. The old name
> remains as an `[Obsolete]` forwarding property on both `EntitySetProfile` and
> `EntitySetDefaults`, reading and writing the same storage, so code compiled against 1.5.0 keeps
> working. See [#457](https://github.com/en-gen/OhData/issues/457).

## Default behavior (`AllowDeepWrites = false`)

When a write body contains nested values for **navigation properties**, the framework does not let
them reach the handler. This applies to both collection navigations (`Order.Lines`) and
single-valued navigations (`Order.Category`), and on all three entity write routes:

| Route | What happens |
|-------|--------------|
| `POST /{EntitySet}` | The navigation is set to `null` on the deserialized model **before** `Post` is invoked. |
| `PUT /{EntitySet}({key})` | The navigation is set to `null` on the deserialized model **before** `Put` is invoked. |
| `PATCH /{EntitySet}({key})` | The navigation **never enters** the `Delta<TModel>` at all. |

`PATCH` is deliberately *not* "bound and then nulled". `Delta<T>` is a change **set**: a navigation
nulled inside it would still be named by `GetChangedPropertyNames()` and still written by
`delta.Patch(existing)`, turning a graph the client sent into an unrequested relationship *clear*.
Withholding it is also what keeps the delta consistent with the subsystem it feeds —
[`Delta<TEntity>` and the delta-mapping compiler](delta-mapping.md) handle scalars and structural
properties only, and have nowhere to put a navigation.

"Navigation property" here means what `$metadata` says it is: every navigation registered via
`HasMany`/`HasOptional`/`HasRequired` (any overload), **plus** every navigation the OData convention
model builder discovered on the CLR type on its own — the ordinary
`public Customer? Customer { get; set; }` beside an `int? CustomerId`, which needs no attribute and
no fluent call to become a navigation in the EDM. Before
[#461](https://github.com/en-gen/OhData/issues/461) only the *declared* set was stripped, so a
profile that declared no navigations at all handed its `Post` handler the full nested graph despite
the flag being `false`. All three routes share that one set, so the two halves cannot drift.

```csharp
// AllowDeepWrites left at its default (false):
Post = async (order, ct) =>
{
    // order.Lines is null here even if the request body included a "lines" array —
    // the framework stripped it before this handler ran.
    db.Orders.Add(order);
    await db.SaveChangesAsync(ct);
    return order;
};

Patch = async (id, delta, ct) =>
{
    // delta.GetChangedPropertyNames() never contains "Lines", even if the request body
    // included a "lines" array — it was withheld before the delta was built.
    var order = await db.Orders.FindAsync([id], ct);
    if (order is null) return null;
    delta.Patch(order);
    await db.SaveChangesAsync(ct);
    return order;
};
```

Nested values for properties that are **not** navigations in the EDM (a plain `List<string> Tags`,
or a complex-typed member) are left untouched on every verb.

## Opt-in behavior (`AllowDeepWrites = true`)

The full deserialized graph — the parent plus every nested navigation value System.Text.Json
bound from the body — is passed to `Post` or `Put` as-is, and navigation members bind into the
`Patch` delta. **The handler is contractually responsible for persisting the whole graph
atomically** (e.g. one EF Core `SaveChanges` call, or an explicit transaction). The framework does
not open a transaction on the handler's behalf — profiles carry no ASP.NET/EF dependency, and the
scoped-profile pattern already hands the handler its own scoped `DbContext` to manage.

The `201 Created` response echoes whatever the handler returns, nested values serialized inline —
this is what satisfies §11.4.2.2's "return the created entity with related entities." No special
serialization logic is needed: `result` (the handler's return value) is just serialized normally,
and if the handler's return value carries populated navigation properties (as EF Core entities
typically do after `SaveChanges`), those appear in the response body automatically.

The `PUT`/`PATCH` `200` response is **not** widened to match: it omits un-expanded navigations
unconditionally, as every read of the same type does ([#240](https://github.com/en-gen/OhData/issues/240)).
Opting in changes what the handler *receives* on those verbs, not what the response *echoes*.

`Prefer: return=minimal` behaves exactly as it does for an ordinary write: `204 No Content`
with `Location`/`OData-EntityId`/`Preference-Applied` headers, no body. The handler still receives
and persists the full graph — only the *response* is suppressed.

## Limiting request-body size (#203)

Deep-insert graphs are the largest bodies OhData typically accepts, so a request-body-size limit is
the natural guard. Set `MaxRequestBodyBytes` — globally via `WithDefaults`, or per entity set on the
profile (the profile value overrides the global default):

```csharp
builder.Services.AddOhData(o => o
    .WithDefaults(d => d.MaxRequestBodyBytes = 1_000_000) // 1 MB app-wide default
    .AddEntitySetProfile<OrderProfile>());

public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile()
    {
        MaxRequestBodyBytes = 4_000_000; // this set accepts up to 4 MB (large deep-insert graphs)
        // ...
    }
}
```

A write request (`POST`/`PUT`/`PATCH`, including navigation/`$ref`/property/action variants) whose
body exceeds the limit is rejected with `413 Payload Too Large` and the OData error envelope, before
the body is deserialized. Enforcement is twofold: an oversized `Content-Length` is rejected up front,
and the per-request Kestrel `MaxRequestBodySize` is set so a chunked / no-`Content-Length` body is
bounded during read (its overflow is mapped to the same `413`). The default is `null` — **no
OhData-level limit**, leaving the host's Kestrel default (~30 MB) in effect.

## `@odata.bind` — not supported

`prop@odata.bind` (JSON format §8.5 — link to an **existing** entity instead of creating a new
one) is not implemented. If the annotation appears anywhere in a `POST` body (top level
or nested inside a deep-insert child), the framework rejects the request with
`501 Not Implemented` rather than silently ignoring it:

```json
{
  "error": {
    "code": "NotImplemented",
    "message": "'@odata.bind' is not supported for POST /Orders. Use the $ref endpoints to link an existing entity, or enable AllowDeepWrites to create nested related entities inline (OData §11.4.2.2)."
  }
}
```

Use the [`$ref` endpoints](navigation-routing.md#ref---managing-links-between-entities) to link
an existing entity to a parent instead.

The other write routes answer `501` too — `PUT`, `PATCH`, the navigation-`POST` create route, the
structural-property writes, and each bound/unbound action parameter — with a shorter message that
does not mention `AllowDeepWrites`. That flag decides whether a nested graph the client *sent*
reaches the handler; `@odata.bind` sends no graph, so enabling it would not make such a request
work on any verb. That holds on **every** registration, not only one whose EDM declares an open
complex type. Until [#456](https://github.com/en-gen/OhData/issues/456) the check sat behind the
open-type gate, so on the majority of registrations it never ran and the annotation was accepted
with `200`/`201` and discarded — a request to link a relationship reporting success while doing
nothing.

## Deep update is out of scope, and enforced

Deep update — a nested graph in a `PUT` or `PATCH` body, OData 4.01 §11.4.3.1 — is a **separate
named feature** from deep insert, an Advanced-conformance item, and it is not implemented. OhData
does not create, update or delete related entities from an update body under any setting.

Through 1.5.0 that was a documented statement rather than an enforced one: `AllowDeepInsert`
applied on the collection `POST` alone, so System.Text.Json bound the nested values anyway and
`PUT` forwarded them to the handler while `PATCH` bound them into the `Delta<TModel>`. A handler
doing `db.Update(model); SaveChanges();` on a `PUT` it never expected to carry a graph could
therefore persist part of one — the exact hazard the flag exists to prevent, on the two verbs it
did not cover. [#457](https://github.com/en-gen/OhData/issues/457) closed that: the same strip now
runs on all three routes, so what the docs say and what the server does agree.

If you want a nested graph on `PUT`/`PATCH`, set `AllowDeepWrites = true` and own it in your
handler — the framework passes the graph through and does nothing else with it (no transaction, no
relationship fixup, no `$metadata` advertisement).

## Response semantics

| Condition | Response |
|-----------|----------|
| Success, no `Prefer` header | `201 Created` — body is whatever `Post` returned, serialized as-is (nested navigation values included when `AllowDeepWrites = true` and the handler populated them) |
| Success, `Prefer: return=minimal` | `204 No Content` with `Location`/`Content-Location`/`OData-EntityId`/`Preference-Applied` — same as a non-deep-insert `POST` |
| `@odata.bind` present anywhere in the body | `501 Not Implemented` (OData error, `code: "NotImplemented"`) |
| Malformed / empty JSON body | `400 Bad Request` (OData error) |
| Non-JSON `Content-Type` | `415 Unsupported Media Type` |
| `Post` handler returns `null` | `400 Bad Request` (`"Post handler returned null."`) — same as today |

## Not supported (documented non-goals)

- **Per-navigation-property granularity** — `AllowDeepWrites` is a single entity-level switch;
  there is no `HasMany(..., allowDeepWrites: true)` for allowing deep insert on one navigation but
  not another on the same entity.
- **`@odata.bind`** — see above; use `$ref` to link existing entities.
- **Deep update** — nested graphs in `PUT`/`PATCH` (OData 4.01 §11.4.3.1) are out of scope; deep
  insert only applies to `POST`. This is now **enforced** rather than only stated — see
  [Deep update is out of scope, and enforced](#deep-update-is-out-of-scope-and-enforced).
- **Capabilities-vocabulary advertisement** of deep-insert support (`InsertRestrictions`) — a 4.01
  metadata nicety, not built.
