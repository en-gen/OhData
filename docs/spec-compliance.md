# OData 4.0 Spec Compliance

OhData targets the [OData 4.0 specification](https://docs.oasis-open.org/odata/odata/v4.0/odata-v4.0-part1-protocol.html). This page documents which sections are implemented and any known limitations.

## Protocol headers

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `OData-Version: 4.0` response header | §8.2.6 | ✅ | Added to all responses |
| `OData-MaxVersion` request header | §8.2.7 | ⚠️ | Accepted but never read, parsed, or validated by the framework - a purely passive no-op, not "supported" processing. Also not echoed in responses. |
| `Content-Type: application/json` | §8.2.1 | ✅ | All responses except `GET /$metadata`, which returns `application/xml` |
| `$format` query option | §11.2.12 | ✅ | `json` and `application/json` accepted; others → 400 |
| `Accept` header validation | §8.2.1 | ✅ | Non-JSON accept headers → 406 |

## Request conditional headers

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `If-Match` on PUT/PATCH/DELETE | §8.2.5 | ✅ | 412 on mismatch; `*` wildcard supported |
| `If-Match` with multiple ETags | §8.2.5 | ✅ | Comma-separated list per RFC 7232 §3.1 |
| `If-None-Match` on GET → 304 | §8.2.5 | ✅ | Returns 304 Not Modified when ETag matches |
| Weak ETag prefix (`W/`) | §2.3 | ✅ | Stripped before comparison |

## Response annotations

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `@odata.context` on data responses | §10 | ✅ | Collections, entities, and the service document. Not set on error responses; `GET /$metadata` is XML so the annotation doesn't apply there. |
| `@odata.count` inline | §11.2.6.5 | ✅ | When `$count=true` |
| `@odata.id` entity self-link | §4.5.8 | ✅ | On GET, POST, PUT, PATCH responses |
| `@odata.etag` in body | §4.5.3 | ✅ | When ETags configured |
| `@odata.nextLink` | §11.2.6.7 | ✅ | When page size equals `MaxTop` |
| `@odata.context` on bound operations | §10 | ✅ | Included when the function/action return type is the profile's model type or `IEnumerable<TModel>`; omitted for primitive/arbitrary return types |
| `OData-EntityId` response header | §8.3.4 | ✅ | On any `204 No Content` that creates/upserts an entity (POST/upsert-PUT with `Prefer: return=minimal`); omitted on a plain update-PUT 204 |

## Collection queries

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `$filter` | §11.2.6.1 | ✅ | Comparison, logical, arithmetic, string functions |
| `$orderby` | §11.2.6.2 | ✅ | Multiple keys, asc/desc |
| `$top` | §11.2.6.3 | ✅ | `MaxTop` server-side cap enforced |
| `$skip` | §11.2.6.4 | ✅ | |
| `$count` (inline and standalone) | §11.2.6.5 | ✅ | Reports the pre-paging total; on the `ODataEntitySetProfile` (`GetODataQueryable`) path a profile that applies its own `$top`/`$skip` must set `ODataQueryResult.TotalCount` or `@odata.count` falls back to the post-page item count |
| `$search` | §11.2.6.6 | ✅ | Requires a `Search` handler; `400 Bad Request` (`UnsupportedQueryOption`) if unset |
| `$select` | §11.2.4.1 | ✅ | JSON post-processing; SQL column projection not performed |
| `$expand` | §11.2.4.2 | ✅ | Generic navigation-delegate expansion (registered via `HasMany`/`HasOptional`/`HasRequired`); the same pipeline runs identically on the `GetQueryable`, `GetAll`, and Priority-1 `ODataQueryOptions` paths. No EF Core dependency - each expanded property is resolved by calling the registered navigation handler per entity. |
| `$skiptoken` (server-driven paging) | §11.2.6.7 | ✅ | Base64-encoded raw skip offset (a 4-byte little-endian int) - not an opaque/obfuscated cursor. Predictable and forgeable by clients. |

## Entity operations

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Get entity by key | §11.2.2 | ✅ | |
| Create entity (POST) | §11.4.1 | ✅ | Returns `201 Created` + `Location` header (also sets `Content-Location`, per §8.3.3) |
| Update entity (PUT) | §11.4.3 | ✅ | Full replacement |
| Update entity (PATCH) | §11.4.3 | ✅ | Partial update |
| Delta PATCH | §11.4.3 | ✅ | Via the base `EntitySetProfile.Patch` delegate - the framework builds a `Delta<TModel>` containing only the properties present in the request body and passes it to the handler, which typically calls `delta.Patch(existing)` |
| Delete entity | §11.4.5 | ✅ | `Delete` returns `Task<bool>`; `false` → `404` or `204` depending on `IdempotentDelete` (defaults to `true`, i.e. `204`) |
| Upsert via PUT | §11.4.4 | ✅ | `AllowUpsert = true` |
| Key validation on PUT/PATCH | §11.4.3 | ✅ | URL key must match body key; 400 on mismatch |

## Navigation and links

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Navigation property routes | §11.2.3 | ✅ | `GET /Set({key})/Nav` |
| Navigation `$count` | §11.2.3 | ✅ | `GET /Set({key})/Nav/$count` |
| Navigation with `$select` | §11.2.3 | ✅ | |
| `$ref` get link(s) | §11.4.6.1 | ✅ | `GET /Set({key})/Nav/$ref` returns populated `@odata.id` (collection or single) when `refTargetEntitySet` is configured on the navigation; otherwise an empty envelope |
| `$ref` add link | §11.4.6.1 | ✅ | `POST /Set({key})/Nav/$ref` |
| `$ref` remove link | §11.4.6.2 | ✅ | `DELETE /Set({key})/Nav/$ref` |

## Individual property access

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Get individual property | §11.2.6 | ✅ | `GET /Set({key})/Prop` — rides the existing `GetById` handler; `PropertyAccessEnabled` (default `true`) gates it, requires `GetById` to be configured. Returns the `{"@odata.context":...,"value":...}` envelope |
| Null property value | §11.2.6 | ✅ | `204 No Content` |
| Get individual property raw value | Part 2 §4.6/4.7 | ✅ | `GET /Set({key})/Prop/$value` — `text/plain` for primitives (invariant culture), `application/octet-stream` for `byte[]` |
| Raw value of a null property | Part 2 §4.7 | ✅ | `404 Not Found` — the raw value does not exist |
| Raw value of a complex property | Part 2 §4.7 | ✅ | `400 Bad Request` — no raw representation; use the non-`$value` envelope form instead |
| Property-route/bound-function collision detection | — | ✅ | Startup validation (`app.MapOhData()`) throws `InvalidOperationException` if an entity-level bound function shares a name with a structural property |
| Update individual property (`PUT`/`PATCH`) | §11.4.9.1/.2 | ❌ | Not yet implemented — planned follow-up PR; rides the existing `Patch` handler in the design |
| Set property to null (`DELETE`) | §11.4.9.3 | ❌ | Not yet implemented — planned follow-up PR |

## Bound operations

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Collection-bound functions | §11.5.3 | ✅ | `GET /Set/FunctionName?params` |
| Collection-bound actions | §11.5.4 | ✅ | `POST /Set/ActionName` (JSON body) |
| Entity-bound functions | §11.5.4 | ✅ | `GET /Set({key})/FunctionName?params` |
| Entity-bound actions | §11.5.4 | ✅ | `POST /Set({key})/ActionName` (JSON body) |
| Unbound functions | §11.5.3 | ✅ | `GET /FunctionName?params` |
| Unbound actions | §11.5.4 | ✅ | `POST /ActionName` |

## `Prefer` header

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `Prefer: return=minimal` | §8.2.8.7 | ✅ | POST/PUT/PATCH return 204; `Preference-Applied` set |
| `Prefer: return=representation` | §8.2.8.7 | ✅ | `Preference-Applied` set in response |
| `Prefer: maxpagesize` | §8.2.8.7 | ✅ | Applied as the page size when `$top` is absent. **Unconditionally overrides a configured `MaxTop`** - there is no `Math.Min` against the server cap, so a client can request (and receive) a page larger than `MaxTop`. See Known Limitations. |

## Error responses

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `error.code` and `error.message` | §9.3 | ✅ | All error responses, including malformed/wrong-shaped POST, PUT, and PATCH request bodies and unsupported `Content-Type` values (400/415) - these read and deserialize the body manually rather than relying on ASP.NET Core's implicit body-binder, which used to short-circuit with an empty 400/415 body before this formatting code ran |
| `error.target` | §9.3 | ✅ | Set on key-mismatch and invalid-key errors |
| `error.details` array | §9.3 | ⚠️ | The internal `ODataError` helper accepts a `details` parameter and will serialize it, but no call site in the framework currently populates it - the array never appears in a real response today |

## Service document and metadata

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Service document (`GET /`) | §11.1 | ✅ | Lists all entity sets |
| CSDL metadata (`GET /$metadata`) | §11.1 | ✅ | Full EDM XML |
| Entity set declarations | §9.1 | ✅ | |
| Navigation property declarations | §9.1 | ✅ | |
| Bound function/action declarations | §9.1 | ✅ | |
| Unbound function/action declarations | §9.1 | ✅ | |

## Known limitations

| Feature | Notes |
|---------|-------|
| SQL column projection for `$select` | All columns fetched; `$select` trims response JSON only |
| `Prefer: maxpagesize` can exceed `MaxTop` | Client-requested page size unconditionally overrides a configured `MaxTop` cap with no ceiling enforced - a client can request an unbounded page. Worth hardening with a `Math.Min(preferredPageSize, source.MaxTop)` clamp if `MaxTop` is meant to be a hard server-side cap. |
| ETag check atomicity | GET-then-write has a race window; use a database-level mechanism for true atomistic concurrency |
| `If-None-Match` on POST | Not implemented; validate in the `Post` handler if needed |
| `$compute` | Unimplemented. `Microsoft.AspNetCore.OData` is pinned to `[9.4.*, 10)` on all target frameworks (including net10.0), which deliberately excludes the v10+ release that adds `$compute` support - the blocker is the package version pin, not the target framework. |
| JSON batch requests | Not supported |
| `error.details` array | Mechanism exists in the `ODataError` helper but is currently dead code - no call site populates it |
