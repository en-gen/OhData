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
  validations above still apply. **This has a security consequence — see
  [below](#ignore-under-advancedconfigure-is-a-value-oracle).**
- **ETags:** an ignored property MAY participate in `UseETag(...)` — useful for row-version
  columns that should never be exposed.
- **Navigation-only types** (a related type with no profile of its own) have no `Ignore`
  surface; give the type a profile if its wire shape needs trimming.

## `Ignore()` under `AdvancedConfigure` is a value oracle

> **Security warning.** If a property is ignored for **security** rather than tidiness, do not
> combine `Ignore(...)` with an `AdvancedConfigure` override unless the override re-applies the EDM
> removal by hand. ([#489](https://github.com/en-gen/OhData/issues/489))

`Ignore()` withholds a property on **two** levels:

| Half | Where it comes from | What it does |
|---|---|---|
| EDM removal | rides the configurator pipeline in `VisitModelBuilder` | property leaves `$metadata` and stops being a valid query identifier |
| Runtime suppression | applied from the profile's ignored-name set | no property routes, omitted from every response body, never bound from a write body, never in a `Delta<TModel>` |

Overriding [`AdvancedConfigure`](architecture.md#advancedconfigure---full-edm-control) **ejects the
EDM half** — that is the whole point of the hatch, and it ejects `HasMany`/`HasOptional`/
`HasRequired` alongside it. The runtime half still applies. So the property becomes *withheld but
addressable*:

```
GET /Widgets                              ->  200, Secret absent from every row
GET /Widgets?$filter=Secret eq 'abc'      ->  200, N rows      ← the value is discoverable
GET /$metadata                            ->  <Property Name="Secret" Type="Edm.String" … />
```

The response never carries the value, but a client can **probe it one predicate at a time**, and
`$metadata` discloses its name and type outright. Compare the ordinary case, where the EDM removal
makes the property indistinguishable from one that never existed: `$filter` naming it fails at parse
with the same *"could not find a property named…"* a genuinely nonexistent property produces, so the
`400` cannot confirm existence.

Two things widen or narrow this in practice:

- `$filter`/`$orderby`/`$select` are only *live* to the extent the override re-enabled them (taking
  the hatch also drops OhData's automatic `Filter()`/`OrderBy()`/`Select()` calls, so the documented
  `config.EntityType.Select().OrderBy().Filter()` line is what turns the oracle on).
- The `$metadata` disclosure happens either way, capabilities or not.

**The fix in your own code** is one line inside the override:

```csharp
protected override void AdvancedConfigure(EntitySetConfiguration<Widget> configuration)
{
    configuration.EntityType.HasKey(x => x.Id);
    configuration.EntityType.Select().OrderBy().Filter();
    configuration.EntityType.Ignore(x => x.Secret);   // ← re-apply the EDM half
}
```

OhData does not do this for you: re-imposing `Ignore()` on top of an override would defeat the hatch,
and singling out `Ignore()`'s configurator while leaving the navigation configurators ejected would
make the pipeline's membership depend on severity rather than on a rule. Instead, `app.MapOhData()`
emits **one `Warning` per affected property** naming the entity set, the property, and this remedy —
the same shape the open-type wire-shape warning uses. Re-apply the removal and the warning goes away,
because it is gated on the EDM as actually built, not on the presence of the override.

## Performance

Wire suppression uses a `JsonTypeInfoResolver` modifier baked into one derived
`JsonSerializerOptions` per registration. The modifier runs once per type (cached), so steady
state serializes *fewer* members than an un-ignored model — measured at 0.82× baseline time and
0.81× allocations for a 100-entity page ([#226](https://github.com/en-gen/OhData/issues/226) has
the full A/B table). When no profile ignores anything, the pipeline is byte-identical to before.

## OpenAPI / Swagger documents

As of [#228](https://github.com/en-gen/OhData/issues/228) the companion packages omit ignored
properties from generated schemas, so documents match the real wire shape. Each doc stack has a
schema-level hook to register alongside its operation-level one:

- **Microsoft.AspNetCore.OpenApi:** `o.AddSchemaTransformer<OhDataOpenApiSchemaTransformer>()` —
  see [openapi.md](openapi.md#ignored-properties-omitted-from-schemas)
- **NSwag:** `s.SchemaSettings.SchemaProcessors.Add(new OhDataNSwagSchemaProcessor(sp))` — see
  [nswag.md](nswag.md#ignored-properties-omitted-from-schemas)
- **Swashbuckle:** `c.SchemaFilter<OhDataSwaggerSchemaFilter>()` — see
  [swashbuckle.md](swashbuckle.md#ignored-properties-and-schema-casing)

One caveat: an OpenAPI document holds a single component schema per CLR type, so if separate
registrations expose the same model type with *different* ignore sets (legal — see Rules above),
the schemas omit the **union** of the sets, preferring to under-document a property one
registration exposes over listing a name another registration deliberately hides.

Those same schema hooks also rename each surviving property key to OhData's response casing
(PascalCase by default; see [query-options.md → JSON property casing](query-options.md#json-property-casing)),
so the documented casing matches the wire.
