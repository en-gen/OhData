# Open Types (dynamic property bags)

An OData **open type** carries caller-supplied properties that are not declared in the EDM. OhData
supports this on **complex types**: give the type an `IDictionary<string, object?>` member, opt in
with `WithOpenTypes()`, and its entries are written and read as **siblings** of the declared
properties — never nested under the member's own name.

**No attributes. No model changes.** Support is driven entirely by the EDM, so a model published
in a shared contract package works as-is:

```csharp
public record ExternalReference
{
    public Guid Id { get; set; }
    public string Source { get; set; } = "";
    public string Xref { get; set; } = "";
    public ExternalReferenceMetadata? Metadata { get; set; }
}

public record ExternalReferenceMetadata
{
    public IDictionary<string, object?>? KeyValuePairs { get; set; }
}
```

```csharp
services.AddOhData(o => o
    .WithOpenTypes()
    .AddEntitySetProfile<ExternalReferenceProfile>());
```

```jsonc
// GET /odata/ExternalReferences
{
  "@odata.context": "…/$metadata#ExternalReferences",
  "value": [
    {
      "Id": "1111…",
      "Source": "LeaseAccounting",
      "Xref": "xref-1",
      "Metadata": { "organizationCreatedDate": "2026-01-01T00:00:00.0000000+00:00", "tier": 3 }
    }
  ]
}
```

`KeyValuePairs` appears nowhere on the wire, and nowhere in `$metadata`.

## Opt in deliberately — this changes the wire shape

`WithOpenTypes()` is **off by default**, and turning it on is a breaking change for any complex
type in the model that has a dictionary member.

**Does this affect me?** Ask one question: *do any of your complex types have an
`IDictionary<string, object>` member?* If none do, enabling this is a no-op — the registration's
serializer options are not even derived, and every response is byte-identical. If any do, read on.

Once the container is extension data it is no longer a *declared* property. An existing client body

```jsonc
{ "Metadata": { "KeyValuePairs": { "a": 1 } } }
```

stops binding `{"a":1}` to the `KeyValuePairs` property and starts binding a **dynamic key literally
named `KeyValuePairs`** whose value is that dictionary. The handler then persists
`KeyValuePairs = { "KeyValuePairs": {"a":1} }`, and the response echo of that mis-bound value is
byte-identical to the correct one — so the corruption is invisible from the wire. Migrate clients
and stored data deliberately; do not flip this on a live surface and watch the responses.

## How the container is discovered

`ODataConventionModelBuilder` — the builder OhData already uses to produce the EDM — infers a
dynamic-property container from an `IDictionary<string, object>`-assignable member. It marks the
containing type `OpenType="true"`, omits the member from the declared properties, and records the
backing `PropertyInfo` on the model as a `DynamicPropertyDictionaryAnnotation`.

OhData reads that annotation back at `MapOhData()` and marks exactly that member as
`System.Text.Json` extension data on the registration's `JsonSerializerOptions`. **The same
registration that produces the CSDL produces the wire shape**, so the two cannot drift, and the
CLR model needs no `[JsonExtensionData]` (or any other) attribute. Nothing is matched by property
name or by convention.

```xml
<ComplexType Name="ExternalReferenceMetadata" OpenType="true" />
```

The container must have **both a getter and a setter** — `System.Text.Json` populates it on read
and enumerates it on write. The idiomatic collection-initializer shape

```csharp
public IDictionary<string, object?> Bag { get; } = new Dictionary<string, object?>();   // ✗
```

is inferred as a container by the model builder but cannot be bound into, so `MapOhData()` throws
and names the member. Give it a setter:

```csharp
public IDictionary<string, object?>? Bag { get; set; }                                   // ✓
```

## What is supported

| Surface | Behavior |
|---|---|
| `GET /Set` and `GET /Set({key})` | Dynamic keys flat, alongside declared properties |
| `GET /Set({key})/{ComplexProp}` and `$expand` targets | Same |
| `POST` / `PUT` | Undeclared keys inside a complex value bind into the dictionary and reach the handler |
| `PATCH` | Same — but the complex value is **replaced**, not merged; see below |
| `PUT`/`PATCH /Set({key})/{ComplexProp}` | Same (the whole complex value is replaced) |
| `$select=<ComplexProp>` | The container is preserved in full — dynamic keys are not stripped |
| `$metadata` | `OpenType="true"`; container omitted from the declared properties |
| Inheritance | A derived complex type inherits its base's container |

Values arrive in the dictionary as `JsonElement` (System.Text.Json's representation for an
`object`-typed slot), which is what a handler should expect when reading them.

**The container is `null`, not empty, when a body carries no undeclared keys.** `System.Text.Json`
only materialises the extension-data dictionary once the first unmatched member arrives, so a
`POST` of `{"Metadata": {}}` leaves `KeyValuePairs` `null`. Every bag read in a handler needs a null
check:

```csharp
object? tier = entity.Metadata?.KeyValuePairs?.GetValueOrDefault("tier");
```

## Dynamic property names are validated on write

A dynamic key is persisted verbatim and echoed on every later read, so an unconstrained one is a
*stored* fault against other consumers rather than a one-request nuisance: `@odata.type` inside a
complex value is what a conforming reader (`Microsoft.OData.Client` among them) uses to resolve that
value's type, and `@odata.id` is an entity reference. Nested under a declared container these are
inert payload; flattened, they are control information.

`POST`, `PUT`, `PATCH` and property-route writes therefore reject a dynamic key that is not an OData
**simple identifier** (CSDL §4.1 `odataIdentifier`): a letter or `_` followed by up to 127 letters,
digits or `_`. That rules out the empty string, `@odata.type`, `Meta@odata.count`, `has.dot`,
`has space` and `kebab-case`.

```jsonc
// POST /odata/ExternalReferences  →  400
{ "Source": "S", "Xref": "X", "Metadata": { "@odata.type": "#Evil.Type" } }
```

```jsonc
{ "error": { "code": "InvalidBody", "target": "@odata.type",
             "message": "'@odata.type' is not a valid dynamic property name. …" } }
```

The check runs only under the opt-in, and only against members that will actually land in a bag —
unknown members of a *non*-open type are ignored on binding exactly as they were before, so a client
may still send `@odata.context` at the entity root.

## `PATCH` replaces the complex value — it does not merge it

**This is the sharpest edge of the feature.** `PATCH` of a complex member deserializes that member
wholesale into the CLR property, so the value the handler receives is built *only* from what the
request restated. Everything else in the old complex value is gone — dynamic keys and declared
properties alike.

```jsonc
// stored:  "Metadata": { "Region": "eu", "keepMe": "important", "tier": 3 }
// PATCH:
{ "Metadata": { "tier": 4 } }
// result:  "Metadata": { "Region": null, "tier": 4 }        ← keepMe gone, Region nulled
```

This is **pre-existing behavior for every complex member**, not something open types introduced —
but open types widen its blast radius from "one nullable member" to "the entire caller-supplied
bag", so plan for it.

The only reading under which "undeclared keys survive a PATCH" is true is the weak one: a `PATCH`
that **omits** the complex member entirely leaves it, and everything in it, untouched.

**To change one dynamic key, read-modify-write the whole complex value:**

```csharp
// 1. read
var current = await client.GetAsync<ExternalReference>(id);
var bag = new Dictionary<string, object?>(current.Metadata?.KeyValuePairs
    ?? new Dictionary<string, object?>());

// 2. modify
bag["tier"] = 4;

// 3. write the WHOLE complex value back
await client.PatchAsync(id, new { Metadata = MergeWithDeclaredProperties(current.Metadata, bag) });
```

`PATCH` on the property route (`PATCH /Set({key})/Metadata`) is not a merge either — it returns
`400 NotSupported`. Use `PUT /Set({key})/Metadata`, which is an explicit whole-value replace.

## A bag key that collides with a declared property name

Server-side data can put a key in the bag that equals one of the complex type's own declared
property names (a handler merging a caller-supplied dictionary, for instance). Emitting both would
produce a **duplicate JSON property name** — invalid OData, and something every .NET reader tested
resolves in the *bag's* favour, making the declared value unreachable.

**Contract: the declared property wins and the colliding bag key is omitted from the response**, with
a warning logged on the `OhData` logger naming the type and the key. Nothing faults, and nothing
invalid is emitted.

```jsonc
// Meta.Channel = "declared";  Meta.KeyValuePairs = { "Channel": "fromBag", "ok": 1 }
{ "Channel": "declared", "ok": 1 }
```

The match is **ordinal**: a key differing only in case (`channel` beside a declared `Channel`) is
kept, since it produces no duplicate key and suppressing it would be silent data loss.

Suppression works by handing the serializer a filtered clone of the container, of the container's own
runtime type. If that type cannot be instantiated (no parameterless constructor — a
`ReadOnlyDictionary`, say) the clone is impossible; the response then does contain the duplicate key
and an **error** is logged naming the type, the keys and the container type. A read is never faulted
over a data condition.

This cannot be checked at startup — the keys are dynamic — and it does not arise from a client body,
because `System.Text.Json` binds a body key matching a declared name to that declared property; it
never reaches the bag.

## What is *not* supported

### Entity-root dynamic containers

A dictionary member on an **entity** type is not flattened. `ODataConventionModelBuilder` still
consumes it (so it disappears from the CSDL) while `System.Text.Json` still writes it as a
declared property — an EDM/wire mismatch that predates this feature and is not fixed by it:

```jsonc
// entity-level container — NOT flattened
{ "Id": 1, "Name": "n", "Extras": { "dyn": "v" } }
```

The write half is the blocker: `PATCH` builds its `Delta<TModel>` by resolving each body member
through the EDM/CLR property map and skipping what it cannot resolve, so a root-level undeclared
key would be silently dropped. Half-working would be worse than absent. Put the bag on a complex
type instead.

### `$filter` / `$orderby` over an individual dynamic key

Not supported, and **it does not degrade gracefully**. Microsoft's query binder builds a
property-bag indexer access for `$filter=Metadata/tier eq 3`, which faults while the expression
tree is being constructed:

```
System.ArgumentException: Method 'System.Object get_Item(System.String)' declared on type
'System.Collections.Generic.IDictionary`2[System.String,System.Object]' cannot be called with
instance of type 'System.Object'
```

Over an EF Core-backed `GetQueryable` this surfaces as a **500**, and **no query reaches the
database** — the request fails before materialization, so it is not a silent client-side
evaluation of the whole table. `$orderby` over a dynamic key faults identically. `$select` over a
dynamic key (`$select=Metadata/tier`) does *not* fault: it returns `200` and behaves as
`$select=Metadata`, emitting the whole complex value.

Filter on a **declared** property of the complex type (`$filter=Metadata/Region eq 'eu'`) — that
translates to SQL and pushes down normally.

### Other boundaries

- A container `System.Text.Json` cannot use as extension data — in practice, one with no setter —
  **fails at `MapOhData()`** with a message naming the type, the member and the fix. It is not
  skipped: the registration asked for open types explicitly, and a silent skip leaves the CSDL
  saying `OpenType="true"` while the wire nests the bag under its own name.
- A type that already carries its own `[JsonExtensionData]` member (a `JsonObject`, say — the model
  builder does not see that as a container, so both survive) would end up with two extension-data
  members, which `System.Text.Json` rejects. That is also caught at `MapOhData()`, not left to
  become a 500 on the first response.
- A derived type that *shadows* the container with `new` **is** flattened, using the shadowing
  member. `ODataConventionModelBuilder` records the derived member as that derived EDM type's own
  container, so the derived type gets its own entry.
- `Ignore(x => x.Container)` does **not** interact with this feature at all. `Ignore(...)` takes a
  root member of a profile's *entity* type, and a container lives on a *complex* type, which is
  never a profile's model type — the two never meet.

## Interaction with `$expand` and cycle safety

OhData serializes clause-bounded (`#325`/`#326`): a navigation property is never handed to
`System.Text.Json` unless the `$expand` clause asked for it, which is what makes a reference cycle
in the underlying object graph structurally unreachable.

Open types do **not** widen that. The bag's values already reached the serializer before this
feature — they were simply written one level deeper. Flattening changes only where the keys land
in the emitted JSON; it adds no object to the graph the serializer walks, applies only to complex
types (which carry no EDM navigations), and never touches an entity type or a navigation property.

Be aware of the corollary: a dynamic value is arbitrary caller-supplied CLR state, so **whatever
a handler puts in the bag is serialized as-is**. Putting a tracked entity (or anything with a
reference cycle) into a dynamic bag is a serialization fault waiting to happen — the clause-bounded
walker cannot protect a value it has no EDM description of.

## Performance

Flattening is one `JsonTypeInfoResolver` modifier baked into the registration's derived
`JsonSerializerOptions` — the same mechanism `Ignore(...)` uses. It runs once per CLR type
(`JsonTypeInfo` is cached on the options instance), never per request. When the registration has
not called `WithOpenTypes()` — or has, but the model declares no open complex type — the derived
options are skipped entirely and the pipeline is reference-identical to before.

Write-side dynamic-key validation walks the request body once, and only for a registration that
opted in; without the opt-in it is a single `bool` test and the body is never walked.
