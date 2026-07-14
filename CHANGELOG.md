# Changelog

All notable changes to this project will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added

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

---

## [0.1.0] - 2026-06-11

### Added

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

### Changed

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

[Unreleased]: https://github.com/en-gen/OhData/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/en-gen/OhData/releases/tag/v0.1.0
