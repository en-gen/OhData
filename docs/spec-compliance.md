# OData 4.0 Spec Compliance

OhData targets the [OData 4.0 specification](https://docs.oasis-open.org/odata/odata/v4.0/odata-v4.0-part1-protocol.html). This page documents which sections are implemented and any known limitations.

## Protocol headers

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `OData-Version: 4.0` response header | §8.2.6 | ✅ | Added to all responses |
| `OData-MaxVersion` request header support | §8.2.7 | ✅ | Request-only header; not sent in responses |
| `Content-Type: application/json` | §8.2.1 | ✅ | All responses |
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
| `@odata.context` on all responses | §10 | ✅ | Collections, entities, errors, metadata |
| `@odata.count` inline | §11.2.6.5 | ✅ | When `$count=true` |
| `@odata.id` entity self-link | §4.5.8 | ✅ | On GET, POST, PUT, PATCH responses |
| `@odata.etag` in body | §4.5.3 | ✅ | When ETags configured |
| `@odata.nextLink` | §11.2.6.7 | ✅ | When page size equals `MaxTop` |
| `@odata.context` on bound operations | §10 | ⚠️ | Not included on function/action responses |

## Collection queries

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `$filter` | §11.2.6.1 | ✅ | Comparison, logical, arithmetic, string functions |
| `$orderby` | §11.2.6.2 | ✅ | Multiple keys, asc/desc |
| `$top` | §11.2.6.3 | ✅ | `MaxTop` server-side cap enforced |
| `$skip` | §11.2.6.4 | ✅ | |
| `$count` (inline and standalone) | §11.2.6.5 | ✅ | |
| `$search` | §11.2.6.6 | ✅ | Requires `Search` handler; 501 if unset |
| `$select` | §11.2.4.1 | ✅ | JSON post-processing; SQL column projection not performed |
| `$expand` | §11.2.4.2 | ✅ | EF Core `Include` on `GetQueryable` path |
| `$skiptoken` (server-driven paging) | §11.2.6.7 | ✅ | Base64-encoded skip token |

## Entity operations

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Get entity by key | §11.2.2 | ✅ | |
| Create entity (POST) | §11.4.1 | ✅ | Returns `201 Created` + `Location` header |
| Update entity (PUT) | §11.4.3 | ✅ | Full replacement |
| Update entity (PATCH) | §11.4.3 | ✅ | Partial update |
| Delta PATCH | §11.4.3 | ✅ | Via `ODataEntitySetProfile.PatchDelta` |
| Delete entity | §11.4.5 | ✅ | `false` return → 404 or 204 (configurable) |
| Upsert via PUT | §11.4.4 | ✅ | `AllowUpsert = true` |
| Key validation on PUT/PATCH | §11.4.3 | ✅ | URL key must match body key; 400 on mismatch |

## Navigation and links

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Navigation property routes | §11.2.3 | ✅ | `GET /Set({key})/Nav` |
| Navigation `$count` | §11.2.3 | ✅ | `GET /Set({key})/Nav/$count` |
| Navigation with `$select` | §11.2.3 | ✅ | |
| `$ref` add link | §11.4.6.1 | ✅ | `POST /Set({key})/Nav/$ref` |
| `$ref` remove link | §11.4.6.2 | ✅ | `DELETE /Set({key})/Nav/$ref` |

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
| `Prefer: maxpagesize` | §8.2.8.7 | ✅ | Used as `MaxTop` when no server cap set |

## Error responses

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `error.code` and `error.message` | §9.3 | ✅ | All error responses |
| `error.target` | §9.3 | ✅ | Set on key-mismatch and invalid-key errors |
| `error.details` array | §9.3 | ✅ | Available in `ODataError` helper |

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
| `@odata.context` on bound operation responses | Not included - return types are arbitrary |
| ETag check atomicity | GET-then-write has a race window; use a database-level mechanism for true atomistic concurrency |
| `If-None-Match` on POST | Not implemented; validate in the `Post` handler if needed |
| `$compute` | Requires `Microsoft.AspNetCore.OData` v10+ (not yet available on net8.0) |
| JSON batch requests | Not supported |
