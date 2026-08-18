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

## JSON payload casing (§4.4)

Response property names are serialized in **PascalCase by default** — the same identifiers the EDM
declares in `$metadata` — so payload casing matches `$metadata` casing, as §4.4 requires. This makes
OhData correct out of the box for case-sensitive OData-native clients (e.g. `Microsoft.OData.Client`).
The casing is OhData-owned (not inherited from the host's `HttpJsonOptions`); switch to camelCase with
`AddOhData(o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase))`. The OpenAPI/Swagger
companion packages generate schema property names under this same policy, so the documented casing
matches the wire. See
[query-options.md → JSON property casing](query-options.md#json-property-casing).

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
| `If-Match` on PUT/PATCH/DELETE | §8.2.5 | ✅ | 412 on mismatch; `*` wildcard supported. `If-Match` (including `*`) against a resource that does not exist returns `412 Precondition Failed`, not `404` — the existence check happens before the wildcard short-circuit, per RFC 7232 §3.1 / §11.4.1.1 |
| `If-Match` with multiple ETags | §8.2.5 | ✅ | Comma-separated list per RFC 7232 §3.1 |
| `If-None-Match` on GET → 304 | §8.2.5 | ✅ | Returns 304 Not Modified when ETag matches |
| `If-None-Match: *` on PUT (create-guard) | §11.4.4 | ✅ | When `AllowUpsert` is enabled: if the entity already exists → `412 Precondition Failed`; if not → proceeds as an insert. Requires `GetById` to check existence up front. A no-op when the header is absent or `AllowUpsert` is off |
| Weak ETag prefix (`W/`) | §2.3 | ✅ | Stripped before comparison |

## Response annotations

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `@odata.context` on data responses | §10 | ✅ | Collections, entities, the service document, and single-valued navigation results. Not set on error responses; `GET /$metadata` is XML so the annotation doesn't apply there. |
| `@odata.context` projection suffix with `$select` | §10.7/§10.8 | ✅ | `$select` narrows the context URL to the projected form (`#Set(prop1,prop2)` for collections, `#Set(prop1,prop2)/$entity` for a single entity), listing properties in the order the client requested them. Wired on all three collection-GET paths, `GetById`, and navigation-collection routes; omitted (context unchanged) when no `$select` is present |
| `@odata.count` inline | §11.2.6.5 | ✅ | When `$count=true` |
| `@odata.id` entity self-link | §4.5.8 | ✅ | On GET, POST, PUT, PATCH responses |
| `@odata.etag` in body | §4.5.3 | ✅ | When ETags configured |
| `@odata.nextLink` | §11.2.6.7 | ✅ | When page size equals `MaxTop` |
| `Nav@odata.nextLink` (nested server-driven paging) | §11.2.6.7 / JSON §4.5.5 / JSON §24 item 15 | ✅ opt-in | JSON Format §24 item 15 — *"MUST include the `odata.nextLink` control information in partial results for entity collections"* — is a `MUST` **once partial results are returned**, and OhData never returns partial results without one. An expanded collection larger than `MaxExpandTop` is either rejected with `400` (returning no partial result at all, which the `MUST` does not reach) or, on a profile that sets both `MaxExpandTop` and `ExpandPagingEnabled`, served as its first `MaxExpandTop` children **with** `Nav@odata.nextLink` pointing at `GET /{Set}({key})/{Nav}?$skip=N`. Silent truncation exists in neither configuration. Emission is restricted to a *truly bare* `$expand` at depth 1; every other over-ceiling shape keeps the `400`. Emitted in the 4.0 `@odata.`-prefixed form (the 4.01 short form is a `SHOULD`; OhData sends `OData-Version: 4.0`), and placed after the array — JSON §20.2 exempts `nextLink` from the "immediately prior" rule. `Nav@odata.count` and `Nav@odata.nextLink` never coexist: a nested `$count=true` makes an expand non-bare, so a paged nested collection never reports a count that would have to be the full one (§11.2.4.2). See [query-options.md](query-options.md#nested-server-driven-paging-expandpagingenabled-313) |
| `@odata.context` on bound operations | §10 | ✅ | Included when the function/action return type is the profile's model type or `IEnumerable<TModel>`. A recognized Edm-primitive return type (string, numeric types, `bool`, `Guid`, date/time types, `byte[]`) gets the JSON §11 individual-value envelope (`{"@odata.context":".../$metadata#Edm.<Type>","value":<primitive>}`); an arbitrary non-model, non-primitive return type is returned unwrapped |
| `OData-EntityId` response header | §8.3.4 | ✅ | On any `204 No Content` that creates/upserts an entity (POST/upsert-PUT with `Prefer: return=minimal`); omitted on a plain update-PUT 204 |

## Collection queries

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `$filter` | §11.2.6.1 | ✅ | Comparison, logical, arithmetic, string functions |
| Capability-flag enforcement ("parse or reject") | §8.2.1 / Minimal item 7 | ✅ | `FilterEnabled`/`OrderByEnabled`/`SelectEnabled`/`ExpandEnabled`/`CountEnabled` are enforced at runtime on every collection GET path (`GetQueryable`, `GetAll`, Priority-1) and on `GetById` (`$select`/`$expand`): a disabled option present in the request returns `400` (`UnsupportedQueryOption`) instead of being applied or silently ignored |
| Unimplemented-option rejection ("parse or reject") | §8.2.1 / Minimal item 7 | ✅ | System options the framework does not implement - `$apply`, `$compute`, `$index`, `$deltatoken` - return `400` (`UnsupportedQueryOption`) on every collection GET path rather than being ignored silently. (`$compute`/`$apply` remain unimplemented per Known Limitations; this row is about rejecting them explicitly.) The navigation-collection route additionally rejects `$filter`/`$expand`/`$search`/`$skiptoken`, which it does not implement |
| Property allowlists | §11.2.6 (model-bound restrictions) | ✅ | `FilterProperties`/`OrderByProperties`/`SelectProperties`/`ExpandProperties` are enforced at runtime via the EDM's model-bound `NotFilterable`/`NotSortable`/`NotSelectable`/`NotExpandable` annotations: an option referencing a non-allowlisted property returns `400` (`InvalidQueryOption`). **Exception: dynamic (open-type) properties are not in the EDM, so they carry no annotation and are not gated** - `Microsoft.AspNetCore.OData` behaves identically; see `docs/open-types.md#dynamic-keys-outside-allowlists` and [#401](https://github.com/en-gen/OhData/issues/401) |
| `round()` midpoint rounding | Part 2 §5.1.1.9 | ✅ | Round-half-away-from-zero by default (spec-compliant, e.g. `2.5 → 3`, `-2.5 → -3`) via a post-`ApplyTo` expression rewrite on the `GetQueryable` path. Set profile/global `RoundingMode = BankersRounding` to restore .NET's default banker's rounding (`2.5 → 2`) - see Known Limitations for why that override exists, and `docs/query-options.md#round-midpoint-rounding` |
| `$orderby` | §11.2.6.2 | ✅ | Multiple keys, asc/desc |
| `$top` | §11.2.6.3 | ✅ | `MaxTop` server-side cap enforced on all three collection paths (`GetQueryable`, `GetAll`, Priority-1). An explicit `$top` above `MaxTop` returns `400`; an omitted `$top` is capped to `MaxTop` (or a smaller `Prefer: maxpagesize`) with a `@odata.nextLink` continuation on every path (#195, #201). `GetAll` applies it as a post-materialization `Take()` and emits a `$skip` link; set `MaxTop = null` to opt a path out of the omitted-`$top` cap (unbounded, no `nextLink`). See `docs/query-options.md` |
| `$skip` | §11.2.6.4 | ✅ | On `GetAll`, applied as a post-materialization `Skip()` |
| `$count` (inline and standalone) | §11.2.6.5 | ✅ | Reports the pre-paging total; on the `ODataEntitySetProfile` (`GetODataQueryable`) path a profile that applies its own `$top`/`$skip` must set `ODataQueryResult.TotalCount` or `@odata.count` falls back to the post-page item count |
| `$search` | §11.2.6.6 | ✅ | Requires a `Search` handler; `400 Bad Request` (`UnsupportedQueryOption`) if unset |
| `$select` | §11.2.4.1 | ✅ | On the `GetQueryable`/EF path an eligible `$select` pushes a column-pruned projection to SQL (#206, `SelectPushdownEnabled` — on by default); JSON post-processing applies the selection on the `GetAll`/Priority-1 paths and as the fallback for ineligible requests |
| `$expand` | §11.2.4.2 | ✅ | The expansion pipeline runs identically on the `GetQueryable`, `GetAll`, and Priority-1 `ODataQueryOptions` paths **and on the single-entity `GET /Set({key})` route**. A **delegate-less** `HasMany`/`HasOptional`/`HasRequired` is folded into an EF Core SQL JOIN (#206, `ExpandPushdownEnabled` — on by default), including nested/multi-level/`$levels`, and including a standard bidirectional relationship (#323 — a related type is materialized through a fresh member-init projection at every level including leaves, which forecloses a serialization cycle structurally regardless of back-references); a **delegate-backed** navigation is resolved by its registered handler per entity (or once per page via a batch handler — no EF Core dependency). Ineligible pushdown (non-EF provider, non-projectable AND cyclic) degrades to the delegate/EDM-only path — safely: response serialization itself is clause-bounded, not object-graph-bounded (#325/#326, the `SerializeBounded` walker, JSON §4.5.1), so a self-referential/bidirectional entity set no longer 500s regardless of which path served it, whole-graph `$expand` or plain `GET`. |
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
| `@odata.bind` (link existing entity during insert) | JSON format §8.5 | ❌ | Not implemented — detected anywhere in a POST body (top level or nested) and rejected with `501 Not Implemented` rather than silently ignored. The other write routes (`PUT`, `PATCH`, navigation-`POST`, structural-property writes, action parameters) give the same `501`, on registrations whose EDM declares an open complex type — the only ones that buffer the body. Use `$ref` endpoints to link existing entities |

## Navigation and links

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| Navigation property routes | §11.2.3 | ✅ | `GET /Set({key})/Nav`. Both the collection and single-valued (`HasOptional`/`HasRequired`) branches carry `@odata.context` (single-valued context: `#Set(key)/Nav/$entity`) |
| Navigation `$count` | §11.2.3 | ✅ | `GET /Set({key})/Nav/$count`. A missing parent returns the OData error envelope (`404`), not an empty body |
| Navigation with `$select` | §11.2.3 | ✅ | Collection navigation only; narrows the response body and the context URL's projection suffix (see `@odata.context` projection suffix above) |
| Navigation `$top`/`$skip` validation | Part 2 §5.1.6 | ✅ | An invalid (non-numeric or negative) `$top`/`$skip` on a navigation-collection route returns `400 Bad Request` (`InvalidQueryOption`), matching the main collection route's validation, instead of being silently ignored and returning the full un-paged collection |
| Navigation unsupported-option rejection | Minimal item 7 | ✅ | `$filter`, `$expand`, `$search`, `$apply`, `$compute`, `$skiptoken`, `$deltatoken` on a navigation route return `400` (`UnsupportedQueryOption`) rather than being silently ignored — the route implements only `$select`/`$orderby`/`$skip`/`$top`/`$count` |
| `$expand` continuation route | §11.2.3 / Minimal item 3, item 7 | ✅ opt-in | `GET /{Set}({key})/{Nav}?$skip=N` — registered only on a profile with both `MaxExpandTop` and `ExpandPagingEnabled` set, and only for a delegate-less, collection-valued navigation whose element type has a single resolvable key. It is the target of `Nav@odata.nextLink` (see Response annotations above) and implements **server-driven paging** for the nested case, satisfying Minimal item 3 there rather than truncating. It accepts **`$skip` only**: every other system query option returns `400` (`UnsupportedQueryOption`), rejected by the `$` sigil so an option this build has never heard of is refused rather than ignored (Minimal item 7). `$format` is the one exemption and is not a data option — §11.2.12 negotiation is handled once by the group filter that wraps the whole OData surface, never reaches this handler, and cannot change a row; an unsupported `$format` **value** is still rejected there. The context URL uses the same parent-path shape as the ordinary navigation route (see Declared deviations). Ordering is a total order on the child key, composed unconditionally on both the first page and every continuation |
| `$ref` get link(s) | §11.4.6.1 | ✅ | `GET /Set({key})/Nav/$ref` returns populated `@odata.id` (collection or single) when `refTargetEntitySet` is configured on the navigation; otherwise an empty envelope. Context URL is `#$ref` (single-valued) or `#Collection($ref)` (collection), per JSON Format §14 / Protocol §10.12 |
| `$ref` add link | §11.4.6.1 | ✅ | `POST /Set({key})/Nav/$ref` (collection navigations) / `PUT /Set({key})/Nav/$ref` (single-valued navigations). Malformed/non-object/empty body → `400`; non-JSON `Content-Type` → `415` |
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
| Collection-bound actions | §11.5.4 | ✅ | `POST /Set/ActionName` (JSON body). Malformed/non-object/empty body → `400`; non-JSON `Content-Type` → `415` (only when the action has parameters — a parameterless action never reads the body) |
| Entity-bound functions | §11.5.4 | ✅ | `GET /Set({key})/FunctionName?params` |
| Entity-bound actions | §11.5.4 | ✅ | `POST /Set({key})/ActionName` (JSON body). Same body-shape/`Content-Type` guards as collection-bound actions |
| Unbound functions | §11.5.3 | ✅ | `GET /FunctionName?params`. Unlike the bound/entity-bound rows above, the result is returned as a bare JSON body with no `@odata.context`/individual-value envelope, even for a model or Edm-primitive result — see `docs/bound-operations.md#unbound-functions-and-actions` |
| Unbound actions | §11.5.4 | ✅ | `POST /ActionName`. Same body-shape/`Content-Type` guards as bound actions; same unenveloped-response caveat as unbound functions above |

## `Prefer` header

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `Prefer: return=minimal` | §8.2.8.7 | ✅ | POST/PUT/PATCH return 204; `Preference-Applied` set |
| `Prefer: return=representation` | §8.2.8.7 | ✅ | `Preference-Applied` set in response |
| `Prefer: maxpagesize` | §8.2.8.7 | ✅ | Applied as the page size when `$top` is absent, capped at `MaxTop`: the honored page size is `min(maxpagesize, MaxTop)`. `Preference-Applied` echoes the value actually applied, not the value the client requested. |

## Error responses

| Feature | Section | Status | Notes |
|---------|---------|--------|-------|
| `error.code` and `error.message` | §9.3 | ✅ | All error responses, including malformed/wrong-shaped POST, PUT, and PATCH request bodies and unsupported `Content-Type` values (400/415) - these read and deserialize the body manually rather than relying on ASP.NET Core's implicit body-binder, which used to short-circuit with an empty 400/415 body before this formatting code ran. The same manual-read/guard pattern also covers every route that reads a JSON body by hand: entity-bound actions, collection-bound actions, unbound actions, and `$ref` POST/PUT |
| `error.target` | §9.3 | ✅ | Set on key-mismatch and invalid-key errors |
| `error.details` array | §9.3 | ⚠️ | The internal `ODataError` helper accepts a `details` parameter and will serialize it, but no call site in the framework currently populates it - the array never appears in a real response today |
| Unhandled handler exceptions | §9.4 | ✅ | A group-level endpoint filter wraps every route (added alongside the `OData-Version`/`OData-MaxVersion` filters) and converts any exception a handler throws — as opposed to an `ODataError` result a handler deliberately returns — into a `500` with the standard error envelope, `code: "InternalServerError"`, and a generic message. The real exception is logged (category `"OhData"`) but its message/stack trace is never included in the response body |

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
| SQL column projection for `$select` | Pushed to SQL on the `GetQueryable`/EF path by default (#206); ineligible requests (no parameterless ctor, setterless projected member, non-EF provider, or `SelectPushdownEnabled = false`) fall back to fetching all columns + trimming the response JSON |
| ETag check atomicity | GET-then-write has a race window; use a database-level mechanism for true atomistic concurrency |
| `If-None-Match` on POST | Not implemented; validate in the `Post` handler if needed. (`If-None-Match: *` on PUT *is* implemented as a create-guard — see Request conditional headers above.) |
| `$compute` | Unimplemented. `Microsoft.AspNetCore.OData` is pinned to `[9.4.*, 10)` on all target frameworks (including net10.0), which deliberately excludes the v10+ release that adds `$compute` support - the blocker is the package version pin, not the target framework. |
| JSON batch requests | Not supported |
| Nested paging is limited to a *truly bare* `$expand` at depth 1 | `Nav@odata.nextLink` is emitted only for a collection `$expand` carrying no nested options (`$skip=0` and `$count=false` are the two identity no-ops that still count as bare) directly under the URL-named entity set. **Depth ≥ 2 stays `400`** over the ceiling: a level with its own nested `$expand` cannot be SQL-bounded at all (the `APPLY`/`LATERAL` constraint), so it is unbounded in *materialization* whatever the response says, and a link there would advertise a bound that does not exist. Any nested `$filter`/`$orderby`/`$select`/`$skip>0`/`$count=true`/`$levels`, and any navigation whose element type has a composite or unresolvable key, likewise keep the `400` — a `$skip`-only continuation cannot carry a nested option, and without a single key there is no total order to walk. Deliberate; see [query-options.md](query-options.md#deliberate-limits-and-why-they-are-limits) and [#410](https://github.com/en-gen/OhData/issues/410) |
| Delegate-backed navigations are not bounded or paged by `MaxExpandTop`/`ExpandPagingEnabled` | A navigation declared with a handler is never in the engaged pushdown tree, so no SQL bound, no ceiling check and no continuation link apply to it, and its size is entirely the handler's responsibility. (A nested `$top`/`$skip` on one is separately rejected with `400`, #294.) Bounding it would mean the framework silently truncating a collection the developer's delegate deliberately returned — a direct weakening of the delegate-safety invariant — so the intended fix is a *contract* change (a delegate overload taking `(key, skip, take, ct)`), not a ceiling applied behind the delegate's back. [#410](https://github.com/en-gen/OhData/issues/410) |
| `Prefer: odata.maxpagesize` is not honoured on nested collections | Honoured on root collections; ignored inside `$expand`, with no `Preference-Applied` emitted on its behalf. An unmet §8.2.8.5 / §11.2.5.7 `SHOULD` rather than a violation, since nothing claims it was applied. Honouring it would make the nested page size request-dependent while the continuation link carries only `$skip`, so hop 2 could not reproduce hop 1's page size. [#412](https://github.com/en-gen/OhData/issues/412) |
| `error.details` array | Mechanism exists in the `ODataError` helper but is currently dead code - no call site populates it |
| `round()` + `RoundingMode.SpecCompliant` may not translate on every EF Core provider | The spec-compliant rewrite emits `Math.Round(value, MidpointRounding.AwayFromZero)`, which not every EF Core provider can translate to SQL - a query using `round()` may throw a translation exception. Set `RoundingMode = BankersRounding` (per-profile or via `EntitySetDefaults`) to fall back to the single-argument `Math.Round` overload the provider could already translate, at the cost of reverting to banker's rounding on midpoints. |
| Priority-1 `GetODataQueryable` path does not inherit `RoundingMode` | The profile calls `ApplyTo` itself on that path, so the framework's post-`ApplyTo` rounding rewrite never runs against it - `round()` keeps .NET's default banker's-rounding semantics there regardless of `RoundingMode`, unless the profile applies the same rewrite manually. |
| Unbound functions/actions have no per-operation auth, and no `@odata.context` envelope | `AddFunction`/`AddAction` are mapped on the same top-level route group as `$metadata`/the service document (no per-profile auth group to sit inside), so only group-level `MapOhData().RequireAuthorization()` can protect them - see `docs/authorization.md`. Their results are also always returned as a bare, unenveloped JSON body, unlike bound/entity-bound operations - see the "Bound operations" table above and `docs/bound-operations.md#unbound-functions-and-actions`. |

## Declared deviations

These are intentional, permanent design choices rather than bugs to be fixed - the code will not
change to "correct" them.

| Deviation | Notes |
|---|---|
| Priority-1 `GetODataQueryable` `@odata.count` is the profile's contract | On the `ODataEntitySetProfile`/`IODataEntitySetEndpointSource` path, the profile applies `$top`/`$skip` itself and returns an `ODataQueryResult<TModel>`. `@odata.count` is not second-guessed: if the profile omits `TotalCount`, `@odata.count` reflects only the returned page's item count (not the true total), so a profile that pages itself must set `ODataQueryResult.TotalCount` explicitly. **Paging, however, is now enforced by the framework (#195):** if the profile does not set `NextLink` and the client omits `$top`, the framework caps the materialized result to `MaxTop` (or a smaller `Prefer: maxpagesize`) and emits an `@odata.nextLink` continuation - so this path can no longer return an unbounded set. As of #360 that continuation carries its offset in a framework-private custom query option (`ohdata-skiptoken`) which the framework applies itself, rather than the `$skip` it used to emit and leave to the profile; see [`$top` and `$skip`](query-options.md#top-and-skip). An oversized `$top` is rejected with `400` on this path too. A profile-supplied `NextLink` still takes priority and disables the framework cap. See `ODataQueryResult<TModel>` in `src/OhData.AspNetCore/ODataQueryResult.cs`. |
| `$expand` continuation for a nonexistent parent key is `200` + empty `value`, not `404` | `GET /{Set}({key})/{Nav}?$skip=N` (the `Nav@odata.nextLink` target, #313) answers a key that matches no entity with `200`, an empty `value` array and no link. `Microsoft.AspNetCore.OData` returns `404` for the equivalent request. The route composes `parents.Where(p => p.Key == k).SelectMany(p => p.Nav)`, and a `SelectMany` cannot distinguish "no such parent" from "a parent that has no children" — both produce zero rows. Telling them apart requires an existence probe, i.e. a **second round trip on every continuation**, to improve the status code of a request a conforming client never issues: it only ever follows a link the server emitted, which by construction names a parent that existed. Contrast the delegate-backed navigation route on the same URL shape, where the handler decides and returning `null` does produce `404`. Recorded as an accepted limit in [#410](https://github.com/en-gen/OhData/issues/410). |
| Navigation-collection `@odata.context` uses the parent-path shape | `GET /{EntitySet}({key})/{Nav}` responses use `#{EntitySet}({key})/{Nav}` as the context fragment (the path that produced the result) rather than `#{TargetEntitySet}` (the target entity set's own name), even when the navigation's target entity set is independently addressable. This is a deliberate reading of §10.4 ("the context URL... identifies the type of the payload by ... the last segment of the request URL that identifies a type"), which permits the parent-relative form; it favors traceability back to the request over resolvability to the shortest canonical set name. |
