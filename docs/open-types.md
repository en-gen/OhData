# Open Types (dynamic property bags)

An OData **open type** carries caller-supplied properties that are not declared in the EDM. OhData
supports this on **complex types**: give the type an `IDictionary<string, object?>` member and its
entries are written and read as **siblings** of the declared properties — never nested under the
member's own name.

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

There is no opt-in switch. A complex type with a dictionary member *is* an open type — that is
what the CSDL has always said; this only makes the JSON agree with it.

## What is supported

| Surface | Behavior |
|---|---|
| `GET /Set` and `GET /Set({key})` | Dynamic keys flat, alongside declared properties |
| `GET /Set({key})/{ComplexProp}` and `$expand` targets | Same |
| `POST` / `PUT` | Undeclared keys inside a complex value bind into the dictionary and reach the handler |
| `PATCH` | Same; undeclared keys survive a request that also patches declared properties |
| `PUT`/`PATCH /Set({key})/{ComplexProp}` | Same (the whole complex value is replaced) |
| `$select=<ComplexProp>` | The container is preserved in full — dynamic keys are not stripped |
| `$metadata` | `OpenType="true"`; container omitted from the declared properties |
| Inheritance | A derived complex type inherits its base's container |

Values arrive in the dictionary as `JsonElement` (System.Text.Json's representation for an
`object`-typed slot), which is what a handler should expect when reading them.

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

- A container whose CLR type `System.Text.Json` cannot use as extension data (not assignable to
  `IDictionary<string, object>` / `IDictionary<string, JsonElement>`, or not both readable and
  writable) is left alone — that type keeps serializing exactly as it did before.
- A derived type that *shadows* the container with `new` is not matched; only the exact member the
  EDM designated is flattened.
- `Ignore(x => x.Container)` still wins: the member is removed from the contract entirely, so
  nothing is flattened and the bag does not appear.

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
(`JsonTypeInfo` is cached on the options instance), never per request. When a model declares no
open complex type, the derived options are skipped entirely and the pipeline is reference-identical
to before.
