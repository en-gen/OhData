# Changelog

All notable changes to this project will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

_Nothing yet._

---

## [1.0.0] - 2026-07-15

First public release. Includes the initial framework feature set (drafted as an unpublished
0.1.0 that never shipped) plus the full conformance, hardening, and performance train.

### Added

- Deep insert — nested related entities in `POST /{EntitySet}` (OData §11.4.2.2): a new
  `AllowDeepInsert` profile flag (`bool?`, inherits `EntitySetDefaults.AllowDeepInsert`, default
  `false`, entity-level granularity — no per-navigation opt-in). Rides the existing `Post`
  handler; no new route or handler delegate. **Default (`false`):** nested navigation-property
  values (declared via `HasMany`/`HasOptional`/`HasRequired`, any overload) that System.Text.Json
  already bound during deserialization are stripped (set to `null`) before `Post` is invoked —
  both collection and single-valued navigations; nested values for non-navigation (plain)
  collection properties are left untouched. **Opt-in (`true`):** the full deserialized graph is
  passed to `Post` as-is; the handler owns atomic persistence of the whole graph (e.g. one EF Core
  `SaveChanges`) — the framework does not open a transaction on the handler's behalf. The `201`
  response echoes the handler's return value verbatim, so nested children serialize inline when
  the handler populates them, satisfying §11.4.2.2's "return the created entity with related
  entities." `prop@odata.bind` (JSON format §8.5 — link an existing entity instead of creating
  one) is documented non-support: detected anywhere in the POST body (top level or nested) and
  rejected with `501 Not Implemented` rather than silently ignored. New
  `IEntitySetEndpointSource.AllowDeepInsert`/`NavigationPropertyNames` members. See
  `docs/deep-insert.md`.
- POST to a collection navigation property — create a related entity (OData §11.4.2.1):
  `HasMany` gains an optional `post` parameter,
  `Func<TKey, TNavigation, CancellationToken, Task<TNavigation?>>`, that registers
  `POST /{EntitySet}({key})/{Property}`. The request body is deserialized as the navigation's
  item type and passed to `post` along with the parent key; the handler persists the child and
  returns it (or `null` if the parent was not found, mapped to `404`). On success: `201 Created`
  with the created child in the body, plus `@odata.id`/`Location` when `refTargetEntitySet` is
  configured (reuses the same child-key detection `$ref` uses); `Prefer: return=minimal` → `204`
  with `Preference-Applied`/`OData-EntityId` (mirrors the entity-level POST behavior). Malformed
  or empty JSON body → `400`; non-JSON `Content-Type` → `415`. No `post` handler configured means
  the route is not registered at all (handler-presence-drives-routes) — `POST` to that path
  returns `405` since the `GET` nav route occupies the same template. New
  `NavigationRouteDefinition.PostChild` (type-erased handler, following the existing
  `Handler`/`BatchHandler` pattern). No EDM/`$metadata` change — the navigation property is
  already declared via `HasMany`. See `docs/navigation-routing.md`.
- Individual structural property write routes (OData §11.4.9.1/.2/.3, spec items #30/#31):
  `PUT`/`PATCH /{EntitySet}({key})/{Property}` with body `{"value": <newValue>}` (`204` on
  success) and `DELETE /{EntitySet}({key})/{Property}` (sets the property to `null`, `204` on
  success). Rides the existing `Patch` handler — a single-property write is built as a
  one-property `Delta<TModel>` and handed to `Patch`, which already owns fetch-existing → apply →
  persist; no new handler delegate. Routes are registered only when `PropertyAccessEnabled`
  resolves `true` AND `Patch` is configured (unlike property *read*, `GetById` is not required —
  `Patch` does its own fetching). Validation: writing to the key property returns `400` (with
  explicit stub routes so clients get a clean OData error instead of an unmatched-route response);
  unknown property → `404`; malformed body (missing `value`, non-JSON-object, invalid JSON, wrong
  type for the property) → `400`; entity not found (`Patch` returns `null`) → `404`; wrong
  `Content-Type` → `415`; `DELETE` on a non-nullable property → `400`. Complex properties: `PUT`
  performs a full replacement; `PATCH` on a complex property is documented non-support and returns
  `400` (`code: "NotSupported"`) rather than attempting a merge. `If-Match`/ETag honored on all
  three verbs via the existing `CheckETagAsync` helper (`412` on mismatch). Property-write routes
  inherit the entity set's authorization, same as every other route. Raw-value write
  (`PUT .../{Property}/$value`) remains out of scope — see `docs/property-access.md`.
- Individual structural property read routes (OData §11.2.6, JSON format Part 2 §4.6-4.7):
  `GET /{EntitySet}({key})/{Property}` (property-value envelope; `204` when the value is `null`)
  and `GET /{EntitySet}({key})/{Property}/$value` (raw `text/plain`/`application/octet-stream`
  value; `404` when the value is `null`; `400` for complex-typed properties, which have no raw
  representation). Rides the existing `GetById` handler — no new handler delegate. Controlled by
  a new `PropertyAccessEnabled` flag (profile-level, `bool?`, inherits
  `EntitySetDefaults.PropertyAccessEnabled`, **default `true`**) and requires `GetById` to be
  configured; routes are omitted otherwise. Structural properties are computed once at startup as
  every public readable CLR property of the model minus every property declared as a navigation
  via `HasMany`/`HasOptional`/`HasRequired`, so property and navigation routes never collide by
  construction. Adds a startup route-collision validation pass: `app.MapOhData()` throws
  `InvalidOperationException` if an entity-level bound function shares a name with a structural
  property. ETags: when `UseETag` is configured, the property-read route sets the entity's `ETag`
  header and honors `If-None-Match` (`304`); `/$value` omits the header. Property routes inherit
  the entity set's authorization configuration. Property **write** (`PUT`/`PATCH`/`DELETE` on an
  individual property) is covered by the entry above.
- `OData-MaxVersion` request-header validation (§8.2.7): a service must honor `OData-MaxVersion`
  or reject the request. OhData now parses the header (`major.minor`, whitespace-tolerant) on
  every route in the OData group - service document, `$metadata`, and all entity-set/bound-operation
  routes. No header, or `4.0` and higher (`4.01`, `5.0`, ...), proceeds unconstrained; a value
  below `4.0` or an unparseable value returns `400 Bad Request` with the standard OData error
  envelope (`code: "UnsupportedODataVersion"`). The header is still never echoed in responses.
  New `ODataMaxVersionFilter` endpoint filter, registered alongside the existing `OData-Version`/
  `$format`/`Accept` group filter in `OhDataEndpointFactory.MapAll`
- Batch-aware `$expand` navigation handlers (REVIEW.md M-1): `HasMany`, `HasOptional`, and
  `HasRequired` now accept an additive `batchGetAll`/`batchGet` overload
  (`Func<IReadOnlyList<TKey>, CancellationToken, Task<ILookup<TKey, TNavigation>>>` for
  `HasMany`; `Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TNavigation?>>>`
  for `HasOptional`/`HasRequired`) alongside the existing per-entity `getAll`/`get` delegates.
  When registered, `$expand` collects the page's parent keys and invokes the batch delegate
  **once per expanded property per page** instead of once per parent entity, collapsing the
  previous N×P sequential awaited handler calls to P. A per-entity `Handler` is auto-derived
  from the batch delegate, so `GET /{EntitySet}({key})/{Nav}`, nav `$count`, and `$ref` keep
  working without registering a second handler. Fully additive and opt-in - profiles that keep
  using the per-entity overloads are unaffected (byte-identical fallback behavior)

### Fixed

- `round()` now follows OData's round-half-away-from-zero semantics (Part 2 §5.1.1.9) by default
  instead of .NET's banker's rounding (round-half-to-even), e.g. `round(2.5)` now returns `3`, not
  `2`, and `round(-2.5)` returns `-3`, not `-2`. Root cause: Microsoft.OData's `ApplyTo` binder
  emits the single-argument `Math.Round(double)`/`Math.Round(decimal)` overload, which defaults to
  banker's rounding. On the `GetQueryable` path (and its `$count` companion) OhData now rewrites
  those call nodes in the post-`ApplyTo` expression tree into the two-argument
  `Math.Round(value, MidpointRounding.AwayFromZero)` overload before the query is enumerated. New
  `RoundingMode` setting (profile-level `RoundingMode?` enum — `SpecCompliant` default /
  `BankersRounding` — inheriting `EntitySetDefaults.RoundingMode`), following the same
  `PropertyAccessEnabled`/`AllowDeepInsert` wiring pattern. `BankersRounding` exists as an
  escape hatch: the two-argument `Math.Round` overload the fix requires is not translatable by
  every EF Core provider, so a profile that hits a translation failure can opt back into the
  pre-fix (single-argument) behavior. Does **not** reach the Priority-1
  `ODataEntitySetProfile.GetODataQueryable` path, where the profile calls `ApplyTo` itself — see
  `docs/query-options.md#round-midpoint-rounding` and `docs/spec-compliance.md`
- `Prefer: maxpagesize` (§8.2.8.3) is now capped at the entity set's `MaxTop`: the honored page
  size is `min(maxpagesize, MaxTop)` rather than `maxpagesize` overriding `MaxTop` outright with no
  ceiling. `Preference-Applied` reflects the page size actually honored (the clamped value) per
  §8.2.8.7, not the value the client requested. Removes the "Known Limitation" documented in
  `docs/query-options.md` (M-4)
- `$orderby` on collection navigation routes (`GET /{Set}({key})/{Nav}`) is now applied in-memory
  (ascending/descending, multiple sort keys) instead of being silently accepted and ignored. An
  unknown property name returns `400 Bad Request` (`InvalidQueryOption`), matching the existing
  `$select` validation on the same path. Applied before `$skip`/`$top`, per standard OData
  system-query-option ordering. `docs/navigation-routing.md` updated (M-3)
- Startup route-collision validation now also covers `POST /{EntitySet}({key})/{segment}`: a
  navigation property with a `post` (create-related-entity) handler sharing a name with an
  entity-level bound action would previously register two handlers for the same route template,
  surfacing only as an ambiguous-match failure at request time. `app.MapOhData()` now throws
  `InvalidOperationException` at startup instead, matching the existing structural-property/
  bound-function collision guard
- `OData-EntityId` response header (§8.3.4) is now emitted on any `204 No Content` response
  that creates or upserts an entity (POST/upsert-PUT with `Prefer: return=minimal`); a plain
  update-PUT 204 does not carry it
- `GET /{EntitySet}({key})/{Nav}/$ref` on a single-valued navigation now returns a populated
  `@odata.id` when `refTargetEntitySet` is configured, matching the existing collection-valued
  behavior (§11.4.6.1)
- POST and PUT with a malformed, wrong-shaped, or non-JSON-object request body (invalid JSON,
  empty body, JSON array, wrong-typed field, ~100-level-deep JSON) now return `400 Bad Request`
  with the documented OData error envelope (`{"error":{"code":...,"message":...}}`, §9.4)
  instead of an empty body. Root cause: POST/PUT bound the request body via a `TModel model`
  minimal-API parameter, so ASP.NET Core's implicit JSON body binder rejected malformed input
  before OhData's error-formatting code ran. POST/PUT now read and deserialize the body
  manually, mirroring PATCH's existing approach
- POST, PUT, and PATCH with an unsupported `Content-Type` (e.g. `text/plain`, `application/xml`,
  or a missing header) now return `415 Unsupported Media Type` with the OData error envelope
  instead of an empty body. PATCH's route previously carried an `.Accepts<TModel>("application/json")`
  metadata declaration that made ASP.NET Core reject the request before the handler's own JSON
  parsing (and error formatting) ran; content-type validation is now performed manually in all
  three handlers
- PATCH with a JSON array (or any non-object JSON value: string, number, bool, null) as the
  request body no longer throws an unhandled `System.InvalidOperationException` from
  `JsonElement.EnumerateObject()`. Non-object bodies now return `400 Bad Request` with an OData
  error envelope
- `GET /{EntitySet}({key})/{Nav}` on a single-valued navigation (`HasOptional`/`HasRequired`)
  now carries `@odata.context` (JSON §4.5), matching the collection-valued branch, which already
  did
- `GET /{EntitySet}({key})/{Nav}/$count` on a missing parent now returns the OData error envelope
  (`404`, §9.4) instead of an empty-body `404` — this was the sole remaining bare `Results.NotFound()`
  in the endpoint factory
- `$ref` response context URLs now use `#$ref` (single-valued) / `#Collection($ref)`
  (collection-valued) per JSON Format §14 / Protocol §10.12, instead of a path-shaped context
- `$select` now narrows the `@odata.context` URL to the projected form (`#Set(prop1,prop2)` for
  collections, `#Set(prop1,prop2)/$entity` for a single entity, §10.7/§10.8), with properties
  listed in the order the client requested them. Wired on all three collection-`GET` paths,
  `GetById` (which also gained actual `$select` body filtering — previously the metadata declared
  `SelectEnabled` but nothing enforced it), and navigation-collection routes
- `If-Match` (including the `*` wildcard) against a resource that does not exist now returns
  `412 Precondition Failed` instead of `404` (RFC 7232 §3.1 / §11.4.1.1) — the existence check
  now happens before the wildcard short-circuit
- `If-None-Match: *` on `PUT` is now honored as a create-guard (§11.4.4) when `AllowUpsert` is
  enabled: `412 Precondition Failed` if the entity already exists, otherwise proceeds as an
  insert. A no-op when the header is absent
- `$top`/`$skip` with an invalid (non-numeric or negative) value on a navigation-collection route
  (`GET /{Set}({key})/{Nav}`) now returns `400 Bad Request` (`InvalidQueryOption`) instead of being
  silently ignored and returning the full, un-paged collection (Part 2 §5.1.6)
- Bound function/action results that are a recognized Edm-primitive type (string, numeric types,
  `bool`, `Guid`, date/time types, `byte[]`) now get the JSON §11 individual-value envelope
  (`{"@odata.context":".../$metadata#Edm.<Type>","value":<primitive>}`) instead of a bare scalar
  body. Model and collection-of-model results already carried context and are unchanged

### Docs

- `docs/spec-compliance.md`'s `Prefer: maxpagesize` row and Known Limitations table corrected to
  match the `Math.Min(maxpagesize, MaxTop)` clamp (they still described the pre-#133 unclamped
  behavior); a new "Declared deviations" section documents the Priority-1
  `GetODataQueryable`/`ODataQueryResult` paging-metadata contract and the parent-path
  `@odata.context` shape on navigation-collection routes as permanent design choices
- New "Unbound functions and actions" section in `docs/bound-operations.md` covering
  `OhDataBuilder.AddFunction`/`AddAction`, previously undocumented despite being claimed ✅ in
  `docs/spec-compliance.md`
- New "Registering profiles" section in `docs/architecture.md` covering
  `AddProfilesFrom`/`AddProfilesFromAssemblyOf`/`AddProfilesFromAssembly` assembly-scanning
  registration
- New `docs/deployment.md` documenting the repo's `Dockerfile` and `render.yaml`, linked from the
  README documentation index
- `CLAUDE.md` and `docs/architecture.md`'s startup-validation description now also mentions the
  POST-nav/bound-action collision guard added in #133

### Added — initial framework (drafted as an unpublished 0.1.0; first shipped in 1.0.0)

**Server (OhData.AspNetCore)**

- Convention-based OData 4.0 endpoint registration via `EntitySetProfile<TKey, TModel>`  - 
  no controllers required
- `GetAll`, `GetQueryable` (IQueryable with EF Core pushdown), `GetById`, `Post`, `PutById`,
  `Patch`, and `Delete` handler slots; unregistered slots produce no route
- `GetQueryable` path: framework applies `$filter`, `$orderby`, `$skip`, `$top` via
  `ApplyTo(IQueryable)` for SQL pushdown; `$select` applied via JsonNode post-processing
  for consistent camelCase
- `$count` support: inline (`?$count=true`) and standalone `/$count` endpoint
- `$expand` support for registered navigation properties
- `$search` support via opt-in `Search` handler
- `$format` query option support
- ETag support (`GetETag`, `If-Match`, `If-None-Match`, 412 on mismatch, 304 on match)
- Named registrations: `AddOhData("v1", ...)` / `MapOhData("v1")` for multiple coexisting
  API surfaces
- Bound functions (`GET /{EntitySet}/{Name}?param=value`) and bound actions
  (`POST /{EntitySet}/{Name}` with JSON body)
- Entity-bound functions and actions (`GET /{EntitySet}({key})/{Name}`)
- Navigation routing: `HasMany`, `HasOptional`, `HasRequired` with optional handler delegates
- `$ref` link management: `AddRef`, `RemoveRef`, `SetRef`
- Authorization per entity set: `RequireAuthorization()`, `RequireRoles(...)`
- `Prefer: return=minimal` support on POST/PUT/PATCH (returns 204)
- `Prefer: maxpagesize=N` support with server-side `nextLink` pagination
- `ODataEntitySetProfile<TKey, TModel>` extension: profile receives `ODataQueryOptions<T>`
  directly for full manual control
- Service document (`GET /`) and CSDL metadata (`GET /$metadata`) endpoints
- OpenAPI/Swagger integration: entity sets grouped by name, service doc and metadata
  excluded from API explorer
- `AdvancedConfigure` eject hatch for full EDM control
- OData error body (`application/json`) on 400/404/405/406/412/501 responses

**Client (OhData.Client)**

- Typed OData 4.0 client with `OhDataClient` and `IHttpClientFactory` support
- Fluent LINQ-to-OData filter translation (`$filter`, `$select`, `$expand`,
  `$orderby`/`$thenby`, `$top`, `$skip`, `$count`)
- Terminal operations: `ToListAsync`, `ToPageAsync` (returns `ODataPage<T>`),
  `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync`
- Single-entity operations: `GetAsync`, `InsertAsync`, `PutAsync`, `PatchAsync`,
  `DeleteAsync`
- `ODataPage<T>` with `NextPageAsync()` for cursor-based pagination via `$skiptoken`
- `ODataClientException` with parsed OData error body
- Entity set name resolution via `[ODataEntitySet]` attribute or `EntitySetNameConvention`
  (handles irregular plurals)
- Configurable `JsonSerializerOptions` via `OhDataClientOptions`

**Versioning helpers (included in OhData.AspNetCore, namespace `OhData.AspNetCore.Versioning`)**

- `AddOhDataVersion(name, prefix, configure)` convenience wrapper for named multi-version registrations
- `MapOhDataVersion(name)` convenience wrapper matching `AddOhDataVersion`

**Infrastructure**

- CI pipeline: build, format check, server tests, client tests, code coverage (Codecov)
- k6 smoke tests in Docker Compose: collection, single-entity, mutations, navigation,
  versioning; p95 latency threshold and 99% check pass rate
- GitVersion (GitFlow) for semantic versioning
- Husky pre-commit hook for `dotnet format`
- BenchmarkDotNet project for client library performance
- Render deployment config for hosted test bench

### Changed — initial framework

- `Delete` handler returns `Task<bool>` - `false` produces a 404 OData error response
- `$select` uses JsonNode post-processing (not `ISelectExpandWrapper`) to preserve
  camelCase consistency
- OData spec compliance improvements across batches 1-6:
  - Correct `406 Not Acceptable` for unsupported `$format` values
  - `@odata.etag` annotation in collection results
  - `If-Match` list parsing (multiple ETags)
  - `OData-Version` and `Content-Type: application/json;odata.metadata=minimal` headers
  - `Location` and `OData-EntityId` headers on POST 201
  - `Content-Location` header on GET single entity
  - `@odata.count` on `GetAll` responses when `$count=true`
  - `Prefer: return=representation/minimal` on PUT/PATCH
  - Removed `OData-MaxVersion` from response headers (not required by spec)

---

[Unreleased]: https://github.com/en-gen/OhData/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/en-gen/OhData/releases/tag/v1.0.0
