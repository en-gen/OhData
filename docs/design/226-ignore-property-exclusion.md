# #226 — `Ignore(x => x.Property)`: excluding a model property from the OData surface

- **Issue:** [#226](https://github.com/en-gen/OhData/issues/226)
- **Milestone:** 1.5.0
- **Status:** Approved design (2026-07-18)

## Problem

A profile author often has CLR model properties that must never cross the wire — cost bases,
internal notes, soft-delete flags, tenant discriminators. Today the only options are polluting the
model with `[JsonIgnore]` (which is global, not per-entity-set, and does nothing for `$metadata`
or query options) or maintaining a separate DTO. Neither fits OhData's core stance that **the
profile, not the POCO, defines the OData surface**.

## API

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

New protected method on `EntitySetProfile<TKey, TModel>`:

```csharp
protected void Ignore(params Expression<Func<TModel, object?>>[] properties)
```

- Expression overload **only** (no string overload). Unlike `FilterProperties`/`SelectProperties` —
  where the string overload exists for forward-compat with renamed wire names — `Ignore` targets a
  CLR member that must exist on `TModel`; the expression form makes typos a compile error. A string
  overload can be added later without breaking anything (YAGNI).
- Reuses the existing `ExtractNames` helper: direct member access required, boxing `Convert` nodes
  stripped, nested access (`x => x.Child.Name`) rejected with the same `ArgumentException`.
- Multiple calls **accumulate** (set semantics, duplicates harmless) — consistent with
  `HasMany`/`BindFunction` accumulation, unlike the last-wins allowlist setters.
- Guarded by `ThrowIfSealed()` like every other mutating config method.

## Semantics: full hide

An ignored property is suppressed at **every** point the framework exposes the model. Handlers and
the data layer still see the complete CLR model — only the OData surface hides it.

| # | Surface | Behavior | Mechanism |
|---|---|---|---|
| 1 | `$metadata` (CSDL) | Property omitted | EDM `Ignore` configurator |
| 2 | `$select`/`$filter`/`$orderby`/`$expand` | `400` — same error as any unknown property name | Falls out of EDM removal |
| 3 | Property routes (`GET/PUT/PATCH/DELETE /Set({key})/{Prop}`, `/$value`) | Not registered → `404` | Excluded from `BuildStructuralProperties` |
| 4 | Response bodies — collection, single-entity, navigation GET, `$expand`-nested, nav-POST echo, `Prefer: return=representation` | Property omitted | Derived `JsonSerializerOptions` (see below) |
| 5 | Request bodies — POST/PUT | Member not bound (silently skipped, exactly like an unknown member) | Same derived options on the deserialize path |
| 6 | PATCH (`Delta<TModel>` build) | Skipped — same silent-skip as an unknown member today | Explicit name check in the delta loop (it is CLR-reflection-driven, **not** EDM-driven — `OhDataEndpointFactory` PATCH handler resolves body members via `typeof(TModel).GetProperty`) |

Point 6 matters: without the explicit filter, PATCH would be a bypass hole — the delta builder
looks up body members on the CLR type directly, so EDM removal alone would not stop
`{ "internalNotes": "x" }` from binding.

## Wire suppression mechanism (benchmarked)

At `MapAll`, collect an ignored-property map from every profile in the registration:
`IReadOnlyDictionary<Type /* ModelType */, IReadOnlySet<string>>` (new
`IEntitySetEndpointSource.IgnoredPropertyNames` member). If the map is empty, thread
`startupJsonOptions` through unchanged — **zero delta when the feature is unused**. Otherwise
build **one** derived `JsonSerializerOptions`:

```csharp
var derived = new JsonSerializerOptions(startupJsonOptions ?? defaults);
derived.TypeInfoResolver = (derived.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
    .WithAddedModifier(ti =>
    {
        if (ti.Kind != JsonTypeInfoKind.Object) return;
        if (!ignoredByType.TryGetValue(ti.Type, out var names)) return;
        for (int i = ti.Properties.Count - 1; i >= 0; i--)
            if (ti.Properties[i].AttributeProvider is PropertyInfo pi && names.Contains(pi.Name))
                ti.Properties.RemoveAt(i);
    });
```

and use it as the registration's `jsonOptions` everywhere the current value is threaded today.
Matching on the CLR `PropertyInfo.Name` (via `AttributeProvider`) rather than the serialized name
makes the map immune to naming policy.

Properties of this approach:

- **Keyed by CLR type, registration-wide** — an `$expand`-nested child hides *its own* profile's
  ignored properties automatically; navigation GET routes and deep-insert echoes are covered with
  no per-call logic.
- **Deserialization covered for free** — STJ skips members absent from the `JsonTypeInfo` on read,
  so POST/PUT bodies cannot set ignored properties (row 5) through the same single object.
- **The modifier runs once per type** (when STJ first builds and caches the `JsonTypeInfo` on the
  options instance), never per call.

### A/B benchmark (why not `JsonNode` post-processing?)

The stylistic alternative — serialize with unmodified options, then strip keys off the `JsonNode`,
matching the existing `$select`/nav-omission post-processing — was benchmarked head-to-head.
BenchmarkDotNet v0.14.0, .NET 10.0.10, X64 RyuJIT AVX2; 12-property model, 2 ignored;
`SerializeToNode` (the shape OhData actually produces); page of 100 entities:

| Approach (read path, 100 items) | Mean | Ratio | Allocated | Alloc ratio |
|---|---:|---:|---:|---:|
| A — baseline (feature unused) | 118.4 µs | 1.00× | 64,096 B | 1.00× |
| **B — resolver modifier (chosen)** | **96.9 µs** | **0.82×** | **51,696 B** | **0.81×** |
| C — serialize, then strip `JsonNode` | 214.7 µs | 1.83× | 276,953 B | 4.32× |

| Approach (write path, deserialize 100-item body) | Mean | Allocated |
|---|---:|---:|
| A — baseline | 106.8 µs | 48,184 B |
| **B — resolver modifier** | **96.9 µs** | **37,784 B** |

B beats even the do-nothing baseline — steady state simply has fewer members to emit/bind. C pays
full serialization plus a second mutating traversal: 1.8× time, 4.3× allocations. Timing ratios
are directional (noisy dev machine, multimodal distributions flagged); allocation ratios are
deterministic and decisive on their own. Full table on issue #226.

**Do not refactor this toward the `JsonNode`-strip style for consistency with `$select`** — that
consistency would be a measured regression.

## Validation

| Condition | Failure | When |
|---|---|---|
| Ignoring the key property | `ArgumentException` | At the `Ignore(...)` call |
| Same name both ignored and declared as navigation (`HasMany`/`HasOptional`/`HasRequired`, any order) | `InvalidOperationException` naming the entity set and property | Seal time (`VisitModelBuilder`) — order-independent |
| Two profiles in one registration close over the same `TModel` with **different** ignore sets | `InvalidOperationException` naming both entity sets and the model type | `MapAll`, while building the ignored-property map |
| Non-member expression (`x => x.A.B`, computed) | `ArgumentException` | At the `Ignore(...)` call (existing `ExtractNames` behavior) |
| Call after seal | `InvalidOperationException` | Existing `ThrowIfSealed` |

The same-`TModel` conflict rule exists because the derived options are keyed by CLR type: silently
unioning two profiles' sets would hide a property from an entity set that never asked for that,
and taking either side alone would leak. Identical sets (including both-empty) are fine. Separate
registrations (`AddOhData("v1", ...)` / `AddOhData("v2", ...)`) each build their own options, so a
v2 registration can legitimately expose a property that v1 ignores.

## Interaction notes

- **`AdvancedConfigure`** ejects the EDM half (row 1–2) like all automatic EDM config — call
  `configuration.EntityType.Ignore(...)` yourself. Rows 3–6 and all validation still apply, since
  they are runtime concerns, not EDM config.
- **ETags:** `UseETag` selectors are profile-side code, not wire surface — an ignored property MAY
  participate in the ETag hash (useful for row-version columns that should never be exposed).
- **Navigation-only types** (a `TNavigation` with no profile of its own) have no `Ignore` surface;
  their properties serialize as-is. Give the type a profile if its wire shape needs trimming.
- **`$select` post-processing and nav omission** operate on JSON that already lacks the ignored
  members; no changes needed there.
- **OpenAPI/NSwag companions** generate schemas from CLR types and will still show ignored
  properties until taught otherwise — follow-up issue to file alongside #219/#220 (same
  "reflect profile config into API docs" family). Runtime behavior is unaffected.

## Testing

Matrix over a profile ignoring one primitive + one complex property (and a control profile with no
ignores, same registration):

1. `$metadata` omits ignored properties; control entity's properties intact.
2. Collection GET, single GET, nav GET, nested `$expand`, POST echo, PUT/PATCH
   `return=representation` — response bodies omit ignored members.
3. `$select`/`$filter`/`$orderby` naming an ignored property → `400`.
4. `GET/PUT/PATCH/DELETE /Set({key})/{IgnoredProp}` and `/$value` → `404`.
5. POST and PUT bodies containing ignored members → members not bound (handler receives default).
6. PATCH body containing an ignored member → not in `GetChangedPropertyNames`.
7. Validation: key-ignore throws; nav+ignore throws (both declaration orders); same-`TModel`
   conflicting sets throws at `MapOhData()`; identical sets do not.
8. Zero-delta guard: registration with no ignores threads the original options instance
   (reference-equal).
9. Unit tests on the options-derivation helper (modifier removes exactly the mapped members,
   respects naming policy, non-object kinds untouched).
