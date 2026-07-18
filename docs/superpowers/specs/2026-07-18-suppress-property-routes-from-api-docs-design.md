# Design: Suppress property routes from API docs by default

**Date:** 2026-07-18
**Status:** Approved design, pending implementation

## Problem

OhData registers four routes per structural property, per entity set, all gated by
`PropertyAccessEnabled` and all riding existing handlers:

- `GET  /{EntitySet}({key})/{Property}` — property-value envelope (§11.2.6)
- `GET  /{EntitySet}({key})/{Property}/$value` — raw value (Part 2 §4.7)
- `PUT` / `PATCH` / `DELETE /{EntitySet}({key})/{Property}` — property writes (§11.4.9)

For an entity with N structural properties this is ~4·N operations. Across several entity
sets the generated API documentation (Swagger UI / Scalar / any OpenAPI consumer) becomes
dominated by low-value property-access operations, drowning the primary CRUD, navigation, and
bound-operation surface. These routes are spec-conformant and must keep working at runtime,
but they add little to the human-facing API docs.

## Goal

Omit all property routes from the generated API documentation **by default**, while leaving
them fully live at runtime. Provide an **opt-in** configuration flag to include them in the
docs when desired, following the framework's established server-default + per-profile-override
pattern.

## Non-goals

- No change to runtime routing, dispatch, or behavior of property routes. They respond
  identically whether documented or not.
- No per-property or per-verb doc granularity. The toggle covers all property routes
  (read + write) for an entity set as a single unit.
- No change to `PropertyAccessEnabled` semantics. That flag continues to gate whether the
  routes *exist*; the new flag only gates whether existing routes appear in docs.

## Decisions (confirmed with owner)

1. **Behavior:** Omit by default, with an opt-in config flag. (Not "always omit, no config.")
2. **Scope:** Cover all property routes — the two GETs, the PUT/PATCH/DELETE writes, and the
   key-immutable 400-stub writes — as one concept. (Not just the two GET routes.)
3. **Naming:** `PropertyRouteDocsEnabled`, matching the `…Enabled` capability-toggle
   convention already used by `SelectEnabled`/`ExpandEnabled`/`FilterEnabled`/
   `OrderByEnabled`/`CountEnabled`/`PropertyAccessEnabled`. "Docs" scopes it unambiguously to
   documentation so it is not confused with the runtime `PropertyAccessEnabled` sibling.

## Mechanism

Apply `.ExcludeFromDescription()` to each property route builder when
`PropertyRouteDocsEnabled` resolves `false`. `ExcludeFromDescription()` removes the endpoint
from ASP.NET Core's `ApiExplorer`, which is the single shared upstream for all three doc
stacks the project supports (Microsoft.AspNetCore.OpenApi, Swashbuckle, NSwag). One change
therefore covers every doc generator. This is the same technique already used for the
`$metadata` and service-document routes in `OhDataEndpointFactory` (around lines 552–556).

Runtime dispatch, filters, auth, and response contracts are untouched — `ExcludeFromDescription`
only affects API-metadata enumeration.

## Configuration surface

The new flag mirrors `PropertyAccessEnabled` across the exact same touch points.

### `EntitySetDefaults` (server-wide default)

```csharp
/// <summary>
/// Whether individual structural property routes
/// (GET /{EntitySet}({key})/{Property}, .../{Property}/$value, and the PUT/PATCH/DELETE
/// property writes) appear in generated API documentation (Swagger/OpenAPI). Defaults to
/// <c>false</c>: these routes are numerous (four per property, per entity set) and would
/// otherwise dominate the docs. They remain fully functional at runtime regardless of this
/// flag — it only controls documentation visibility. Set to <c>true</c> to include them.
/// Profile-level <c>PropertyRouteDocsEnabled</c> overrides this value.
/// </summary>
public bool PropertyRouteDocsEnabled { get; set; } = false;
```

### `EntitySetProfile<TKey, TModel>` (per-profile override)

```csharp
/// <summary>
/// Controls whether this entity set's structural property routes appear in generated API
/// documentation. Inherits from <see cref="EntitySetDefaults"/> (default <c>false</c>) when
/// <c>null</c>. Documentation-only — property routes remain live at runtime whenever
/// <see cref="PropertyAccessEnabled"/> resolves <c>true</c> and the required handler is
/// configured, regardless of this flag.
/// </summary>
protected bool? PropertyRouteDocsEnabled { get; init; }
```

- Backing field: `private bool _resolvedPropertyRouteDocsEnabled;`
- Resolution (alongside the sibling resolutions):
  `_resolvedPropertyRouteDocsEnabled = PropertyRouteDocsEnabled ?? defaults.PropertyRouteDocsEnabled;`
- Interface implementation:
  `bool IEntitySetEndpointSource.PropertyRouteDocsEnabled => _resolvedPropertyRouteDocsEnabled;`

### `IEntitySetEndpointSource` (internal)

```csharp
bool PropertyRouteDocsEnabled { get; }
```

## Applying it in `OhDataEndpointFactory`

Both property-route blocks — the read block (`if (source.PropertyAccessEnabled && source.HasGetById)`,
~line 3302) and the write block (`if (source.PropertyAccessEnabled && source.HasPatch)`,
~line 3439) — register routes through `entityAuthGroup.Map*(...)` chains.

Introduce a single local helper visible to both blocks (declared once before the read block,
in the same method scope):

```csharp
// Property routes are numerous; by default they are omitted from API docs
// (ExcludeFromDescription) but remain fully live at runtime. Opt in via PropertyRouteDocsEnabled.
RouteHandlerBuilder DocProp(RouteHandlerBuilder b) =>
    source.PropertyRouteDocsEnabled ? b : b.ExcludeFromDescription();
```

Wrap every property route registration with `DocProp(...)`:

- Read block: the `GET {Property}` builder and the `GET {Property}/$value` builder.
- Write block: the PUT builder, the PATCH (`MapMethods`) builder, the DELETE builder, and the
  three key-immutable 400-stub builders (PUT/PATCH/DELETE on the key property).

Because these chains currently end in `.WithTags(...).Produces(...)` statements without
assignment, wrap the whole expression, e.g.:

```csharp
DocProp(entityAuthGroup.MapGet($"/{name}({{key}})/{propCapture.Name}", handler)
    .WithTags(name)
    .Produces(200, /* ... */)
    .Produces(204)
    .Produces(404));
```

`ExcludeFromDescription()` returns the builder, so `DocProp` composes cleanly and is a no-op
(identity) when docs are enabled.

## Testing

Extend `OhData.AspNetCore.Tests/ApiDescriptionProviderTests.cs` (or a focused new test class):

1. **Default omits:** With no configuration, assert the `ApiExplorer` `ApiDescription`
   collection contains **no** property routes (match relative paths ending in `/{Property}`
   and `/{Property}/$value` for a profile that has `GetById` + `Patch` and property access
   enabled). Assert the primary CRUD / nav / bound-operation routes are still present.
2. **Opt-in includes:** With `PropertyRouteDocsEnabled = true` (set via `WithDefaults` and,
   in a second case, via a per-profile override), assert the property routes **do** appear in
   the `ApiDescription` collection, including both GETs and the writes.
3. **Runtime unaffected:** Assert (or rely on the existing `PropertyAccessTests` /
   `PropertyWriteTests` staying green) that property routes still respond correctly at
   runtime with the default (docs-suppressed) configuration — documentation visibility must
   not change dispatch.
4. **Override precedence:** Profile-level `PropertyRouteDocsEnabled` overrides the
   server-wide default in both directions (`true` over default `false`, and `false` over a
   `WithDefaults(d => d.PropertyRouteDocsEnabled = true)`).

The three doc-stack integration test projects (`OhData.AspNetCore.OpenApi.Tests`,
`OhData.AspNetCore.Swashbuckle.Tests`, `OhData.AspNetCore.NSwag.Tests`) all build on
`ApiExplorer`; a single ApiExplorer-level assertion is sufficient and avoids triplicating the
same check. Add at most one lightweight per-stack assertion only if an existing test's
expected operation count would shift.

## Documentation updates

- `docs/property-access.md`: document `PropertyRouteDocsEnabled`, its default (`false` = omit
  from docs), the runtime-vs-docs distinction, and both configuration scopes.
- `docs/openapi.md`: note that property routes are omitted from generated docs by default and
  how to include them.
- `CHANGELOG.md`: additive feature entry.

## Interaction notes / edge cases

- When `PropertyAccessEnabled` resolves `false`, no property routes are registered at all, so
  `PropertyRouteDocsEnabled` has nothing to act on — it is simply inert. No coupling beyond
  that.
- The flag has no effect on navigation routes, `$ref` routes, `$count`, bound/unbound
  operations, or entity CRUD — only on the structural-property routes enumerated above.
- Default `false` is a documentation-only default change; it does not alter the framework's
  spec conformance (the routes remain served), so it is not a breaking runtime change.

## Workflow

Per repository conventions: file a GitHub issue for this change and land it as a
`Fixes #NNN` PR branched off `develop`, with BenchmarkDotNet/k6 metrics in the PR body per the
project's PR checklist (this change is doc-metadata only, so runtime perf should be flat —
note that explicitly). Additive, non-breaking.
