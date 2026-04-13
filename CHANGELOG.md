# Changelog

All notable changes to this project will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

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

**Versioning (OhData.AspNetCore.Versioning)**

- `AddOhDataVersion(name, prefix, configure)` convenience wrapper
- `MapOhDataVersion(name)` convenience wrapper

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

[Unreleased]: https://github.com/en-gen/OhData/compare/HEAD...HEAD
