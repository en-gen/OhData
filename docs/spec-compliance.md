# OData 4.0 Spec Compliance

OhData targets the [OData 4.0 specification](https://docs.oasis-open.org/odata/odata/v4.0/odata-v4.0-part1-protocol.html). This page documents which sections are implemented and any known limitations.

## Conformance posture

OhData has not been run against an official OASIS conformance test suite - the table below is our
own honest self-assessment against the four conformance tiers the protocol describes, derived from
the row-by-row detail further down this page. Treat it as a map of "what to expect", not a
certification claim.

| Tier | Posture |
|---|---|
| **Minimal conformance** (read entities and collections, `$top`/`$skip`/`$orderby`/`$select`/`$count`, error format, content-type negotiation) | ✅ Met |
| **Minimal-updatable** (create/update/delete entities, `$ref` link management) | ✅ Met |
| **Intermediate conformance** (functions/actions, `$expand`, `$search`, batch requests, deep insert) | ⚠️ Substantially met, with named exceptions: **JSON batch requests are not supported** (see Known Limitations below); `@odata.bind` (link an existing entity inline during insert) returns `501 Not Implemented` rather than being honored; `PATCH` partial-merge on a complex property and raw `/$value` property *writes* are documented non-goals (see Individual property access below) |
| **OData 4.01 / Advanced conformance** (`$compute`, aliases, cross joins, and other 4.01-only additions) | ❌ Not targeted - `$compute` is unimplemented because the pinned `Microsoft.AspNetCore.OData` package range predates 4.01 support (see Known Limitations); no other 4.01/Advanced feature is attempted |

## Protocol headers

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `OData-Version: 4.0` response header | §8.2.6 | ✅ | Added to all responses |
| `OData-MaxVersion` request header | §8.2.7 | ✅ | Parsed as `major.minor` (whitespace-tolerant); `4.0` or higher (`4.01`, `5.0`, ...) proceeds, a value below `4.0` or an unparseable value is rejected with `400 Bad Request` (`UnsupportedODataVersion`). Enforced at the OData route-group level, so it applies to the service document, `$metadata`, and every entity-set/bound-operation route. Never echoed in responses (request-only header). |
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
| `round()` midpoint rounding | Part 2 §5.1.1.9 | ✅ | Round-half-away-from-zero by default (spec-compliant, e.g. `2.5 → 3`, `-2.5 → -3`) via a post-`ApplyTo` expression rewrite on the `GetQueryable` path. Set profile/global `RoundingMode = BankersRounding` to restore .NET's default banker's rounding (`2.5 → 2`) - see Known Limitations for why that override exists, and `docs/query-options.md#round-midpoint-rounding` |
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
| Deep insert (nested related entities in POST) | §11.4.2.2 | ✅ | `AllowDeepInsert = true` (profile-level, entity-level granularity — no per-navigation opt-in). Rides the existing `Post` handler; no new route/delegate. Default (`false`): nested navigation-property values are stripped (set to `null`) before `Post` is invoked. Opt-in (`true`): the full deserialized graph is passed to `Post`, which owns atomic persistence (e.g. one EF Core `SaveChanges`); the `201` response echoes the handler's return value, including populated nested navigation values. See `docs/deep-insert.md` |
| `@odata.bind` (link existing entity during insert) | JSON format §8.5 | ❌ | Not implemented — detected anywhere in a POST body (top level or nested) and rejected with `501 Not Implemented` rather than silently ignored. Use `$ref` endpoints to link existing entities |

## Navigation and links

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Navigation property routes | §11.2.3 | ✅ | `GET /Set({key})/Nav` |
| Navigation `$count` | §11.2.3 | ✅ | `GET /Set({key})/Nav/$count` |
| Navigation with `$select` | §11.2.3 | ✅ | |
| `$ref` get link(s) | §11.4.6.1 | ✅ | `GET /Set({key})/Nav/$ref` returns populated `@odata.id` (collection or single) when `refTargetEntitySet` is configured on the navigation; otherwise an empty envelope |
| `$ref` add link | §11.4.6.1 | ✅ | `POST /Set({key})/Nav/$ref` |
| `$ref` remove link | §11.4.6.2 | ✅ | `DELETE /Set({key})/Nav/$ref` |
| POST related entity via navigation | §11.4.2.1 | ✅ | `POST /Set({key})/Nav` — collection navigations only, via the `post` parameter on `HasMany`. `201 Created` (`Location`/`@odata.id` when `refTargetEntitySet` is configured); `Prefer: return=minimal` → `204` + `OData-EntityId`; handler returning `null` → `404` (parent not found); malformed body → `400`; non-JSON content type → `415`. No `post` handler → route not registered (`405` from the coexisting `GET` nav route) |

## Individual property access

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Get individual property | §11.2.6 | ✅ | `GET /Set({key})/Prop` — rides the existing `GetById` handler; `PropertyAccessEnabled` (default `true`) gates it, requires `GetById` to be configured. Returns the `{"@odata.context":...,"value":...}` envelope |
| Null property value | §11.2.6 | ✅ | `204 No Content` |
| Get individual property raw value | Part 2 §4.6/4.7 | ✅ | `GET /Set({key})/Prop/$value` — `text/plain` for primitives (invariant culture), `application/octet-stream` for `byte[]` |
| Raw value of a null property | Part 2 §4.7 | ✅ | `404 Not Found` — the raw value does not exist |
| Raw value of a complex property | Part 2 §4.7 | ✅ | `400 Bad Request` — no raw representation; use the non-`$value` envelope form instead |
| Property-route/bound-function collision detection | — | ✅ | Startup validation (`app.MapOhData()`) throws `InvalidOperationException` if an entity-level bound function shares a name with a structural property |
| Update individual property (`PUT`/`PATCH`) | §11.4.9.1/.2 | ✅ | `PUT`/`PATCH /Set({key})/Prop` with body `{"value":...}` — rides the existing `Patch` handler (built as a one-property `Delta<TModel>`); registered when `PropertyAccessEnabled` and `Patch` are both configured. `PUT` full-replaces complex properties; `PATCH` on a complex property is documented non-support (`400`, see below) |
| Set property to null (`DELETE`) | §11.4.9.3 | ✅ | `DELETE /Set({key})/Prop` — `204` on a nullable property, `400` on a non-nullable property |
| Key property write | §11.4.9 | ✅ | `PUT`/`PATCH`/`DELETE` on the key property always `400 Bad Request` — the key is immutable |
| `PATCH` (partial merge) on a complex property | §11.4.9.2 | ❌ | Documented non-support — `PUT` full-replacement is supported; merge is not. Returns `400 Bad Request` rather than a bare `405` |
| `PUT /Set({key})/Prop/$value` (raw-value write) | §11.4.9.1.2 | ❌ | Not supported — raw `/$value` remains read-only; use the enveloped `PUT .../{Property}` form |

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
| `round()` + `RoundingMode.SpecCompliant` may not translate on every EF Core provider | The spec-compliant rewrite emits `Math.Round(value, MidpointRounding.AwayFromZero)`, which not every EF Core provider can translate to SQL - a query using `round()` may throw a translation exception. Set `RoundingMode = BankersRounding` (per-profile or via `EntitySetDefaults`) to fall back to the single-argument `Math.Round` overload the provider could already translate, at the cost of reverting to banker's rounding on midpoints. |
| Priority-1 `GetODataQueryable` path does not inherit `RoundingMode` | The profile calls `ApplyTo` itself on that path, so the framework's post-`ApplyTo` rounding rewrite never runs against it - `round()` keeps .NET's default banker's-rounding semantics there regardless of `RoundingMode`, unless the profile applies the same rewrite manually. |
