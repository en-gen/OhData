# Ignoring Properties

`Ignore(...)` excludes model properties from the entire OData surface without touching the CLR
type — no `[JsonIgnore]`, no DTO split. The profile, not the POCO, defines what is exposed.

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        Ignore(x => x.CostBasis, x => x.InternalNotes);
        GetById = ...;
    }
}
```

## What "ignored" means

Handlers and the data layer still see the complete CLR model. The OData surface hides the
property everywhere:

| Surface | Behavior |
|---|---|
| `$metadata` (CSDL) | Property omitted |
| `$select` / `$filter` / `$orderby` / `$expand` | `400` — same error as any unknown property |
| Property routes (`GET/PUT/PATCH/DELETE /Set({key})/{Prop}`, `/$value`) | Not registered → `404` |
| Response bodies (collection, single, navigation, `$expand`-nested) | Member omitted |
| POST / PUT request bodies | Member not bound — silently skipped like an unknown member |
| PATCH request bodies | Member not in the `Delta<TModel>` |

An `$expand`-nested child hides *its own* profile's ignored properties automatically.

## Rules

- **Expression selectors only** (`x => x.Prop`) — the member must exist on the model, so typos
  are compile errors. Multiple calls accumulate; duplicates are harmless.
- **The key property cannot be ignored** (`ArgumentException` at the `Ignore` call).
- **A navigation property cannot be ignored.** Declaring the same property in `Ignore(...)` and
  `HasMany`/`HasOptional`/`HasRequired` (either order) throws `InvalidOperationException` at
  startup.
- **Entity sets sharing a CLR model type must declare identical ignore sets.** Suppression is
  keyed by CLR type across a registration, so `app.MapOhData()` throws at startup if two
  profiles over the same type disagree. Separate registrations (`AddOhData("v1", ...)` /
  `AddOhData("v2", ...)`) are independent — v2 may expose a property v1 ignores.
- **`AdvancedConfigure`** ejects the automatic EDM removal like all automatic EDM config — call
  `configuration.EntityType.Ignore(...)` yourself. Route suppression, wire suppression, and the
  validations above still apply.
- **ETags:** an ignored property MAY participate in `UseETag(...)` — useful for row-version
  columns that should never be exposed.
- **Navigation-only types** (a related type with no profile of its own) have no `Ignore`
  surface; give the type a profile if its wire shape needs trimming.

## Performance

Wire suppression uses a `JsonTypeInfoResolver` modifier baked into one derived
`JsonSerializerOptions` per registration. The modifier runs once per type (cached), so steady
state serializes *fewer* members than an un-ignored model — measured at 0.82× baseline time and
0.81× allocations for a 100-entity page ([#226](https://github.com/en-gen/OhData/issues/226) has
the full A/B table). When no profile ignores anything, the pipeline is byte-identical to before.

## Limitations

- The OpenAPI/NSwag companion packages generate schemas from CLR types and currently still list
  ignored properties in generated documents (runtime behavior is unaffected — ignored values
  never cross the wire). Tracked as a follow-up alongside the other doc-reflection items.
