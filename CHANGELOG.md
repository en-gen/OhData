# Changelog

All notable changes to this project will be documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Fixed

- **⚠ BREAKING CHANGE — the EDM-nullability rejection is one envelope on all five write routes
  (#569, #558).** #355 made the EDM the single authority on nullability for every write family,
  which made the CONDITION the same everywhere. It did not unify the ANSWER — three wordings and
  **two `code` values** shipped:

  | route | `code` | message |
  |---|---|---|
  | `POST`, `PUT`, nav-`POST` | `InvalidBody` | *"…and cannot be null."* |
  | `PATCH` | `InvalidBody` | *"…and cannot be **set to** null."* |
  | `PUT`/`PATCH`/`DELETE /{Set}({key})/{Prop}` | **`BadRequest`** | *"…is **not nullable** and cannot be set to null."* |

  So a client moving from `PATCH /Movies(1) {"Title":null}` to `PUT /Movies(1)/Title
  {"value":null}` got a different error **code** for the same rejection — against the rule #543
  states and #357/#467 both cite. This is 1.7.0's own doing: at `v1.6.0` the property route gated on
  **CLR** nullability, a genuinely different condition, so no divergence existed. **Nothing pinned
  any of the three messages**, which is why they were free to drift.

  All four sites now go through one `NonNullablePropertyError`, and the property writes join the
  rest of the framework's vocabulary at `InvalidBody`. **BREAKING** for a client matching on
  `code == "BadRequest"` or on any of the three message strings.

  **The unified wording is not simply `PATCH`'s, because of #558.** Three different arrivals reach
  this one condition, and *"cannot be null"* is false of the second: the body named the property
  with an explicit `null`; the body sent a value under a spelling the **binder ignored**, so nothing
  bound and the CLR default is `null`; or `DELETE /{Set}({key})/{Prop}`, which supplies no value at
  all. The second is reachable under a non-case-preserving `PropertyNamingPolicy` — measured under
  `SnakeCaseLower`, `POST {"CreatedBy":"x"}` against a `CreatedBy` property answered *"cannot be
  null"* for a request that sent `"x"`, because the body-name table carries EDM and CLR aliases the
  binder does not honour. **Those aliases must stay**: dropping them would make that body *unnamed*,
  skip the gate entirely, and hand the handler a `null` under a `201` — #511's fail-open, strictly
  worse. So the message is what changed. It now states that the request supplied no non-null value,
  which is true of all three arrivals.

  The new suite asserts the five families **against each other** rather than against a literal, so a
  future reword stays green and a future fork does not.

- **⚠ BREAKING CHANGE — an explicit `null` for a reference-typed KEY is now rejected before the
  handler runs (#557).** `BuildEdmRequiredProperties` excluded the entity key from the nullability
  gate outright. The recorded justification was that a service-generated key is routinely **omitted**
  (§11.4.2) — which was correct while #355's gate also rejected omission, and became vestigial when
  #544 narrowed the gate to *a property the body NAMED with an explicit `null`*. What the exclusion
  still did was hide the explicit-`null` case for a **reference-typed** key.

  Two properties the framework's own `$metadata` describes **identically** answered differently:

  ```xml
  <Property Name="Code" Type="Edm.String" Nullable="false"/>
  <Property Name="Name" Type="Edm.String" Nullable="false"/>
  ```

  **The pre-fix answer depended on the handler, which #557's report does not say.** Both measured by
  ablation:

  | body | handler supplies the key? | before | after |
  |---|---|---|---|
  | `{"Code":null,"Name":"x"}` | no | **`500`**, *after the handler ran* | `400 InvalidBody` |
  | `{"Code":null,"Name":"x"}` | yes (`Code ??= …`) | **`201 Created`** | `400 InvalidBody` |
  | `{"Code":"a","Name":null}` | — | `400 InvalidBody` | unchanged |
  | `{"Name":"x"}` (key omitted) | — | `201 Created` | unchanged |

  The `500` came from `ODataEntityKeyUrlFormatter.Format` ("OData key value must not be null") inside
  response construction — so it arrived **with the handler already run**, meaning a `Post` that
  persists had already persisted. That is strictly worse than #355's original symptom, where the
  write did not happen.

  **So this is not purely `500` → `400`: the second row is a request that used to succeed and now
  does not.** That is intended. `{"Code": null}` is the client asserting the key *is* null, which has
  no valid reading, and the way to ask the service to supply one is to **omit the property** — which
  still works and is unchanged. Remedy: send `{"Name":"x"}` rather than `{"Code":null,"Name":"x"}`,
  or set `RequestBodyNullabilityValidationEnabled = false` to opt the entity set out of the gate
  entirely.

  The omission exemption now comes from the gate's own `namedByBody` intersection rather than from a
  blanket key exclusion, so it is the same mechanism every other property uses. **A value-typed key
  needs no exclusion of its own** and never did — `int Id` cannot hold `null`, so the deserializer
  rejects it first; that is the pre-existing value-type rule, unchanged.


- **#487's two startup warnings prescribed the same `AllowAnonymous()` fix, where that call does
  OPPOSITE things (#572).** Both audits told the developer to silence them with a lambda spelled
  `a => a.AllowAnonymous()`, over the same `ICategoryAuthorizationBuilder` interface. On an
  entity-set **category** that emits ASP.NET Core's `AllowAnonymousAttribute`, which **overrides a
  host-applied** `app.MapOhData().RequireAuthorization()` — #487's own seam 3, the one it
  deliberately chose not to change. On an **unbound operation** it deliberately does *not* emit the
  attribute, because doing so would let the operation tunnel out from under a host requirement the
  host cannot see. Neither message said which you were getting, and the shared interface member
  documented neither.

  Latent rather than immediate: the category audit returns early when `IAuthorizeData` is already
  present, so it only fires on hosts with **no** group auth, where the attribute has nothing to
  override. It lands later, when the host adds `MapOhData().RequireAuthorization()` — which is
  precisely the mitigation `docs/authorization.md` recommends for these routes. At that point the
  category silenced months earlier tunnels out of the new requirement while the unbound operation
  beside it does not. **A fail-open produced by following the framework's own advice, in the exact
  configuration the framework's own docs recommend.**

  No behaviour changes. Both warning messages now state which `AllowAnonymous()` they mean and what
  it does to a host requirement; the category remedy additionally points at naming the requirement
  you intended, which is the right answer when you only mean "no rule needed here".
  `ICategoryAuthorizationBuilder.AllowAnonymous` documents the asymmetry on the member itself, and
  `docs/authorization.md` — which already described both halves 240 lines apart — now states them
  side by side. `Issue572_TheTwoAnonymousWarnings_DescribeOppositeBehaviours` asserts each message
  carries its own half and **not** the other's, so a future edit cannot quietly re-converge them.

- **⚠ BREAKING CHANGE — an entity-bound action now honours `If-Match`/`If-None-Match` (#566).**
  `POST /{EntitySet}({key})/{Action}` was the one state-changing keyed route that did not call
  `CheckETagAsync`. A conditional header was discarded and the action ran, which is a flat
  violation of **§11.4.1.1** — a MUST whose subject is *"a Data Modification Request **or Action
  Request**"*. §8.2.4 and §8.3.1 name Action Requests as explicitly, and **§11.5.4.1 instructs the
  client to send `If-Match`** for exactly this case: *"To request processing of the action only if
  the binding parameter value … is unmodified, the client includes the `If-Match` header."*

  **Measured on the shipped TestBench before the fix**: `POST /v1/Movies(3)/Rate` carrying a stale
  `If-Match` answered `200` and **mutated the entity**, while `PATCH /v1/Movies(3)` carrying the
  *same* header answered `412`. A client using `If-Match` to guard a read-modify-write loop was
  silently unprotected on precisely the route that exists to encapsulate such a loop.

  Breaking in the safe direction: a request that previously succeeded now answers `412` **only if
  it carried a conditional header that does not match**. A request with no conditional header is
  unaffected, and `CheckETagAsync` returns before touching `GetById` when neither header is
  present, so the ordinary invocation path costs nothing new.

  The gate is placed after the key parses and **before the parameter body is read**, so a refused
  invocation provably runs no user code — the ordering invariant #478 established, which is why
  the tests assert delegate **non-execution** rather than only the status code.

  **Collection-bound and unbound actions stay excluded**, and that half survives on its own
  reasoning: neither has a `{key}` segment or an addressed entity, so there is nothing to load an
  entity tag from, and §11.5.4.1's *"or collection of entities"* half would need a **collection**
  ETag this framework does not compute.

  #478 excluded actions on the reasoning that an action-invocation resource *"has no representation
  and therefore no entity tag"*, citing Protocol §11.5.4. **That phrase appears nowhere in Part 1**
  — `grep -ic "no representation"` over the specification returns `0`. 1.7.0 withdrew the claim
  across all twelve sites carrying it and shipped the behaviour labelled as a known deviation;
  this closes the deviation itself.

### Build

- **`PackageValidationBaselineVersion` moves to `1.7.0`, and the instruction moves to the other end
  of the release cycle (#590).** `docs/releasing.md` told you to bump the baseline in the
  release-**prep** PR — the last few commits before the branch is cut — which leaves it correct for a
  handful of commits and **stale for every commit of the following release**, i.e. exactly when the
  API is changing. Measured: the baseline sat at `1.5.0` through the *entire* 1.7.0 cycle, all 30
  commits, so ApiCompat could not have caught a break introduced in 1.6.0 at any point; it was bumped
  to `1.6.0` during 1.7.0 prep, where it validated nothing that had not already merged. The
  instruction now lives in the close-out step, so the bump rides the back-merge and the next cycle
  develops against the release that just shipped. Verified this pack really used the new baseline
  rather than skipping: all five semaphores deleted first and regenerated, zero `CP####`, and the
  five `1.7.0` baseline packages appeared in the NuGet cache for the first time.

---

## [1.7.0] - 2026-08-31

### Upgrading from 1.6.0

A checklist, not an explanation. Every line links to the entry below, which carries the
measurement, the reasoning and the rejected alternatives. **The baseline throughout this section
is 1.6.0** — the last shipped release — never an intermediate state on this branch.

#### 1. What stops the app starting

Each of these throws now where it did not before. The first four are refused at **bind time** (the
profile constructor, or `AddOhData(…)`); the rest at `MapOhData()`.

- [ ] A **bound or unbound FUNCTION that returns `void`/`Task`/`ValueTask`** (#498). Register it
      with `BindAction`/`BindEntityAction`/`AddAction` instead — an action may return nothing and
      produces `204`.
- [ ] An operation handler with a **`CancellationToken` that is not the last parameter**, or a
      **nullable** one (#498). Move it to the end and make it non-nullable. *Previously the app
      booted and that one operation was unreachable.*
- [ ] An operation handler whose **return type implements `IResult`** (#498). Return the value
      itself. *Previously the app booted and the route served the result object's property bag.*
- [ ] A **capturing `DeltaMapping.Convert(...)` converter** (#488) — including one that captures
      only an immutable local, and an instance method group such as `_dep.Convert`. Remedy is one
      keyword: `static v => …`, or a static method group. Also: a **second `Rename()`/`Convert()`
      for one source property** now throws instead of replacing the first.
- [ ] Two **bound operations of the same kind and binding level sharing a name** in one profile
      (#492).
- [ ] An **unbound operation name colliding with a Priority-1 (`GetODataQueryable`) entity set's
      collection route** (#492) — the check previously could not see Priority-1 sets.
- [ ] A **case-differing** collision that used to slip past an `Ordinal` comparison (#492):
      structural property vs entity-level bound function, navigation `post` vs bound action, and
      the #313 continuation route.
- [ ] An **entity-level bound function whose name matches a navigation route** (#416) — a new
      check; there was none.
- [ ] **`.RequireResource()` on `Create` or `Invoke` with no `GetById` handler** (#486) — the
      key-based navigation-`POST` route and entity-bound operations. *Previously `500`ed on every
      request.*
- [ ] An **`Invoke("Name", …)` authorization rule that resolves to no declared bound operation**
      (#525), and **two `Invoke(name, …)` rules resolving to the same operation** — including two
      spelled identically (#546).
- [ ] **Scanning one profile type into two registrations** via `AddProfilesFrom*` (#424).
- [ ] A **get-only collection model property** in a delta mapping is now *in scope*: map, rename,
      convert or `Ignore()` it (#488). *Previously silently dropped.*

#### 2. What changes status on the wire

- [ ] **Any `$`-prefixed system query option a read route does not implement is now `501`**
      (#359/#380/#353), where most were previously accepted and silently ignored under a `200`.
      This covers a misspelling (`$slect`), a name this build has never heard of, and a real option
      addressed to a route that does not implement it. `$apply`/`$compute`/`$index`/`$deltatoken`
      move `400` → `501`; **the error code and the message bytes are unchanged**, so only
      status-code branching moves.
- [ ] `GET /{Set}({key})?$select=…&$skiptoken=` and `GET /{Set}/$count?$skiptoken=`:
      `400 InvalidQueryOption` → `501 UnsupportedQueryOption`.
- [ ] The `$expand` continuation route now **honours** `$SKIP`/`$FORMAT` in mixed case where it
      answered `400` — the one place this release becomes more accepting.
- [ ] A **`Post` handler returning `null`**: `400` → `500` (#496). `Post` cannot return an error
      result, so refuse the create before the handler.
- [ ] A handler that **throws `ODataException` or `FormatException` from its own code**:
      `400` → `500` (#496). Retry logic that read the old `400` as "do not retry" will now see a
      `500`.
- [ ] An explicit **`null` for a property `$metadata` declares `Nullable="false"`** is now `400`
      before the handler runs (#355) — on `POST`, `PUT`, `PATCH`, the navigation-`POST` route and
      the structural-property writes, which previously let it reach the handler (typically an EF
      `500`). `PUT /{Set}({key})/{Prop} {"value":null}` on a non-nullable string goes `204` → `400`.
      An **omitted** property is not affected. Opt out per entity set with
      `RequestBodyNullabilityValidationEnabled = false`.
- [ ] A **write body larger than 30,000,000 bytes** is `413` (#474). Only visible on a host that
      raised or removed its own `MaxRequestBodySize`; the default Kestrel limit is the same number.
      Raise `MaxRequestBodyBytes`, or set it to `null` server-wide.
- [ ] A **bound FUNCTION returning more than `MaxTop` entities of its own set's type** now returns
      the first `MaxTop` plus `@odata.nextLink` (#357); a **bound ACTION** in the same position
      returns `500` (#543), because an action invocation has no GET-addressable continuation. On
      both, `$top` above `MaxTop` is `400` and a malformed `$top`/`$skip` is `400`, where all four
      were previously ignored. `MaxTop = null` restores the old behaviour byte for byte.
- [ ] A **`PATCH` key mismatch** spelled in a non-case-preserving `PropertyNamingPolicy`'s casing
      is now `400` (`target: key`) where it was silently dropped (#536).
- [ ] The `413` from the body-limit fast-reject now carries `OData-Version: 4.0` (#496) — header
      only, no status change.

#### 3. Silent behaviour changes — nothing tells you

These change what your service does with **no status-code change**, so no test that asserts only
status will catch them.

- [ ] **`UseETag` with a *capturing* selector is ~2.5–3.5× slower per request** (#483) — measured
      end-to-end at 0.63–0.69 ms/req against 0.20–0.26 ms/req on `GET /Set(key)`. Correctness
      improved (the selector no longer runs against the disposed startup scope's dependency) and
      nothing warns. A "capturing" selector is one that reads anything other than its own lambda
      parameter — a field, an injected service, or a local. **Remedy: hoist the value so the lambda
      reads only its parameter** (fold it into a model property). Assigning it to a local first does
      **not** help — a captured local is still compiled into a display class. Promoting it to
      `static` does, for a value that genuinely is per-process.
- [ ] **`PATCH` binds body keys it previously discarded** under a non-case-preserving
      `PropertyNamingPolicy` (`SnakeCase*`, `KebabCase*`) (#536). A request that answered `200` and
      changed nothing now changes the properties it named. Default and camelCase hosts are
      unaffected.
- [ ] **A complex type's entity-typed navigation is now omitted from response bodies** (#507) —
      on a **plain `GET` with no query string**. It used to be served inline with no `$expand`
      naming it. `$expand=Meta/Owner` is omitted rather than expanded.
- [ ] **`$expand` starts returning data on a renamed schema** (#508) — if any EDM type name differs
      from its CLR `FullName` (via `ODataConventionModelBuilder.Namespace` or an
      `AdvancedConfigure` override), pushdown was disengaged **model-wide** and every expand
      answered `"Children": []`. It now emits SQL and returns rows. Re-baseline snapshot and
      query-count tests.
- [ ] **A bound operation returning `List<TDerived>` now gets the OData collection envelope**
      (#497) — the body goes from a bare JSON array (navigations inline, no `@odata.context`) to
      `{"@odata.context":…,"value":[…]}` with navigations stripped, under the same `200`. Any
      client parsing the old array breaks.
- [ ] **Every generated OpenAPI action-body schema component is renamed** (#499): `Widgets_Archive`
      → `__default___Widgets_Archive` (unnamed registration) or `v1_Widgets_Archive` (named).
      Document-only; regenerated clients get renamed model classes.
- [ ] **Three new startup `Warning`s** that fire on configurations which are not errors: a
      navigation whose target entity set is protected more strictly than the set declaring it
      (#481), an `Ignore()`d property still in `$metadata` under an `AdvancedConfigure` override
      (#489), and a route left anonymous in a registration that requires authorization elsewhere
      (#487). Expect new log volume, not new failures.

### Breaking

- **⚠ BREAKING CHANGE — a bound ACTION returning a collection of the entity set's own type is now
  bounded by `MaxTop`, and a result that does not fit is refused rather than served (#543).**
  #357 bounded bound *functions* and excluded actions, on a reason that was sound about the
  continuation and wrongly taken to be about the ceiling as well. `WrapBoundOpResult` was called
  with `pagingSource: null` for both action call sites, and that one parameter was doing two jobs —
  "is this route pageable" *and* "here is the startup source to read `MaxTop` off" — so an action
  skipped the bound entirely.

  **Measured on `develop` at `d2d96d8`**, a profile with `MaxTop = 10` over a 25-row store, with a
  `BindAction` and a `BindFunction` both declared `Task<object>` and both returning `List<TModel>`:

  ```
  GET  /ZZObjs                 -> 200  len=10  nextLink=…/ZZObjs?%24skip=10
  GET  /ZZObjs?$top=999        -> 400  InvalidQueryOption "The value of '$top' (999) exceeds the maximum allowed value (10)."
  GET  /ZZObjs/DumpFn          -> 200  len=10  nextLink=…/ZZObjs/DumpFn?%24skip=10
  GET  /ZZObjs/DumpFn?$top=999 -> 400  InvalidQueryOption
  POST /ZZObjs/Dump            -> 200  len=25  nextLink=<none>
  POST /ZZObjs/Dump?$top=999   -> 200  len=25  nextLink=<none>
  POST /ZZObjs/Dump?$top=5     -> 200  len=25  nextLink=<none>
  POST /ZZObjs/Dump?$skip=20   -> 200  len=25  nextLink=<none>
  POST /ZZObjs/Dump?$top=abc   -> 200  len=25  nextLink=<none>
  ```

  That is the exact wire shape #357's entry says it closed, on the operation kind it did not cover:
  the ceiling fully bypassed, `$top` neither applied nor rejected, a malformed `$top` silently
  dropped. It was reachable before #539 through any `Task<object>`-declared handler; with #539 below,
  `Task<IEnumerable<TModel>>` becomes the ordinary spelling of it.

  **What changes on the wire.** `$top`/`$skip` on a bound action are now read and applied, and a
  `$top` above `MaxTop` or a malformed `$top`/`$skip` is a `400 InvalidQueryOption` carrying the
  *same message byte for byte* that the collection `GET` and the bound function already produce.
  With **no** `$top`, an action whose collection result exceeds `MaxTop` (default `1000`) now answers
  **`500`** + the OData error envelope, with the real reason — the count, the ceiling, and the
  remedies — logged. A result at or under the ceiling is unchanged, byte for byte, and no header
  moves. `MaxTop = null` opts the entity set out exactly as it does for `GetAll` and for a bound
  function. `Prefer: maxpagesize` is deliberately **not** honoured on an action and no
  `Preference-Applied` is emitted for it — it is a server-driven-paging preference and there is no
  paging to drive; RFC 7240 makes preferences advisory and forbids claiming `Preference-Applied` for
  one that was not applied.

  **Why refuse rather than page or truncate — the fork, stated so it can be redirected.** Three
  shapes were available and two are unavailable here. (1) *Cap and emit `@odata.nextLink`*, what a
  function does, is invalid: a `nextLink` is a URL the client **GETs** (§11.2.5.7), while an action
  is invoked by `POST` to its action URL (§11.5.4.1) — there is no GET-addressable continuation of
  an action invocation for a link to point at, so it would answer `405`, and re-POSTing a
  side-effecting action to collect page 2 is not a continuation in any case. (An earlier revision of
  this entry argued this from an action-invocation resource having *"no representation"*, citing
  §11.5.4 and cross-referencing #478's ETag exclusion. **That phrase is not in the specification**,
  and the ETag exclusion it leaned on is a known deviation rather than a spec allowance — see #566.
  The conclusion here is unchanged; the reasoning no longer rests on that claim.) (2) *Cap silently* is forbidden by the framework's own M1 rule: no
  configuration leaves a bound in place without either a continuation link or a `400`, never silent
  truncation. (3) Refuse. It is a `500` and not a `400` because #496 settled that distinction in
  this same release: a `Post` handler returning `null` went from "400 blaming the client" to a
  logged `500` because the condition is decided entirely by server-side state, and so is this one —
  the profile declared the ceiling, the handler returned more than fits under it, identically for
  every client and every request. The `400` half of the ceiling (an explicit `$top` above `MaxTop`)
  *is* the client's, and stays a `400`.

  The alternative the owner may prefer is to **keep the exclusion and stop advertising the ceiling
  as universal** — i.e. document `MaxTop` as a bound on collection *GET* routes and bound functions,
  and leave actions unbounded. That is coherent with #465's "advertise only what the route serves"
  rule read as a rule about *advertisement*, but it leaves the DoS ceiling the framework enforces
  everywhere else bypassable through any bound action, which is the sentence #357 exists to make
  false. Reverting to it is one branch in `TryApplyOperationCollectionPaging` and one call-site flag.

  **Two corrections to #357's own entry, both refuted by the measurement above.** *"the bound is
  applied in the runtime collection branch rather than from the declared return type, so a handler
  declared `Task<object>` returning a `List<TModel>` is not a way around the ceiling"* — true of a
  function, false of an action, which never reached that branch's bound. *"It is moot in practice
  besides: `ActionConfiguration.Returns`/`.ReturnsCollection` both refuse an already-declared entity
  type, so a `BindAction` over `TModel` or `IEnumerable<TModel>` cannot be registered at all today"*
  — true only for a *declared* entity return (that throw is real, and is #539); `Task<object>`
  registered and served, and #539 removes the declared-return half as well. Both sentences are
  annotated in place rather than deleted. Riding along, `WrapBoundOpResult`'s
  *"see `AddBoundFunctionPagingMetadata` for why actions are excluded"* pointed at a method that
  contained no such rationale; the method is now `AddBoundOperationPagingMetadata`, it is attached to
  action routes too (an action really does honour `$top`/`$skip` now, which is what
  `OhDataQueryOptionsMetadata` fields mean under #467), and the dangling cross-reference is gone with
  the exclusion it described.

- **⚠ BREAKING CHANGE — `BindAction` can return the entity set's own type; the EDM declaration for a
  bound operation's entity return now goes through `ReturnsFromEntitySet` (#539).**
  `Microsoft.OData.ModelBuilder`'s `ActionConfiguration.Returns<T>()` / `.ReturnsCollection<T>()`
  **refuse a CLR type already declared as an entity type** and direct the caller to
  `ReturnsFromEntitySet` / `ReturnsCollectionFromEntitySet`, which OhData never called and does not
  expose. The `FunctionConfiguration` twins accept the same type. **Measured against
  `Microsoft.OData.ModelBuilder` 2.0.0 on .NET 10.0.11:**

  ```
  action.Returns<TModel>()             -> InvalidOperationException, thrown AT THE CALL:
                                          "The EDM type '…' is already declared as an entity type.
                                           Use the method 'ReturnsFromEntitySet' if the return type is an entity."
  action.ReturnsCollection<TModel>()   -> the same, naming 'ReturnsCollectionFromEntitySet'
  function.Returns<TModel>()           -> OK
  function.ReturnsCollection<TModel>() -> OK
  ```

  So `BindAction((Func<Task<IEnumerable<Widget>>>)Archive)` on the `Widget` profile — `POST
  /Widgets/Archive` answering with the archived rows, an ordinary OData shape — killed `MapOhData()`
  with a message quoting a method the developer could not reach, while the bound *function* of the
  identical signature worked.

  `RegisterEdmReturnType` now calls `ReturnsFromEntitySet` / `ReturnsCollectionFromEntitySet` with
  the **declaring profile's own entity set** whenever the (element) return type is that profile's
  model type. Applied to **functions too**, not behind an is-this-an-action branch, for two measured
  reasons: the CSDL is byte-identical either way (a bound operation declared with
  `ReturnsCollectionFromEntitySet` emits the same `<ReturnType Type="Collection(…)"/>` — no
  `EntitySetPath`, no extra attribute — as one declared with `ReturnsCollection`, pinned by
  `BoundActionEntityReturnTests.BoundFunction_MetadataDeclarationIsUnchanged`), and it is what
  Microsoft's own E2E fixtures do for an entity return on both operation kinds. One unconditional
  rule is also less code than a conditional one and cannot drift between kinds.

  The entity-set name passed is always the one `VisitModelBuilder` opened with, never a name derived
  from the return type, and that is load-bearing: **`ReturnsFromEntitySet` does not validate that the
  name exists — it creates the set.** Measured, a bogus name silently added `EntitySet:Ghosts` to the
  container and to `$metadata`.

  **Floor for the residual.** An operation returning some *other* registered entity type — another
  profile's model — still cannot be expressed, because OhData can only bind an operation's entity
  return to the entity set of the profile that declares it. That case now throws an OhData-authored
  `InvalidOperationException` naming the entity set, the operation, the offending type and the
  remedies, with Microsoft's original as the inner exception, instead of letting a raw ModelBuilder
  string naming an unreachable method reach the developer.

  **Why this is breaking.** A `BindAction` declared over the profile's own model type used to be a
  hard startup failure and is now a working route — which means it now reaches #543's ceiling above,
  and an action returning more than `MaxTop` entities answers `500` rather than never having started.
  Nothing that previously started up changes shape: every `$metadata` document and every response for
  a configuration that built before this change is byte-identical.

- **⚠ BREAKING CHANGE — a bound FUNCTION returning a collection of the entity set's own type is now
  bounded by `MaxTop`, with a `@odata.nextLink` continuation (#357).**
  **Who is affected:** any registration with a `BindFunction`/`BindEntityFunction` returning a
  collection of its own `TModel`, on a profile that has not set `MaxTop = null` —
  `EntitySetDefaults.MaxTop` defaults to **1000**, so this is on by default.
  **Direction:** a response that used to carry the whole collection now carries at most `MaxTop`
  entities plus an `@odata.nextLink`, and a client that reads `value` without following
  continuations **silently sees fewer rows than before**. A client `$top`/`$skip` that used to be
  ignored is now applied, a `$top` above `MaxTop` is `400`, and a malformed `$top`/`$skip` is `400`
  rather than dropped.
  **Remedy:** follow `@odata.nextLink`, or set `MaxTop = null` on the profile or in
  `EntitySetDefaults` to restore the previous behaviour byte for byte. A result already under the
  cap is unchanged.

  Before this, such an operation bypassed `MaxTop`,
  the client's `$top`/`$skip`, and server-driven paging entirely — so the DoS ceiling the framework
  advertises and enforces on every ordinary collection route was fully bypassable through any
  operation that returned a collection, and a `$top` sent against one was neither applied nor
  rejected. Measured on a profile with `MaxTop = 50`:
  `GET /v2/Movies?$top=1000` → `400`, while `GET /v2/Movies/TopRated?count=1000&$top=2` → `200`
  with all 77 entities and no `@odata.nextLink`.

  **What changes on the wire.** `EntitySetDefaults.MaxTop` defaults to **1000**, so this is
  default-on: an operation that used to return more than 1000 entities of its set's own type now
  returns the first 1000 plus an `@odata.nextLink`, and a client that reads `value` without
  following continuations sees a truncated result. A client `$top`/`$skip` is now applied rather
  than ignored, a `$top` above `MaxTop` is `400 InvalidQueryOption` with the *same* message the
  collection route uses, a malformed `$top`/`$skip` is `400` rather than silently dropped, and
  `Prefer: maxpagesize` is honoured (with `Preference-Applied`). Opt out exactly as on `GetAll`:
  set `MaxTop = null` on the profile or in `EntitySetDefaults`, which restores the previous
  behaviour byte for byte. A result already under the cap is unchanged, byte for byte.

  The semantics are #201's `ApplyGetAllPaging`, not a new scheme: the framework is holding a fully
  materialized array and owns the pipeline from there, so an offset continuation is one it can
  always honour. The bound is applied in the **runtime** collection branch rather than from the
  declared return type, so a handler declared `Task<object>` returning a `List<TModel>` is not a way
  around the ceiling — *true for a bound function, and **measured false for a bound action**, whose
  route never reached that branch's bound at all; corrected in the #543 entry below* — while the
  OpenAPI `$top`/`$skip` documentation is attached from the declared type at the single
  `OhDataQueryOptionsMetadata` site (#467), so the only possible divergence is serving a bound the
  document did not promise.

  **Actions were deliberately excluded, and that exclusion was wrong — see the #543 entry below,
  which supersedes this paragraph.** Two claims made here have been measured false and are corrected
  there: that the bound "is applied in the runtime collection branch … so a handler declared
  `Task<object>` returning a `List<TModel>` is not a way around the ceiling" (it was exactly that,
  for an action), and that the exclusion was "moot in practice besides" because
  `ActionConfiguration.Returns`/`.ReturnsCollection` refuse an already-declared entity type (true
  only of a *declared* entity return — `Task<object>` registered fine, and #539 has since made the
  declared spelling work too).

- **⚠ BREAKING CHANGE — a `Post` handler returning `null` is now a logged `500`, not a `400`
  blaming the client (#496).**
  **Who is affected:** any profile whose `Post` handler returns `null` — including one that was
  doing so deliberately, as a rejection.
  **Direction:** `400 {"error":{"code":"BadRequest","message":"Post handler returned null."}}` →
  `500 {"error":{"code":"InternalServerError","message":"An unexpected error occurred while
  processing the request."}}`, with the real exception logged at `Error`. The quoted message is no
  longer produced anywhere in the assembly, so a client matching on it stops matching.
  **Remedy:** return the created entity. `Post` is typed `Task<TModel?>` and cannot return an
  error result, so a create that must be refused has to be refused *before* the handler — with
  `RequestBodyNullabilityValidationEnabled`, a `Create` authorization rule, or by throwing (which
  is the same `500`). Choosing the status code from inside a `Post` handler is not expressible
  today.

  It used to answer
  `400 {"error":{"code":"BadRequest","message":"Post handler returned null."}}` — a server-side
  contract violation reported as a client error, with the server's own handler named back to the
  client, and the only 4xx null policy in the framework (`GetAll` → `200` with an empty collection,
  `GetById`/`PUT`/`PATCH` → `404`, a bound operation → `204`). It now throws, which routes it
  through the group-level filter: the real exception logged at `Error`, and a `500` carrying the
  OData error envelope with a generic message.

- **⚠ BREAKING CHANGE — a handler-thrown `Microsoft.OData.ODataException` or `FormatException` is
  no longer relabelled as a client error (#496).**
  **Who is affected:** any handler that throws either type from its own code — a `GetQueryable`
  proxying a downstream OData service, or any read/write handler doing its own parsing
  (`decimal.Parse` on a CSV column, a `DateTime.Parse` of a downstream field).
  **Direction:** `400 InvalidQueryOption` carrying the exception's own message verbatim, or
  `400 "Invalid key format for <set>: '<key>'"`, → `500 InternalServerError` + the generic
  envelope, with the real exception logged. A client's retry logic that treated the old `400` as
  "do not retry" will now see a `500` and may retry.
  **Remedy:** none needed for correctness — the `500` is the honest answer, and no entity-set
  handler delegate can return an `IResult`, so the old behaviour was never a supported way to
  produce a client error. If your handler was relying on the relabelling to surface a message,
  validate before the handler instead. Framework-raised 400s (a genuinely malformed query option, a
  genuinely malformed key) are unchanged, byte for byte.

  Each read route wraps its whole body in a `try` whose
  `catch (ODataException)` answers `400 InvalidQueryOption` with `ex.Message` passed **verbatim** to
  the client, and every keyed route's `catch (FormatException)` answers
  `400 "Invalid key format for <set>: '<key>'"`. Both clauses also enclose handler invocation, so a
  handler proxying a downstream OData service turned a dependency fault into a client-blamed `400`
  carrying its own message — a targeted bypass of the rule that no internal exception message
  reaches the client — and a handler-origin `FormatException` produced a `400` asserting the key was
  malformed for a request whose key had parsed one line earlier. Both are `500` + the OData error
  envelope now, with the real exception logged. Framework-raised 400s are unchanged, byte for byte.

- **An explicit `null` for a property the EDM declares `Nullable="false"` is now `400` before the
  handler runs (#355, narrowed by #544/#545).** The framework publishes the nullability of every
  structural property in the CSDL it generates, and until now enforced none of it: a `null` for a
  property declared `Nullable="false"` reached the handler, and the persistence layer's rejection
  surfaced as a generic `500` — measured on the shipped test bench, `POST /Movies {"Title":null}` →
  `500 InternalServerError` from EF's *"Required properties '{'Title'}' are missing"*. A violation the
  framework could see at its own boundary, reported as a server fault. `BuildEdmRequiredProperties`
  now asks the EDM once per type at startup, and `POST`/`PUT`/`PATCH`, the navigation-`POST` create
  route and the structural-property writes answer `400 InvalidBody` — *"Property 'X' is declared
  non-nullable by the service metadata and cannot be null."* — before any handler delegate runs.

  **The rule fires only on a property the request body NAMES with an explicit `null`. An OMITTED
  property is not a violation on any verb.** An earlier revision of this change also refused an
  omission on `POST`/`PUT`; #544 removed that leg before release, and this entry describes the shipped
  behaviour. The removed leg cited **§11.4.2**, which requires nothing of the kind — its only
  MUST-fail is *"The service MUST fail if unable to persist all property values **specified in the
  request**"*, about values sent. §11.4.2 also permits omission outright, though for exactly two
  categories rather than the broad set an earlier revision of this entry claimed: *"Properties
  computed by the service (annotated with the term `Core.Computed` …) and properties that are tied
  to properties of the principal entity by a referential constraint, can be omitted and MUST ignored
  if included in the request."*

  **No clause in Part 1 mandates a `400` for an omitted property.** The nearest one, **§11.4.3**, is
  PUT-only and prescribes the opposite remedy: *"Missing non-key, updatable structural properties not
  defined as dependent properties within a referential constraint MUST be set to their default
  values."* That is a statement about what the service **stores**, not about refusing the request,
  and OhData leaves it to the handler that owns persistence. (An earlier revision of this entry
  quoted §11.4.3 as conditioning the refusal on the property having *"no service-generated or
  default value"* — **that phrase is not in the specification**; the only occurrence of
  "service-generated" anywhere in Part 1 is in §11.3.1, about delta links. The conclusion #544/#545
  reached was right; the citation supporting it was invented, and the real clause supports it
  better.) `Microsoft.AspNetCore.OData` draws the same line: `ApplyStructuralProperties`
  loops over payload properties only with no reverse pass, while `ValidateNullValueAllowed` rejects an
  explicit `null`.

  **That is what makes the rule derivable from the wire (#545).** Three properties `$metadata`
  describes **identically** as `Nullable="false"` now answer identically. Measured, the unreleased
  #355 revision → what ships — note the "before" column is that revision, **not 1.6.0**, which had no
  such validation at all and answered `201` to every row:

  | CLR declaration | omit → | explicit `null` → |
  |---|---|---|
  | `string X { get; set; } = ""` | `201` → `201` | `400` → `400` |
  | `string X { get; set; } = null!` | **`400` → `201`** | `400` → `400` |
  | `int Year` | `201` → `201` | `400` → `400` (deserializer-worded) |

  No dependence on whether the developer wrote `= ""` or `= null!`, and none on CLR
  value-versus-reference — neither of which appears in `$metadata`.

  The property-write route is the proof that *"ask the EDM"* is not stylistic. It already had a
  nullability check, built on `IsNullableClrType` — for which **every reference type is nullable** — so
  `PUT /{Set}({key})/Name {"value":null}` on a `Nullable="false"` string was a `204`. There is one
  authority now. `PATCH` and the property writes go through the partial-update twin, which checks only
  a property the body actually **named**: a `Delta<T>` is a change set, so an absent property means
  *"leave it alone"*, not *"set it to nothing"*. `POST`/`PUT`/nav-`POST` now ask the same question,
  through `CollectPresentBodyMemberClrNames` — #506's existing top-level scanner, shared rather than
  transcribed — over a table built by `BuildBinderBodyNameTable`, the deep-write strip's own builder
  extracted so both gates derive one answer, keyed off `JsonTypeInfo.Properties[].Name` so the
  spelling the gate looks for is the spelling the binder matches (#511). **No route BUFFERS the body
  an extra time** — every site already had the bytes (`postPrepared.Body`, `putPrepared.Body`, #456's
  `putBuffered`/`navBuffered`), and no new reader kind is introduced. On the two streaming branches
  (`PUT` and nav-`POST`) it is one more pass over that same buffer, as #456's `@odata.bind` scan
  already was, through `CreateBinderParityReader`.

  Four deliberate exclusions, each of which would otherwise reject a legal request: the entity **key**
  (every EDM key is `Nullable="false"`, and §11.4.2 explicitly permits omitting a service-generated
  value); a non-nullable **value type** (`int Year` cannot hold `null`, so a JSON `null` for it is
  already a `JsonException` → `400` from the binder); a member the EDM declares that no readable CLR
  property backs; and anything the EDM does not declare at all — which exempts `Ignore()`d properties
  for free. Top level only: a `null` inside a nested complex value is not checked.

  > **⚠ BREAKING CHANGE.** An adopter whose handler relied on receiving an explicit `null` for a
  > property the EDM declares `Nullable="false"` now gets a `400` instead, and the handler does not
  > run. `ODataConventionModelBuilder` honours nullable reference-type annotations, so a property
  > written the ordinary way — `public string CreatedBy { get; set; } = null!;`, with no `[Required]`
  > anywhere — emits `Nullable="false"`; sending `{"createdBy":null}` for it is now refused, while
  > **omitting** it is accepted exactly as before. Opt out per entity set, or in `EntitySetDefaults`,
  > with `RequestBodyNullabilityValidationEnabled = false`.

- **`EntitySetDefaults.MaxRequestBodyBytes` now defaults to 30,000,000 bytes (#474).** The #203
  body-limit filter does both of its jobs — the `Content-Length` fast-reject and the per-request Kestrel
  `MaxRequestBodySize` assignment — only when `OhDataBodyLimitMetadata` is attached, and that metadata
  existed only when a profile or `EntitySetDefaults` set `MaxRequestBodyBytes`, **which defaulted to
  `null` at both levels**. On a default configuration neither half ran, so the only ceiling was the
  host's own Kestrel limit, and a host that raised or disabled it had nothing at all bounding the
  buffered materialisation the write path performs. The new default is `30_000_000` —
  `EntitySetDefaults.DefaultMaxRequestBodyBytes`, which is **Kestrel's own default** — so a default host
  sees the same byte count, reported one layer up. An unbound action, which belongs to no profile and so
  had no per-route metadata to resolve, now falls back to the registration's default rather than to
  nothing.

  **A defect this fix would itself have introduced, found and closed in the same change:** #203 assigns
  `MaxRequestBodySize` **unconditionally**, which was right while the limit could only come from the
  adopter, but a non-`null` framework default would then have *raised* a deliberately-lowered host
  ceiling — measured, `1,000,000 → 30,000,000` on every OData write, a hardening step loosened by a
  security fix, on a registration that configured nothing. The assignment is clamped with `Math.Min`
  when the resolved limit **is** the framework's own constant; an explicitly configured
  `MaxRequestBodyBytes` still overrides in both directions, because *"this set accepts up to 4 MB"* is a
  deliberate per-route decision and still behaves like one.

  > **⚠ BREAKING CHANGE — inert on a default host.** A host that raised or disabled Kestrel's own limit
  > now gets `413 RequestEntityTooLarge` at 30,000,000 bytes on every OData write, unbound actions
  > included. Raise `EntitySetDefaults.MaxRequestBodyBytes` to whatever that host really accepts, or set
  > it to `null` to restore the previous behaviour of no OhData-level ceiling.

- **Nine misconfigurations that booted and then failed on every request are refused at startup or at
  bind time (#492, #498, #486, #416).** One shape throughout: a configuration passes `MapOhData()` and
  the failure lands on the request path — as an `AmbiguousMatchException` from ASP.NET Core's routing,
  where *neither* endpoint runs, or as a guaranteed `500`. Every new throw names the offending profile
  or operation and the remedy.

  - **Priority-1 entity sets were invisible to the unbound-operation collision check (#492 §1).** The
    check asked `HasGetAll || HasGetQueryable`, so an unbound operation colliding with a
    `GetODataQueryable` set's collection route slipped past it. There is one answer now,
    `IEntitySetEndpointSource.HasCollectionGet`, implemented once on `EntitySetProfile` rather than
    re-implemented on `ODataEntitySetProfile` — a new read path arrives as a new *interface*, so one
    site keeps answering correctly.
  - **Three collision checks compared names with `Ordinal` while route matching is case-insensitive
    (#492 §2)**: structural-property-versus-bound-function, navigation-`post`-versus-bound-action, and
    the #313 continuation route. All three are `OrdinalIgnoreCase` now.
  - **New check: an entity-level bound function versus a navigation *route* (#416 / #492 §3).** Keyed
    off `NavigationRoutes` rather than off which delegate was supplied, because `MapEntitySet` maps a
    `GET` for every entry — including a `post`/`addRef`-only navigation whose `GET` 404s. A declared
    navigation with no route at all stays legal.
  - **Duplicate bound-operation names within one profile (#492 §4)**, refused at **bind time**, per
    `(kind, binding level)`. Deliberately not from `MapEntitySet`: `Microsoft.OData.ModelBuilder`
    rejects a repeated *action* name itself, earlier, from inside `VisitModelBuilder`, with a message
    naming no profile, entity set or remedy — so a downstream check would never run for half the cases.
  - **`.RequireResource()` on `Create` or `Invoke` with no `GetById` handler (#486).** The #199 Layer B
    resource filter also attaches on the key-based navigation-`POST` route and on entity-bound functions
    and actions, and it calls `GetById!.Invoke(...)`; both configurations passed startup and then
    `NullReferenceException`ed on 100% of requests. The collection-level members of those two categories
    are deliberately excluded and pinned by controls — the collection `POST` evaluates its `Create`
    requirement inline against the deserialized model, and a collection-bound operation's route has no
    `{key}` segment for the filter to read.
  - **A void-returning bound function (#498 §1).** Previously a hard startup crash out of `EdmFunction`'s
    own constructor — `ArgumentNullException (Parameter returnType)` — naming nothing.
  - **A `CancellationToken` in a non-trailing position (#498 §2).** `SplitCancellationToken` strips only
    a trailing one, so the app **booted** and that operation alone was unreachable, `400`ing
    unsatisfiably on every call.
  - **An `IResult` return type (#498 §3).** The app booted and the route answered `200` with
    `{"Value":…,"StatusCode":200}` while polluting the EDM.

  The three signature checks live in one new `OperationSignatureValidation` with six call sites — the
  four `Bind*` methods plus `AddFunction`/`AddAction` — called from `Bind*` rather than from
  `BoundOperationDefinition.From` so the throw is not wrapped in *"failed to build EDM for profile"*.
  For **unbound** operations they fire at `AddOhData(...)` rather than `MapOhData()`, following #468's
  precedent.

  > **⚠ BREAKING CHANGE — configurations that previously started no longer do.** The collision checks
  > and #486 break only configurations that were already 100% broken: every collision shape raises
  > `AmbiguousMatchException` at request time, so nothing could have relied on whichever route won
  > registration order (measured — neither endpoint runs), and both #486 shapes `500`ed on every
  > request. The two to actually weigh are **#498 §2 and §3**, where the host *booted* and only the one
  > operation was broken — §3 in particular can stop a running, if wrong, deployment. A `Warning` was
  > considered for #416 and #498 §3 and rejected: warning about a route that can never be served just
  > delays the same failure past deployment.

  > **⚠ BREAKING CHANGE — two `$metadata` changes (#498 §4, §5).** A `byte[]` return type is now
  > declared `Edm.Binary` instead of `Collection(Edm.Byte)` — excluded from collection inference in
  > **both** copies of `GetCollectionElementType`, each now carrying a lockstep note pointing at the
  > other — and an optional parameter's `DefaultValue` is formatted with `InvariantCulture`, so on a
  > non-invariant server culture it goes e.g. `1,5` → `1.5`. Clients regenerated from `$metadata` will
  > differ. A `Dictionary<K,V>` return is deliberately **not** changed and is still
  > `Collection(KeyValuePair<K,V>)` while the wire serves a JSON object: there is no CSDL shape for a
  > map short of an open complex type, so it is a design decision rather than a special case, and needs
  > its own issue.

- **A named `Invoke(...)` authorization rule is matched case-insensitively, and an unresolvable one is
  refused at startup (#525).** `ResolveOperationRule` compared `OperationAuthRule.BoundOperationName`
  with `StringComparison.Ordinal` while every route template and operation segment it governs is matched
  **case-insensitively**. So `ConfigureAuthorization(a => a.Invoke("stamp", …))` against an operation
  declared `Stamp` matched nothing: the rule was discarded in silence and the route fell back to the
  generic `Invoke` rule — or, where there was none, to **no requirement at all**. Measured pre-fix,
  anonymous invocation of a `RequireRole("Admin")`-guarded action answered `200`; with a generic rule
  present, a Reader-only caller was admitted and an Admin-only caller refused — **both directions
  wrong**.

  Both halves are implemented, because the comparer alone closes miscasing while a *misspelled* rule
  still evaporates. `MapEntitySet` now throws for any `Invoke(name, …)` that does not resolve to a
  declared bound operation, naming the rule and listing what the profile does declare. The startup check
  uses **the same comparer as the resolution it guards** — a stricter check there would reject exactly
  the miscased rules the comparer fix just made work, which is this very bug one layer up wearing an
  exception — and it is placed **before** #486's `GetById` guard, which also resolves rules by name, so
  a typo reports as a typo rather than as a missing handler.

  > **⚠ BREAKING CHANGE, in both directions.** A miscased named rule now really attaches its
  > requirement, so a route that previously admitted a caller may now answer `401`/`403`. That is the
  > intended direction — it was silently unprotected — but it is a live behaviour change on the request
  > path. And a profile whose `ConfigureAuthorization` names a bound operation it does not declare
  > **no longer starts**, where the rule was previously discarded in silence: a misspelling that used to
  > cost nothing visible now stops the host booting. Consequently a miscased `.RequireResource()` rule
  > on a profile with no `GetById` now trips #486's startup guard, which it previously slipped past by
  > not matching at all.
  >
  > Pre-existing and unchanged: named rules cannot distinguish binding level, so a collection-level
  > `Stamp` and an entity-level `stamp` can coexist, and one `Invoke("stamp", …)` rule now governs both.

- **A capturing `Convert()` converter is refused where it is declared (#488).** `DeltaMapping.Convert`'s
  converter is hoisted **verbatim** into the compiled plan, which is a process-lifetime singleton, from
  a profile `DeltaFactory.Build` resolves in a scope it disposes on the next line — and delta profiles
  are `AddScoped`, the same lifetime whose scoped injection entity profiles advertise, so `_db` in a
  converter body is a natural thing to write. Measured: a converter calling an injected scoped
  `IDisposable` passed startup and threw `ObjectDisposedException` on **every** `Create`; a
  non-disposable dependency was worse, silently reusing the startup instance with no signal at all.
  Unlike #483's caches there is **no seam** — the converter is an opaque delegate, the plan is a
  singleton, and `Create` has no scope — so refusing the shape is the only guarantee available.

  Also closed here: a **get-only collection** model property was silently dropped instead of mapped
  (measured — `int[] [1,2,3]` in, `List<int> []` out), and a get-only collection *entity* target was
  rejected as *"not writable"* although `Delta<T>` can write one. The obvious fix — adopt `Delta<T>`'s
  tracked set as the writable set — was **measured wrong** on .NET 10.0.11: `Delta<T>` tracks a
  setter-less `byte[]` and then throws `SerializationException: … does not have a Clear method` when the
  write is applied, so *tracked ⇒ writable* would have converted a startup rejection into a guaranteed
  per-request `500`, which is #479/#480's defect class one layer down. `CanApplySetterlessWrite`
  therefore **performs** the write on a throwaway delta seeded from a fresh instance's own value; a null
  seed or any throw is fail-closed, and the setter-less-array case stays rejected at startup.

  > **⚠ BREAKING CHANGE — a converter that compiled and ran before now throws at its `Convert()` call
  > site**, i.e. when the delta profile is constructed, which `MapOhData()` forces at startup. The check
  > is deliberately **sound rather than minimal**: it asks *"does this delegate carry a receiver, or a
  > display class holding something?"*, so two shapes developers will not expect are refused. An
  > **instance method group** — `_dep.Convert` — binds the receiver itself as the delegate's target even
  > when that receiver declares no fields at all, which is why the compiler-generated distinction is
  > consulted rather than a field count (measured: a field-less scoped service used as a method group
  > reports zero fields). And a **captured local** is a capture whether or not the value is immutable.
  > The remedy is one keyword: declare it `static v => …`, or point it at a static method. A
  > non-capturing lambda passes with or without `static`, and a static method group has a null target
  > and passes too; delta mapping is dependency-free by design.
  >
  > **⚠ Two smaller breaks in the same surface.** A second `Rename()` or `Convert()` for one source
  > property now throws at the call site instead of silently replacing the first. And a get-only
  > collection model property is now **in scope**: it must be mapped, renamed, converted or `Ignore()`d,
  > and where it maps it now actually persists.

- **`AddProfilesFrom*` enforces the cross-registration profile guard (#424).**
  `AddEntitySetProfile<T>()` consulted the `GlobalProfileRegistry` guard; `AddProfileType` — which every
  `AddProfilesFrom*` overload routes through — did not. Scanning one assembly into two named
  registrations therefore silently allowed exactly what the explicit call rejects. There is **one**
  `RegisterProfileType(Type, bool explicitCall)` behind both paths now; `explicitCall` controls only
  whether a same-registration duplicate throws (explicit call) or is a no-op (scanner re-discovery), and
  the cross-registration check itself is identical on both. Deliberately one implementation rather than
  a second parallel check that could drift — this repo has repeatedly been bitten by two things derived
  independently.

  > **⚠ BREAKING CHANGE.** Scanning the same profile type into two registrations now throws
  > `InvalidOperationException` — *"Profile type 'X' has already been registered in a different OhData
  > registration. A profile type cannot be shared across registrations."* — at `AddProfilesFrom*`,
  > instead of silently producing a shared-then-broken registration. An app doing this was already
  > broken; it now refuses to start. `docs/versioning.md` documented the divergence as a *"do not rely
  > on this"* inconsistency, and that note is removed because the inconsistency is gone.

- **⚠ BREAKING CHANGE — `PATCH` now resolves body keys through the binder's own contract, so a body
  key it silently dropped under a naming policy is bound (#536).**
  **Who is affected:** any registration configured with a **non-case-preserving**
  `WithJsonPropertyNamingPolicy` — `SnakeCaseLower`, `SnakeCaseUpper`, `KebabCaseLower`,
  `KebabCaseUpper`. A default host and a camelCase host see **no change**: camelCase differs from
  the CLR name only by case, and the table's comparer is `OrdinalIgnoreCase`.
  **Direction:** `PATCH` binds body keys it previously discarded, so a request that answered `200`
  and changed nothing now changes the properties it named. The entity **key** is among them, so
  #454's key-immutability guard now *sees* a key spelled in the policy's casing and answers `400`
  (`target: key`) on a mismatch where it used to be silently dropped.
  **Remedy:** none for the intended case — this is the fix. Review any client or test that was
  (knowingly or not) depending on a `PATCH` no-op, and any that sends the key in the body with a
  value differing from the URL.

  This is #511 manifestation (2) surviving on one
  route. `PATCH`'s body-name table was keyed by the **EDM** name — `[JsonPropertyName]` ?? CLR name,
  deliberately policy-free because `$metadata` advertises the CLR identifier whatever casing
  payloads use (OData JSON Format) — plus the CLR name, under `OrdinalIgnoreCase`, while the **value** it
  binds is deserialized with the registration's serializer options. Under a non-case-preserving
  `PropertyNamingPolicy` the two disagreed. Measured with
  `WithJsonPropertyNamingPolicy(JsonNamingPolicy.SnakeCaseLower)` and a `FirstName` property:

  ```
  PATCH /odata/Customers(1)   {"first_name":"Ada"}   ->  200, delta empty, nothing changed
  ```

  camelCase differs from the CLR name only by case, so the comparer hid this for the only policy
  anyone had configured; `SnakeCaseLower` and `KebabCaseLower` did not — which is exactly why #511
  measured those two. Anyone who configured a snake-case or kebab-case policy had a `PATCH` route
  that accepted requests and discarded the changes. Note the direction: it is fail-**closed** on the
  write, so it was data loss under a `200` rather than an unauthorized mutation. Still a silent
  wrong answer.

  Fixed structurally rather than by adding a `PropertyNamingPolicy?.ConvertName(...)` key, which
  would have closed one policy and not the class — "two things that must agree, derived
  independently", #454's shape. The table's primary key is read off the contract the binder
  resolves: `JsonTypeInfo.Properties[].Name` is by construction the string System.Text.Json matches
  a body key against, whatever produced it (a policy, a `[JsonPropertyName]`, a resolver modifier,
  a source-generated contract), so no second derivation is left to drift. It is resolved on a probe
  **copy** of the serializer options (resolving a contract calls `MakeReadOnly()`, and startup must
  stay free to keep configuring the real instance), and a `JsonPropertyInfo` is paired back to its
  `PropertyInfo` with `HasSameMetadataDefinitionAs` rather than `==` — `PropertyInfo` equality also
  compares `ReflectedType`, and the two reflection walks disagree about it for an **inherited**
  member (#462), which would show up here as an inherited property silently keeping the defect.

  **What changes.** On a default host, nothing: the EDM and CLR names stay as non-overwriting
  aliases (`FindClrPropertyByEdmName` is what the rest of the framework resolves through, so
  dropping them would trade a per-host divergence for a per-verb one), and every alias collapses
  onto the contract key under the comparer. On a host with a non-case-preserving policy, `PATCH`
  binds body keys it previously ignored — including the entity **key**, so #454's key-immutability
  guard now sees an occurrence spelled in the policy's casing and answers `400` (target `key`) on a
  mismatch where it used to be silently dropped. An `Ignore()`d property is removed from the
  contract, so it gains no contract key and cannot become newly bindable.

- **Two `ConfigureAuthorization` `Invoke(name, …)` rules targeting one bound operation are now
  refused at startup (#546).** #525 made named rules resolve `OrdinalIgnoreCase`, which is correct —
  everything the rule governs is matched case-insensitively. But `ResolveOperationRule` keeps
  last-write-wins, so two rules differing only in case now collapse onto each other and **the order
  they were declared in decides whether the operation is protected.** Measured, both configurations
  starting cleanly under #525's new validation:

  ```csharp
  .Invoke("Stamp", i => i.RequireRole("admin")).Invoke("stamp", i => i.AllowAnonymous())
  // anonymous GET …/Stamp -> 200

  .Invoke("stamp", i => i.AllowAnonymous()).Invoke("Stamp", i => i.RequireRole("admin"))
  // anonymous GET …/Stamp -> 401
  ```

  The first order was deterministically **protected** under the pre-#525 `Ordinal` comparer and is
  **open** on 1.7.0 — a fail-open produced by a fail-open fix, silent in both directions. #525's own
  check could not see it: it asks only *"does this name resolve to a declared operation?"*, and both
  members of a colliding pair do.

  > **⚠ BREAKING CHANGE, in the security direction.** `MapOhData()` now throws
  > `InvalidOperationException` when two named `Invoke` rules on one profile resolve to the same
  > bound operation, naming both spellings and the operation. **This includes two rules spelled
  > identically** — the mechanism (the earlier rule silently discarded) and the consequence are the
  > same, and case only changes how easy the pair is to spot by eye. An app in this state today is
  > running under an authorization rule it did not choose; there is no configuration in which two
  > rules for one operation are meaningful, so it is refused rather than resolved by precedence.
  > Remedy: keep exactly one `Invoke` rule per operation.

  Matched with the **same comparer `ResolveOperationRule` uses**, and grouped by the declared
  operation each rule resolves to — which is literally the question that resolution answers per
  route. A second, independently derived comparison would reject a different set of configurations
  than the one that actually collapses at runtime, which is #525's own reasoning and still applies.
  **Generic `Invoke(…)` rules are deliberately unaffected**: `generic = rule` is last-write-wins by
  design there, as it is for every category selector, and `All(…)` then `Invoke(…)` is a documented
  refinement idiom.

- **⚠ BREAKING CHANGE — an unrecognized or unimplemented `$`-prefixed system query option is now
  refused with `501 Not Implemented` on every read route, instead of being parsed and discarded
  under a `200` (#359, #380, #353).** OData Part 1 §11.2.5: *"If a data service does not support a
  system query option, it MUST fail any request that contains the unsupported option."* It is
  Minimal-conformance item 7 (§13.1.1), and the server did not honour it anywhere except through
  three closed name lists that between them left most of the read surface unexamined.

  There are **two** distinct breaking changes in this entry and each has its own callout below:
  the refusal itself (a `200` becomes an error), and the status of every refusal (`400` becomes
  `501`, which moves the four names refused since 1.0.0). The two `/$count` routes keep their
  1.0.0-through-1.6.0 behaviour for the options §11.2.9 says a count MUST NOT be affected by —
  accepted and ignored — which is a following of that clause rather than a change; see the
  `/$count` note below.

  **Measured on `develop` at `3781681`** — three faces, one mechanism:

  ```
  #359  the three collection GETs — rejection was a four-name allowlist
        GET /Set?$unknown=1&$top=2       -> 200
        GET /Set?$slect=Name             -> 200  (unprojected, byte-identical to no option)
        GET /Set?$fliter=Year eq 1972    -> 200  (unfiltered)
        GET /Set?$expandx=Cast           -> 200
        GET /Set?$levels=2               -> 200  ($levels is real, but never valid at top level)
        …and echoed verbatim into the link the server itself generated:
        @odata.nextLink = …/Set?%24unknown=evil+payload&%24slect=x&%24skiptoken=MgAAAA%3d%3d
        (only $apply / $compute / $index / $deltatoken were refused, and only those four)

  #380  GET /Set({key}) — implements $select/$expand, rejected NOTHING
        GET /Set(1)?$filter=Title eq 'nope' -> 200, the entity, filter ignored
        GET /Set(1)?$orderby=… $count=true $top=1 $apply=… -> 200, all ignored

  #353  GET /Set/$count — silently ignored $search, returning the UNFILTERED total
        GET /Set?$search=alpha&$count=true  -> 200  "@odata.count":1
        GET /Set/$count?$search=alpha       -> 200  body: 5     <-- expected 1
        GET /Set/$count?$apply=x            -> 200  body: 5
  ```

  The navigation `/$count` route, which no issue named, rejected nothing either.

  **The rule is the `$` sigil, applied against a per-route implemented set.** Part 2 §5.2 reserves
  the `$` prefix for system query options, so a key that does not start with `$` is a *custom* query
  option and is passed through untouched — as is a `@`-prefixed parameter alias (§5.3) and the
  framework's own non-`$` `ohdata-skiptoken` continuation offset. A `$`-prefixed key the route does
  not implement is refused whether or not any OData version has ever defined it, which is the
  fail-closed direction: a system option a future spec adds cannot quietly start being dropped on
  the floor. This generalises the `$`-sigil loop the #313 `$expand` continuation route has used
  since 1.6.0; that route now shares the same matcher rather than carrying a second copy of it.

  **Who is affected, and in which direction.** A client that sends a `$`-prefixed query option a
  route does not implement — a typo (`$slect`), an option meant for a different route (`$filter` on
  `GET /Set({key})`), a real option this build does not implement (`$apply`, `$index`), or anything
  unrecognized — receives `400` `UnsupportedQueryOption` where it previously received `200`. That is
  the whole point: the `200` was a wrong answer, because the client's option had been discarded and
  it had no way to know. No successful response changes by a byte. Remedy: stop sending the
  option, or send it to a route that implements it.

  **One narrow exception to *"nothing that was honoured before is refused now"*, and it is a real
  break.** A **Priority-1** profile (`GetODataQueryable`) receives the whole `ODataQueryOptions`
  and may read anything it likes off `options.Request.Query` — a shape #465's note explicitly
  endorses for `$search`. A profile that had invented a `$`-prefixed option of its own and read it
  there now never sees the request: the framework refuses it before the handler runs. Remedy:
  spell the option **without** the `$`, which is what Part 2 §5.2 requires of a custom query
  option in the first place (`$myTenant` → `myTenant`); non-`$` keys are passed through untouched
  and always have been.

  **Three route families stay outside the rule**, deliberately, and the table below is a list of
  what *is* gated rather than of the whole URL surface: the structural-property routes
  (`GET`\|`PUT`\|`PATCH`\|`DELETE /{Set}({key})/{Prop}` and `/$value`), the service document
  (`GET /{prefix}`) and `GET /{prefix}/$metadata`. Measured: `GET /odata?$unknown=1` → `200`.
  None of them generates a link, so none carries #359's echo; closing them is a separate change.

  > **⚠ BREAKING CHANGE — every one of these refusals is `501 Not Implemented`, and that moves
  > `$apply`/`$compute`/`$index`/`$deltatoken`, which have answered `400` since 1.0.0.**
  >
  > An earlier revision of this change answered `400` throughout, reasoning from §11.2.5's status
  > advice being a *SHOULD*. That reading does not survive two other clauses, quoted verbatim from
  > the OASIS text:
  >
  > - **§9.3.1** — *"If the client requests functionality not implemented by the OData Service, the
  >   service **MUST** respond with 501 Not Implemented and the response body SHOULD describe the
  >   functionality not implemented."*
  > - **§13.1.1 item 7**, inside the Minimal Conformance **MUST** list — *"MUST successfully parse
  >   the request according to [OData-ABNF] for any supported system query string options and either
  >   follow the specification or return **501 Not Implemented** (section 9.3.1) for any unsupported
  >   functionality"*.
  >
  > OhData claims Minimal conformance, so the `501` is not optional. **Who is affected and in which
  > direction:** a client sending `$apply`, `$compute`, `$index` or `$deltatoken` to a collection
  > `GET` receives `501` where every release since 1.0.0 gave `400`. Every other refusal in this
  > entry is new, so for those the change is `200` → `501` rather than `400` → `501`.
  > **Remedy:** treat `501` as you treated `400` — the option is not going to work; stop sending it.
  > Code that branches on `response.StatusCode == 400` for this condition must add `501`.
  >
  > **The error code and the message bytes do not move.** A refusal is still
  > `{"error":{"code":"UnsupportedQueryOption","message":"The query option '$x' is not supported."}}`,
  > byte for byte, and the navigation-collection route keeps its own longer wording. §9.3.1's *"body
  > SHOULD describe the functionality not implemented"* is already satisfied by that text, so only
  > the status line moves — a client matching on the envelope keeps working, and
  > `ErrorEnvelopeFidelityTests` pins the bytes across the change. One condition still produces one
  > envelope, which is the standing rule recorded for #357/#543 in this same release; what changed is
  > which envelope, not how many.

  **`400` is not gone — it now means something specific.** In five words:

  > **`501` is "can't". `400` is "won't".**

  The test is mechanical: **could any setting on the profile make this same request succeed on this
  same route?** Yes → `400`. No → `501`.

  | Condition | Status | Why |
  |---|---|---|
  | Unrecognized `$`-name (`$unknown`, `$slect`, top-level `$levels`) | `501` | implemented nowhere |
  | `$apply` `$compute` `$index` `$deltatoken` | `501` | implemented nowhere (**was `400`**) |
  | An option the addressed **route** does not implement (`$filter` on `GET /Set({key})`, `$search` on a `/$count`, `$select` on a single-valued nav) | `501` | no configuration adds it to that route |
  | `$filter`/`$orderby` on the `GetAll` path, and `$filter` on the `GetAll`-backed `/$count` | `501` | flag-**independent**: that path has no `IQueryable`, so `FilterEnabled = true` changes nothing and the remedy is a different handler |
  | A capability flag left `false` (`FilterEnabled`/`OrderByEnabled`/`SelectEnabled`/`ExpandEnabled`/`CountEnabled`) | `400` | implemented; this resource declines to offer it |
  | A property allowlist rejection (`FilterProperties` etc.) | `400` | same |
  | `$search` with no `Search` handler, on a route that **has** a `$search` leg | `400` | implemented; unconfigured on this profile |
  | A malformed or empty option **value** (`$top=abc`, `$skiptoken=`, `$filter=`) on a route that implements the option (#402) | `400` | a bad request about supported functionality |
  | A value outside a configured bound (`MaxTop`, `MaxExpandTop`) | `400` `InvalidQueryOption` | same |

  `$search` is the clearest illustration that the same option can land on either side: no `Search`
  handler is a `400` on the collection `GET`s, which really do invoke one when configured, and a
  `501` on `/$count` and `GET /Set({key})`, which have no `$search` leg at all.

  The `GetAll` `$filter`/`$orderby` row is "can't", and is recorded at the code. It *reads* like the
  `400` side because its message names a configuration change (*"Configure GetQueryable…"*), but the
  refusal is flag-independent — `CheckCollectionQueryOptionCapabilities` is called with
  `checkFilterOrderBy: false` on that path precisely because the flag changes nothing there — and
  the remedy supplies a **different handler**, i.e. has the request served by a different route
  implementation rather than switching this one on. That is §9.3.1's *"functionality not
  implemented"*, and the existing message is already what its `SHOULD` asks the body to do: it
  describes the unimplemented functionality. Its response body is byte-identical to before; only the
  status moved.

  > **The same option can be `501` on one entity set and `400` on another**, decided by which read
  > handler the profile supplies. `$filter` on a `GetAll`-backed set is `501` — the framework
  > *can't* filter it, under any configuration. `$filter` on a `GetQueryable`-backed set with
  > `FilterEnabled = false` is `400` — it *can*, and the adopter chose not to expose it. That is
  > correct rather than an inconsistency: conformance is per-**resource**, and the two answers tell
  > a client genuinely different things — *"no configuration of this endpoint will ever do that"*
  > versus *"this endpoint could, and is not offering it"*.

  **Mixed-case spellings of real options are still honoured, and that is alignment rather than
  leniency.** `Microsoft.AspNetCore.OData` lowercases a query-option name before matching it
  whenever the URI resolver enables case-insensitivity, which is the default
  (`ODataQueryOptions.cs:283` for the comment, `:284-287` for the branch it guards, `:289-290`
  for the lowercasing), so `$Select` and
  `$TOP` have always been applied and continue to be. #359 reported the pair — `$Select` applied,
  `$slect` ignored, neither rejected — as one inconsistency; it is resolved by rejecting `$slect`,
  never by starting to reject `$Select`. The matching here is `OrdinalIgnoreCase` for the same
  reason.

  **That is a real behaviour change on one route.** The #313 `$expand` continuation's own inline
  sigil loop compared with `StringComparison.Ordinal`, so `GET /{Set}({key})/{Nav}?$SKIP=3` and a
  `$FORMAT=json` beside a `$skip` were `400` there — and are now honoured, like every other
  route. It is the only place this change makes the server *more* accepting, and
  `MixedCaseSkipAndFormat_AreHonoured_NotRejected` pins it (verified to fail on the pre-change
  tree, `Expected: OK / Actual: BadRequest`, for all three spellings). This *is* a deliberate divergence from Microsoft in the other direction: its own
  `BuildQueryOptions` carries `default: // we don't throw if we can't recognize the query`
  (`ODataQueryOptions.cs:1061`), i.e. it silently ignores. §11.2.5's `MUST` wins over matching
  that. (Note that MS's own `IsSystemQueryOption` (`:172-198`) recognizes thirteen names and
  `$index` is **not** among them — it is refused here by the sigil, like `$unknown`.)

  Per-route implemented sets, and what each now refuses:

  | Route | Implemented (accepted) | Newly refused |
  |---|---|---|
  | `GET /{Set}` — `GetQueryable`, Priority-1 | `$filter` `$orderby` `$top` `$skip` `$select` `$expand` `$count` `$search` `$skiptoken` `$format` | every other `$`-name (the four already refused keep their exact envelope) |
  | `GET /{Set}` — `GetAll` | the same, **minus `$skiptoken`** | as above, plus `$skiptoken` — #201 continues this path with `$skip` and nothing on it ever read a `$skiptoken` |
  | `GET /{Set}/$count` | `$filter` `$top` `$skip` `$orderby` `$expand` `$select` `$format` | `$search` (#353), `$count`, `$apply`, `$compute`, and every unrecognized `$`-name. §11.2.9 requires the four it names — `$top`/`$skip`/`$orderby`/`$expand` — to be accepted and **ignored**; see the note below |
  | `GET /{Set}({key})` | `$select` `$expand` `$format` | everything else (#380) |
  | `GET /{Set}({key})/{Nav}` — **collection**-valued (`HasMany`) | `$select` `$orderby` `$skip` `$top` `$count` `$format` | every unrecognized `$`-name (the seven already refused are unchanged) |
  | `GET /{Set}({key})/{Nav}` — **single**-valued (`HasOptional`/`HasRequired`) | `$format` only | `$select` `$orderby` `$top` `$skip` `$count`, plus every unrecognized `$`-name |
  | `GET /{Set}({key})/{Nav}/$count` | `$top` `$skip` `$orderby` `$expand` `$select` `$format` | `$filter` **and** `$search` — it applies neither — plus `$count` and every unrecognized `$`-name |
  | `GET /{Set}({key})/{Nav}?$skip=N` (#313) | `$skip` `$format` | nothing newly refused — but `$SKIP`/`$FORMAT` are now **accepted**; see below |
  | `GET`\|`POST /{Set}/{Op}`, `GET`\|`POST /{Set}({key})/{Op}` (bound operations) | `$top` `$skip` `$format` | every other `$`-name |
  | `GET`\|`POST /{Op}` (unbound operations) | `$format` only | every other `$`-name |

  **The two navigation rows are one route template with two handlers**, and that is what made
  the single-valued one a defect of its own. `GET /{Set}({key})/{Nav}` is mapped once; the
  branch that runs is decided by `HasMany` versus `HasOptional`/`HasRequired`. Every option the
  route applies is applied inside the collection branch — `$select` in `BuildNavEnvelope`,
  `$orderby`/`$skip`/`$top`/`$count` in the branch body — while the single-valued branch
  serializes the related entity through `ODataEntityNode` and reads nothing off the query
  string. Gating both with the collection set therefore left `$select`/`$orderby`/`$top`/
  `$count` accepted and **discarded** under a `200` there (measured), which is #380's own defect
  statement — *"known, implemented-elsewhere options being silently dropped on a route that does
  not implement them"* — and put `GET /Set(1)?$orderby=X` at `400` beside
  `GET /Set(1)/Owner?$orderby=X` at `200`. The single-valued branch is refused rather than
  taught `$select`: this change is about refusing what is not implemented, and `$select` there
  needs the projection, the allowlist validation and the `@odata.context` projection suffix that
  `ODataEntityResult` carries and `ODataEntityNode` does not. Read a projection of a single
  related entity from its own entity set.

  **`$top`/`$skip` are listed unconditionally on the bound-operation routes**, and not derived
  from the declared return type the way `AddBoundOperationPagingMetadata`'s `TopSkipSupported`
  is. The ceiling is applied in the *runtime* collection branch (#357's rule that a handler
  declared `Task<object>` must not be a way around it), so such a route really can emit a
  `$skip=N` continuation — deriving the set from the declared type would make the server refuse
  a link it had just issued. Where the result is not a collection they are accepted no-ops, which is
  the same answer the two `/$count` routes give them, reached by a different road: there §11.2.9
  requires them to be present and ignored, whereas here they are real on the collection shape and the
  route cannot know at startup which shape a handler declared `Task<object>` will return, so refusing
  them would refuse a link the server itself had just issued. An operation's own parameters are
  non-`$` keys (a query string for a function, a JSON body for an action) and are never examined.

  `$format` is in **every** set and must stay there: it is not a data option. §11.2.10 content
  negotiation is implemented once, on the group filter wrapping the whole OData surface, so it never
  reaches these handlers and cannot change a row; an unsupported `$format` **value** is still
  rejected there, unchanged.

  > **`/$count` follows §11.2.9, which decides this route's implemented set outright.** The four
  > options that clause names are accepted and **ignored** — unchanged behaviour from 1.0.0 through
  > 1.6.0, and not a breaking change. Verbatim:
  >
  > > "On success, the response body MUST contain the count of items matching the request after
  > > applying any `$filter` or `$search` system query options, formatted as a simple scalar integer
  > > value with media type `text/plain`. **The returned count MUST NOT be affected by `$top`,
  > > `$skip`, `$orderby`, or `$expand`.**"
  > >
  > > "Content negotiation using the Accept request header or the `$format` system query option is
  > > not allowed with the path segment `/$count`."
  >
  > That partitions the system query options into exactly two classes, and each gets the answer the
  > clause specifies. Under Minimal item 7's disjunction — *"either follow the specification or return
  > 501 Not Implemented … for any unsupported functionality"* — ignoring the second class **is**
  > following the specification, so a `501` there claimed non-implementation of something this route
  > had implemented correctly since 1.0.0.
  >
  > | Class | Options | Answer |
  > |---|---|---|
  > | **Affects the count** | `$filter`, `$search` | Applied where the route can; **refused** where it cannot |
  > | **MUST NOT affect the count** | `$top`, `$skip`, `$orderby`, `$expand` (and `$select`) | Accepted and **ignored** |
  > | **Outside the clause** | `$apply`, `$compute`, `$index`, `$deltatoken`, `$skiptoken`, `$count`, any unrecognized `$`-name | **Refused** (`501`) |
  >
  > **`$search` and `$apply` stay refused, and #353's headline is unchanged.** §11.2.9 requires the
  > count to be taken *after applying any `$filter` or `$search`*, so a route with no `$search` leg
  > that ignored one would answer a **wrong number** under a `200`. The same argument refuses
  > `$filter` on the **navigation** `/$count`, whose handler invokes the navigation delegate and
  > counts what comes back — it can apply neither, so it refuses both. `$apply`/`$compute` fall
  > outside the clause and are unimplemented anywhere in this build.
  >
  > **`$select` is accepted and ignored, deliberately.** §11.2.9's MUST-NOT sentence does not name
  > it, but the clause's positive half is exhaustive about what *does* move the number: the count is
  > of "items **matching the request** after applying any `$filter` or `$search`". `$select` changes
  > an item's **shape**, never its membership, and the response is a bare `text/plain` scalar with no
  > representation left to project out of. Refusing it alone among the five options a grid URL
  > carries would be an unprincipled split.
  >
  > **`$format` is accepted and ignored, which is the opposite error and is stated rather than left
  > implicit.** §11.2.9 says content negotiation "is not allowed" on this segment, so the entry
  > cannot mean "negotiated here" as it does in every other route's set — it means "not refused".
  > Refusing it was rejected on three grounds: the clause constrains the *client* and prescribes no
  > server response; `$format` **is** implemented service-wide (§11.2.10, on the group filter), so a
  > `501` would be a false statement, while the `400` arm of the taxonomy needs a capability flag
  > this condition has none of; and the group filter already answers `400 UnsupportedFormat` for a
  > non-JSON `$format` *before* the route runs, so refusing here would give one disallowed option two
  > envelopes (`$format=xml` → `400`, `$format=json` → `501`). The response is `text/plain` whatever
  > `$format` says. `Accept: application/xml` still answers `406`, unchanged: §11.2.9 forbids the
  > *client* to negotiate, it does not licence the server to ship a media type the client said it
  > will not take, and RFC 9110 §12.5.1 makes `406` the right answer to that.
  >
  > **This route agrees with `Microsoft.AspNetCore.OData`, and that agreement is load-bearing.**
  > Verified against its source at `a05e1ad0` (9.5.0-7): `ODataQueryOptions`' constructor synthesises
  > `Count = "true"` for any request whose path ends in `/$count`
  > (`Query/ODataQueryOptions.cs:1072-1084`), so `ApplyTo`'s `Request.IsCountRequest()` early return
  > (`:425-429`) always fires — before the `$orderby`/`$skip`/`$top` block and before `SelectExpand`
  > — and MS silently ignores all five. **`Microsoft.OData.Client` depends on that**: it translates
  > `LongCount()` by appending `/$count` to the query string it has *already* built and stripping
  > nothing, so an ordinary paging shape sends the option along. Measured against
  > `Microsoft.OData.Client` 8.4.4:
  >
  > ```
  > q.LongCount()                       -> /Widgets/$count                  200
  > q.Where(...).LongCount()            -> /Widgets/$count?$filter=Id gt 1  200
  > q.OrderBy(w => w.Name).LongCount()  -> /Widgets/$count?$orderby=Name    200
  > q.Take(2).LongCount()               -> /Widgets/$count?$top=2           200
  > q.Skip(1).LongCount()               -> /Widgets/$count?$skip=1          200
  > ```
  >
  > Refusing those four therefore broke standard pagination for the industry-standard OData client,
  > not merely a hand-built grid URL. `MsODataClientIntegrationTests`' `Count_*` cases pin every shape
  > above through the real client, so a future narrowing of this route fails there first.

  **`@odata.nextLink` needed no separate fix on the routes that were already gated, and needed
  exactly this one on the routes that were not.** Once an unrecognized option is refused, no link
  is built for that request at all — `Collection_UnrecognizedOption_IsNeverEchoedIntoANextLink`
  proves the response carries neither a `nextLink` nor a `skiptoken`, against a control on the
  same fixture that does emit one. But `@odata.nextLink` is generated by
  `BuildNextPageLinkWithSkip`, which copies the **whole** incoming query string, and #357/#543
  gave the *bound-operation* collection route a `nextLink` in this very release while it had no
  option gate at all. Measured, with `MaxTop = 2` over three rows:

  ```
  GET /odata/SqOps/TopRated?$unknown=evil -> 200
     "@odata.nextLink": "…/SqOps/TopRated?%24unknown=evil&%24skip=2"
  GET /odata/SqOps/TopRated?$apply=groupby((Name)) -> 200 + nextLink
     (400 on the sibling collection route)
  ```

  That is byte for byte the wire shape #359 reports. All six operation routes — the four bound
  ones and the two unbound — are gated for it, and the gate runs **before** parameter binding and
  before the handler delegate, so a refused **action** invocation provably mutates nothing
  (`BoundAction_OptionItDoesNotImplement_Returns501_AndNeverRuns` asserts the delegate was not
  reached, not just the status code).

  One further wire change falls out, on two routes: `GET /{Set}({key})?$select=…&$skiptoken=` and
  `GET /{Set}/$count?$skiptoken=` answered `400 InvalidQueryOption` — *"One or more system query
  options in the request URL could not be parsed."*, #402's **generic** message, because an
  empty `$skiptoken` throws `ArgumentException` out of `SkipTokenQueryOption`'s own constructor
  rather than the `ODataException` whose message #402 passes through — and now answer
  `501 UnsupportedQueryOption`. Neither route implements `$skiptoken`, so it is refused by name
  before any option object is built — a better answer, since the old one said only that
  *something* in the URL had failed to parse, implying some non-empty value would have worked.
  **#402's guarantee is unaffected and is now pinned as the taxonomy's sharpest statement**:
  `QueryOptionConstructionFaultTests.EmptySkipToken_ReturnsODataErrorEnvelope` sends the same empty
  `$skiptoken` to five routes and asserts `400 InvalidQueryOption` on the three that implement the
  option (the empty value is malformed input for supported functionality) beside `501
  UnsupportedQueryOption` on the two that do not. No client-reachable `500` from option
  construction, on any of the five.

  `UnrecognizedSystemQueryOptionTests` covers every gated route in both directions — the
  recognized-but-not-implemented-here case, the unrecognized case, the non-`$` and `@`-alias
  passthroughs, the mixed-case `$Select`/`$TOP` controls, `$format` on every route, the `nextLink`
  case, and controls proving `$filter` still applies on the entity-set `/$count` and that a bare
  `/$count` still returns the total. The status taxonomy is pinned across the suite: nine other test
  classes moved a `BadRequest` expectation to `NotImplemented` for a sigil refusal, and none of them
  moved an expectation for a capability flag, an allowlist or a malformed value.

- **⚠ BREAKING CHANGE — a complex type's own entity-typed navigation is now suppressed like
  any other (#507).**
  **Who is affected:** any model with a **complex** type that has an entity-typed member —
  `ODataConventionModelBuilder` makes that member a navigation *on the complex type*, and the
  suppression seed never looked at complex types.
  **Direction:** the response body of a **plain `GET` with no query string** changes. Such a
  navigation used to be serialized inline with no `$expand` naming it; it is now omitted, as
  JSON Format §4.5.1 / §11.2.4.2 require. A client reading that nested object will find it
  gone. In the other direction, an entity referencing itself through its complex member used
  to `500` on **every** request and now succeeds.
  **Remedy:** the data is no longer reachable from that path at all — `$expand=Meta/Owner` is
  omitted rather than expanded (a pre-existing feature gap that only *looked* like it worked
  because the whole un-suppressed graph leaked). Read the related entity through its own
  entity set.

  `ODataConventionModelBuilder` models an entity-typed member of a **complex** type as a navigation
  *on that complex type*, but the nav-suppression seed walked
  `model.SchemaElements.OfType<IEdmEntityType>()` and read navigations off entity types only — so the
  suppression set computed for every complex CLR type was **empty**. Measured on 1.6.0, both
  consequences on a plain `GET` with no query string: `"Meta":{"Note":"y","Owner":{…}}` — navigation
  data served inline with no `$expand` naming it, which JSON Format §4.5.1 / §11.2.4.2 forbid — and an
  entity referencing itself through its complex member throwing
  `JsonException: A possible object cycle was detected`, i.e. a **500 on every request**. Neither is
  order-dependent and neither needs open types.

  **#491's claim to have covered this case was false**, and is now verified false rather than argued
  about: its measurement covered the entity reached *through* the member (`Owner.Children` really was
  suppressed), which is exactly why the gap looked closed. The universal invariant test could not see
  it either — it quantifies over EDM **entity** types; a complex-type twin now exists and fails pre-fix
  with `["PxMeta.Owner"]`.

  Suppressed, **not served**, for the same reason a derived-declared navigation is: the splice
  iterates the *entity* type's navigations, so such a navigation has no route into an `$expand` clause
  and serving it would mean serving it unconditionally. Deliberate residual: `$expand=Meta/Owner` — a
  complex-type path the OData parser accepts — is now omitted rather than expanded. That is a
  pre-existing feature gap which previously *looked* like it worked because the whole un-suppressed
  graph leaked.

- **⚠ BREAKING CHANGE — `$expand` pushdown no longer disengages model-wide on a renamed
  schema (#508).**
  **Who is affected:** any registration whose EDM type names do not equal the CLR `FullName`
  — reachable through `ODataConventionModelBuilder.Namespace`, or
  `EntityTypeConfiguration.Namespace` under an `AdvancedConfigure` override. **One** renamed
  type disengaged the pushdown for the whole model.
  **Direction:** response bodies change on requests that were already returning `200`.
  `GET /Set?$expand=Children` answered `200` with `"Children": []` on every row and never
  touched the child table; it now returns the children. Query counts and SQL shapes change with
  it — one `LEFT JOIN`ed query where there were none.
  **Remedy:** none needed; the previous answer was wrong. Re-baseline any snapshot test or
  query-count assertion taken against a renamed schema.

  `model.FindDeclaredType(clrType.FullName)` matches on the EDM type's **full name**, which is a
  convention rather than a fact: a schema whose EDM names do not equal the CLR `FullName` — reachable
  through `ODataConventionModelBuilder.Namespace` or `EntityTypeConfiguration.Namespace` under
  `AdvancedConfigure` — makes it return `null` for every type, and every caller takes its "not in the
  EDM" branch. #491 established this and re-keyed the nav-suppression map off `ClrTypeAnnotation`; the
  same call survived at four read-path sites (`ResolveProfilesForClrType`, `IsMemberInitProjectable`,
  `ScalarStructuralClrProps`, `TryGetKeyClrProperty`) plus one residue fallback.

  **Measured** end-to-end with a single renamed type, `GET /NmParents?$expand=Children($expand=Tags)`:
  HTTP `200` with `"Children":[]` on every row and a SQL log whose only statement is
  `SELECT "n"."Id", "n"."Name" FROM "NmParents"` — the child table never touched, nothing logged above
  `Debug`. All five sites now resolve through a shared per-model `CLR type -> IEdmStructuredType` map
  read off the model builder's own `ClrTypeAnnotation`, which involves no name convention and so
  cannot miss. The lookup stays **exact** (no base-chain walk) deliberately: answering with a base
  type's declaration would make the pushdown projection drop a derived type's structural properties
  and would re-broaden the Model B candidate gate that #293 narrowed.

- **⚠ BREAKING CHANGE — a bound operation returning `List<TDerived>` for a declared
  `IEnumerable<TModel>` now gets the OData collection envelope (#497).**
  **Who is affected:** any `BindFunction`/`BindAction` whose runtime result is a collection of a
  type **derived** from the entity set's `TModel` — the ordinary EF Core TPH shape.
  **Direction:** the response body changes shape outright. It used to be a bare JSON array with
  no `@odata.context`, no `value` wrapper, no `@odata.etag`, and the declared navigations served
  **inline**; it is now `{"@odata.context":…,"value":[…]}` with navigations stripped like every
  other collection response. **Any client parsing the old array breaks outright**, and a
  navigation it was reading disappears. In the other direction, a cyclic derived graph used to
  `500` and now succeeds.
  **Remedy:** read `value` instead of the root, and `$expand` the navigation you were relying
  on. The identical handler returning `List<TModel>` was always enveloped, so this makes the two
  agree.

  The ordinary EF Core TPH shape fell out of every branch of the
  bound-op result dispatch, because the collection branch tested the element type with `==` while the
  single-entity branch beside it already accepted a derived instance via `IsAssignableFrom`. Measured:
  `[{"Special":"s","Id":1,"Name":"derived","Parts":[{"Id":9,"Label":"PART-LEAK"}]}]` — a bare array
  with no `@odata.context`, no `value` envelope, the declared navigation `Parts` served **inline**, and
  no `@odata.etag`; a cyclic derived graph made the same request a `500`. The identical handler
  returning `List<TModel>` was correct. The element test is assignability now, and the OpenAPI
  documentation carries the **same** predicate over the declared return type, so the advertised shape
  and the served shape cannot disagree.

### Added

- **`RequestBodyNullabilityValidationEnabled` — a per-entity-set and server-wide opt-out for the new
  EDM nullability gate (#355, renamed by #570).** Declared on `EntitySetProfile<TKey, TModel>` and on
  `EntitySetDefaults` (which a profile inherits it from), defaulting to `true`. Set it `false` for a
  resource whose handler legitimately supplies a value the client is not expected to send. The gate
  itself — and what it rejects after #544/#545 narrowed it to a property the body **named** with an
  explicit `null` — is described under **Breaking** above. The property was called
  `ValidateRequestBodyNullability` earlier in this release cycle and never shipped under that name,
  so there is nothing to migrate; #570 renamed it before release to match every other capability flag
  on those two types (`FilterEnabled`, `PropertyAccessEnabled`, `ExpandPagingEnabled`, …).

- **Startup warning when a navigation's target entity set is protected more strictly than the set
  declaring it (#481, closes #368).** Authorization in OhData is per-profile and does **not** compose
  across a navigation: every navigation-family route, and every `$expand` call site, runs under the
  **declaring** profile's rule and never the target set's. Measured across 19 route shapes on #481 —
  with an admin-gated child set and an anonymous parent declaring a navigation into it, the nav `GET`,
  its `/$count`, all four `$ref` shapes, the navigation-`POST` create route and `$expand` (delegate,
  batch and pushdown) all succeeded anonymously, and `$ref` `POST`/`PUT`/`DELETE` and navigation-`POST`
  **executed the write**.

  **No request-path behaviour changed, deliberately.** This is what `Microsoft.AspNetCore.OData` does
  — verified against its source, which contains no authorization code at all and routes the navigation
  action onto the *parent's* controller — so enforcing the target's rule would be a divergence from
  OData norms rather than a correction toward them; it would also break the ordinary scoped-navigation
  pattern (a customer-scoped `Orders` navigation beside a separately registered, admin-gated `Orders`
  set is correct code), is not well-defined where sibling sets share one EDM type (#458), and collides
  with #293's Model B rule that a sibling's declaration must not retroactively poison a navigation
  another set legitimately serves. The navigation declaration is therefore the opt-in, and
  `MapOhData()` now makes that decision loud at the moment it is introduced.

  Targeting, each point measured rather than reasoned: the target is resolved through the **EDM
  navigation property's own target type** (not `ChildEntitySetName`, which the `batchGetAll` overload
  never sets, and not `NavItemType`, which `HasOptional`/`HasRequired` never set) and then through the
  same candidate **union** the two `$expand` call sites use — never the EDM's navigation-source
  binding, which is *deleted* the moment a second entity set is registered over the child type. It
  fires on the **declared** navigation, not the routed one, because a bare `HasMany(x => x.Children)`
  with no handler and no route still serves the target's rows through `$expand`; an *undeclared*,
  convention-discovered navigation stays silent, because #440/#446 already made it reachable by
  nothing. The comparison is **per operation category** — `Read` always, `Create` only with a `post`
  handler, `Update` only with `$ref` handlers — so a target guarded only on writes does not warn about
  a navigation that exposes only reads. Silent on equal authorization, on a *less* strict target, on
  no authorization anywhere, and when no profile owns the target type. Measured over the repository's
  own seven test projects: **zero** emissions from any pre-existing fixture.

  `docs/authorization.md` gains the matching section, and its claim that navigation/`$ref` routes are
  *"covered uniformly - there's no bypass"* is narrowed — true of the declaring profile's own rule,
  and exactly the assurance #481 disproves about the target's.

- **Startup warning when `Ignore()` loses its EDM half under an `AdvancedConfigure` override (#489).**
  `VisitModelBuilder` returns before the `_configurators` pipeline runs when `AdvancedConfigure` is
  overridden, so `Ignore()`'s EDM removal never executes while its runtime suppression still does. The
  property is omitted from every response body, has no property routes and is never bound from a write
  body — but `$metadata` advertises its name and type, and it stays addressable in
  `$filter`/`$orderby`/`$select` wherever the override re-enabled those capabilities. That makes a
  withheld property a **value oracle**: the value is never served, yet `?$filter=Secret eq …` answers
  truthfully, so it can be probed one predicate at a time. `WarnIgnoredPropertiesStillInEdm` now logs
  one `Warning` per affected property at `MapOhData()`, naming the entity set, the property, the
  consequences and the remedy — re-apply `configuration.EntityType.Ignore(x => x.Secret)` inside the
  override, or drop the override.

  **Documented and warned rather than fixed, deliberately.** Re-imposing `Ignore()` on top of the
  override would defeat the eject hatch outright, and `HasOptional`/`HasRequired`/`HasMany` ride the
  **same** pipeline and stay ejected — singling out `Ignore()` would make pipeline membership depend on
  consequence severity rather than on a rule, which is a worse contract than the one it replaces. The
  warning is gated on **the EDM as built**, not on the presence of an override, so re-applying the
  removal by hand — what the docs prescribe — silences it; and the capability half is deliberately
  *outside* that gate, so a capability added later cannot silently un-warn a profile.

  **A correction to #489's own claim**, recorded because it should not be re-raised: the issue's repro,
  `$filter=Secret eq abc` → `200`, does **not** reproduce on a bare override. Taking the hatch also
  drops OhData's automatic `Filter()`/`OrderBy()`/`Select()` calls, so `$filter` over *any* property
  answers `400`. The value oracle is live only once the override **re-enables** capabilities — which is
  precisely what `docs/architecture.md`'s own example (`config.EntityType.Select().OrderBy().Filter()`)
  tells the developer to write. What holds unconditionally is the `$metadata` disclosure of the
  property's name and type. No request-path behaviour changed.


- **Unbound functions and actions can now carry their own authorization requirement (#487).**
  `AddFunction`/`AddAction` take an optional `authorize` lambda using the same
  `ICategoryAuthorizationBuilder` as `ConfigureAuthorization`:

  ```csharp
  builder.AddAction(ResetAll, a => a.RequireRole("admin"));
  builder.AddFunction(Ping,   a => a.AllowAnonymous());   // deliberately public, and says so
  ```

  This closes an API-shape gap `docs/authorization.md` has named since 1.0. An unbound operation is
  not scoped to an entity set, so no profile's `RequireAuthorization()`/`RequireRoles()`/
  `ConfigureAuthorization(...)` reached it, and the only mitigation was a group-level requirement
  covering the entire surface including `$metadata`. **Measured on the pre-fix tree**, on a
  registration whose only profile declares `RequireAuthorization()`: `GET /odata/{Set}` → `401`,
  `POST /odata/Mutate` → `204` **with the handler executed**, `GET /odata/Peek` → `200` with the body.

  `RequireResource()` is refused at the call site — resource-based authorization evaluates the
  requirement against the entity loaded from a `{key}` segment, and an unbound operation has neither
  a key nor an entity set, so the rule could only ever be a silent no-op. `AllowAnonymous()` on an
  unbound operation states intent and silences the warning below; it deliberately does **not** emit
  `AllowAnonymousAttribute`, so it cannot tunnel the operation out from under a host-applied group
  requirement.

- **A startup `Warning` names every route that is anonymous in a registration that requires
  authorization somewhere else (#487).** Two configurations reach it, both previously silent:

  - an unbound function/action with no requirement of its own, and
  - a `ConfigureAuthorization` profile that leaves an operation **category** rule-less while routes
    exist in it — most consequentially `Invoke`. A profile migrated from `RequireAuthorization()`
    (which covers *all* operations) to `.Read(...).Writes(...)` reads as a refinement and is a
    **widening**: measured pre-fix, that profile answered `401` on its collection `GET` and `204`
    with the handler executed on both `POST /{Set}/{Action}` and `POST /{Set}({key})/{Action}`.

  Each warning names what is anonymous, the configuration that produced it, and two remedies — the
  requirement to add, and the explicit `AllowAnonymous()` that states the opposite intent and stops
  the warning. **Nothing is warned about when the host applies a group-level requirement**, which is
  the mitigation the docs recommend: the diagnostic runs from an `IEndpointConventionBuilder.Finally`
  convention rather than inside `MapOhData()`, because the host applies its requirement to the group
  *after* `MapOhData()` returns and warning earlier would fire on the correct configuration. A
  registration that requires authorization nowhere is a public service, not a service with a hole,
  and is never reported.

  **No request-path behaviour changes.** Every route answers exactly as before; the fix is a
  diagnostic plus the opt-in capability above.

### Fixed

- **The `413` from the `MaxRequestBodyBytes` fast-reject carries `OData-Version` (#496).** The
  `OData-Version: 4.0` header §8.1.5 requires on every response was set by the `$format`/`Accept`
  filter, which is registered **fourth** of five; the #203 body-limit filter's `Content-Length`
  fast-reject is registered third and short-circuits above it, so that `413` shipped with no
  `OData-Version` at all (measured). The header is now written by the outermost group filter, where
  nothing above it can short-circuit. Only reachable on a profile that sets
  `MaxRequestBodyBytes`; every other response already carried it and still does, byte for byte.

- **Two named registrations declaring the same action name no longer share one generated request-body
  schema (#499).** `ActionBodySchemaTypeFactory` memoizes generated OpenAPI body-schema types in a
  process-wide static keyed only by route shape — `"{EntitySet}.{Action}"`,
  `"{EntitySet}.{Action}.Entity"` and `"Unbound.{Name}"` — with no registration identity anywhere. Two
  named registrations declaring the same entity set and action name is **the documented v1/v2
  versioning pattern**, whose flagship example in `docs/versioning.md` has both versions exposing
  `EntitySetName = "Products"`: whichever mapped first won, and the other version documented the wrong
  request body. Measured pre-fix on all three key shapes — entity-bound, collection-bound and unbound —
  one parameter of two survived each time, e.g. expected `["escalate","reason"]`, actual `["reason"]`.
  `OhDataRegistration` gains an internal `Name`, threaded from the registration name
  `OhDataBuilder.Register()` already captured, and prefixed onto all **three** key sites. The unbound
  key was the worst of them, being scoped to nothing but the operation name.

  **Explicitly not changed: the cache lifetime.** The process-wide static is deliberate — it exists so
  the type-emit work runs once per type rather than per request. The defect was the key; making the
  cache per-request would have "fixed" the symptom by regressing the reason the cache exists.

  > **⚠ BREAKING CHANGE — every generated action-body schema component is renamed.** The registration
  > name is now prefixed onto the generated CLR type's name, and the **OpenAPI schema component name
  > derives from that type name** (`ActionBodySchemaTypeFactory.DefineType` says so at the site), so
  > the components in `components.schemas` change for *every* registration — not only the colliding
  > ones. Compared against the **1.6.0** tag, not against an intermediate state on this branch:
  >
  > | | 1.6.0 | 1.7.0 |
  > |---|---|---|
  > | unnamed registration, `POST /Widgets/Archive` | `Widgets_Archive` | `__default___Widgets_Archive` |
  > | `AddOhData("v1", …)`, same route | `Widgets_Archive` | `v1_Widgets_Archive` |
  > | entity-bound variant | `Widgets_Archive_Entity` | `__default___Widgets_Archive_Entity` |
  > | unbound `POST /Greet` | `Unbound_Greet` | `__default___Unbound_Greet` |
  >
  > `__default__` is `OhDataDefaults.DefaultRegistrationName`, the key an unnamed `AddOhData()` uses;
  > the three underscores are the two from `__default__` plus the `.` separator, which the type-name
  > sanitizer maps to `_`.
  >
  > **Who is affected:** anyone generating client code from the OpenAPI document of a service with a
  > bound or unbound **action** (functions take query-string parameters and emit no body schema).
  > **Direction:** regenerated clients get renamed model classes. Nothing on the wire changes — this
  > is document-only — and the schema *contents* (the parameter properties) are unchanged.
  > **Remedy:** regenerate and accept the new names, or map them back in your generator's
  > configuration. There is no flag to restore the old names: they were not unique, which is the
  > defect.
  >
  > No test asserted a schema **component name** before this release, in any companion suite —
  > `ActionBodySchemaRegistrationIdentityTests` asserts *property* names — which is why the rename
  > shipped unnoticed.

- **The compiled-delegate caches no longer freeze the startup scope's profile instance into every
  request (#483).** `s_etagCache`, `s_keyToStringCache` and `s_keyToUrlCache` are keyed by `GetType()`
  and store a delegate compiled from the **first-constructed** instance's expressions — which is the
  startup-scope instance, whose scope `OhDataBuilder` disposes as soon as the registration is built.
  `UseETag`'s comment declared the delegate safe to share because it *"accesses model properties only
  (no DI dependencies)"*; nothing checked that, and profiles are registered `AddScoped` **precisely** so
  they can inject scoped services. Measured — a live `ObjectDisposedException` on a request:
  `System.ObjectDisposedException : Cannot access a disposed object. Object name: ScopedStamp.` For a
  *non-disposable* dependency it was worse: another scope's instance reused silently, with no signal at
  all.

  The caches now serve and populate **only** for a selector that reads nothing but its lambda parameter
  (`CapturedState.IsCapturedByExpression`); a capturing one is compiled per instance instead.
  **Nothing is rejected** — a capturing selector simply becomes correct. Constants are the whole hazard
  and statics are not: C# compiles a capture into a `ConstantExpression` holding the display class (or
  the declaring instance itself, when only `this` is captured), frozen when the lambda is compiled,
  whereas a static field or property is read at *invocation* time and is per-process by construction.
  Value-typed and `string` constants — a literal, an enum such as `StringComparison.Ordinal` — are
  immutable and belong to no instance, so they do not count. The key selector gets the same gate on both
  its caches, so *"no cache in this type stores instance-derived state"* is a property rather than a
  case analysis.

  > **⚠ Upgrade note — an existing capturing selector became correct *and* materially slower (#548).**
  > `UseETag` runs in the profile constructor and profiles are `AddScoped`, so "compiled per instance"
  > means **per request**: losing the cache pays an `Expression.Compile()` on every request that reaches
  > the route. **Measured** end-to-end on `develop` @ `7211a6f` (TestServer, 200 requests each after
  > warm-up, two runs) on `GET /Set(key)`: **0.63–0.69 ms/req** capturing against **0.20–0.26 ms/req**
  > non-capturing — roughly **2.5–3.5×**, about **+0.4 ms per request**.
  > `UseETag(m => m.Title + _salt.Value)` — the injected-dependency shape the framework invites, and the
  > exact shape #483 exists to make correct — is what pays it.
  >
  > The remedy is to hoist the captured value out of the selector so it reads nothing but its lambda
  > parameter: fold the value into a model property and write `m => m.Title + m.Salt`. Note that
  > assigning it to a local first does **not** help — a captured local is still compiled into a display
  > class and is still a capture. Promoting it to a `static` field or property does restore caching,
  > because a static is read at invocation time rather than frozen at compile time — but only do that
  > for a value that genuinely is per-process; parking a scoped service in a static is undetectable here
  > and is the failure #483 exists to prevent. A non-null reference-typed constant in a selector (a
  > `typeof(X)`, say) also counts as a capture and costs the same, deliberately: the judgment errs
  > toward *"captured"*.

- **The three `JsonDocument` write-path sites now read the body the way the binder does (#514).** #511
  gave the two raw-UTF-8 span scanners binder parity through `CreateBinderParityReader`, but the sites
  that materialise a buffered body with `JsonDocument.ParseAsync` still used a **default**
  `JsonDocumentOptions` while the binder reads with the registration's `JsonSerializerOptions`. On a
  host that relaxed its `Http.Json` options the collection `POST` therefore answered `400` for the exact
  bytes `PUT` accepted — measured, `Post_HostRelaxedJsonOptions_AcceptsTheSameBytesPutAccepts`, expected
  `Created`, actual `BadRequest` — which is the per-verb divergence this milestone spent ten PRs
  removing, one option over from #456's. `CreateBinderParityDocumentOptions` derives
  `AllowTrailingCommas`, `CommentHandling` and `MaxDepth` from the registration's options: the same
  three members `CreateBinderParityReader` derives, and what `JsonSerializerOptions.GetReaderOptions()`
  derives internally for `DeserializeAsync`, so this is parity rather than a second guess. .NET 10's
  `AllowDuplicateProperties` is deliberately not derived — `OhData.AspNetCore` multi-targets `net8.0`,
  where the member does not exist — and that residual runs the safe way. Unlike #511's scanners this
  direction failed **closed**: the stricter reader rejected a request rather than silently disabling a
  guard.

  It also moved `OpenTypeJsonOptions.RewriteWithoutUnbindableKeys`' re-parse, which is a **third** reader
  over the same body. Once `ParseAsync` honours a raised `MaxDepth`, a body it accepts would have been
  rejected there — a divergence this fix would have *created* rather than merely exposed.

- **`PATCH` no longer feeds client-supplied strings to a process-wide memoizing cache (#510).**
  `ODataPropertyNaming.FindClrPropertyByEdmName` memoizes on `(Type, string)` in a process-wide
  `ConcurrentDictionary` keyed by the caller's **exact** string, and the `PATCH` delta loop — plus
  #454's key-mismatch guard — called it once per **body property name**. The lookup is what caches, not
  the result, so an unmatched key grew the dictionary by one entry per distinct spelling, unbounded,
  straight from the request body. A startup `patchPropByBodyName` table — built from the model's own
  properties under `OrdinalIgnoreCase`, with the EDM name and the CLR name as non-overwriting keys —
  answers both call sites now, identically to the call it replaces by construction.

  Deliberately **not** keyed off the binder's contract the way #511 keyed the deep-write table: that
  changes what `PATCH` *binds*, which does not belong in a memory fix. Filed as **#536**, together with
  its measured manifestation — under `SnakeCaseLower`, `PATCH {"first_name":"x"}` is a `200` no-op.
  `FindClrPropertyByEdmName`'s cache remains uncapped, now with no client-reachable feeder; that is
  tracked as **#537**.

- **The profile scanner skips open generic profile types, and a scan followed by an explicit
  registration is a no-op (#488 item 5).** An open generic is a template, not a profile: it was
  discovered, registered in DI, and then killed `MapOhData()` with a raw
  `MemberAccessException: Cannot create an instance of …` naming no remedy, with no way to exclude it
  from the scan. Skipping is what every DI scanner does, and a *closed* generic profile is still
  discovered normally; one predicate serves both profile kinds, so the entity-set path is covered by the
  same line. Separately, `AddDeltaProfile<T>()` after a scan that had already discovered the same type
  threw *"already registered. Remove the duplicate `AddDeltaProfile` call."* — while the reverse order
  was already a silent no-op, so the outcome depended on declaration order and the message blamed the
  developer's single explicit call. Explicit registrations are tracked per builder now, and the throw
  fires only when a duplicate explicit call is what actually happened. The identical ordering defect on
  the **entity**-profile path is filed as **#534** and is not fixed here.

- **The action body-schema cache is keyed by registration identity, not by its name (#547).**
  #499/#527 narrowed this and did not close it: `ActionBodySchemaTypeFactory`'s process-wide cache
  was keyed by `$"{registration.Name}.{set}.{action}"`, and `Name` is `__default__` for **every**
  unnamed registration in the process. Measured — two independent `WebApplication`s in one process,
  both with the default registration, both exposing `ZZSchemas` with a bound action `Submit` of
  different signatures:

  ```
  host1  Submit(string note)                 -> body schema props: note
  host2  Submit(string note, int priority)   -> body schema props: note     <-- wrong
  same type instance: True
  ```

  So the second host's OpenAPI document silently documented the first host's request body. All three
  key sites were affected — the collection-level bound action, the entity-level bound action, and
  the unbound operation (whose key carried neither a registration nor an entity set name). Mostly a
  concern for multi-host processes rather than single-host production, but that is precisely what an
  integration-test suite is, and a wrong document there is hard to attribute.

  The cache is now a `ConditionalWeakTable` keyed on the `OhDataRegistration` **instance**, following
  `EdmClrTypeMap`'s per-`IEdmModel` shape. Its process-wide **lifetime** is unchanged and deliberate
  — it exists so the `Reflection.Emit` work runs once per distinct shape rather than once per route
  mapped, and that memoization is verified to still hold within a registration. The registration name
  survives only as the generated type's human-readable label, where two registrations sharing one
  name get the numeric suffix that mechanism already applies. No wire behaviour changes.

  **Read the schema-name claim against 1.6.0, not against #499.** This entry previously said the
  generated schema names for existing single-host and named-registration setups are *unchanged* —
  true only relative to #499's unreleased intermediate state, and **false** relative to the 1.6.0
  baseline an adopter is upgrading from. #499 renamed every action-body schema component in this
  same release; see its `⚠ BREAKING CHANGE` callout above for the before/after table. What #547 adds
  on top of that rename is nothing: it changes the cache *key*, not the label.

### Documentation

- **`docs/authorization.md` gains "The composition: securing everything you can name" (#487).** Three
  individually-documented behaviours compose into a quiet fail-open, and no single document owned the
  system-level property. The new section states it, and states the third seam that is deliberately
  *not* changed: a category-level `.AllowAnonymous()` overrides a host-applied
  `app.MapOhData().RequireAuthorization()`. That is ASP.NET Core's own `AllowAnonymousAttribute`
  semantics — verified with a control test containing no OhData at all, in which a plain `MapGroup`
  carrying `RequireAuthorization()` serves an endpoint marked `.AllowAnonymous()` with `200` while its
  sibling answers `401`, regardless of the order the two were applied in. It is not warned about
  because `.AllowAnonymous()` is the only way to express a deliberate public hole in an otherwise-
  gated surface, so a warning would fire on correct configuration with no way to silence it. The
  "Global auth" section's claim that a group-level requirement covers every route in the group is
  narrowed accordingly.


- **The group exception filter's "outermost group filter (added first)" claim was false and is
  corrected (#496).** The #200 observability filter is added first and wraps it. The consequence is
  now stated rather than implied: an exception thrown in the observability filter's own body or in
  its `Response.OnCompleted` callback escapes the error envelope. Reordering the two is deliberately
  *not* done — it would move the filter's `LogError` outside the request's `Activity` and lose trace
  correlation on the single most important log line the framework emits, a worse trade for
  framework-only code that does no I/O and runs no user code.

- **Every spec citation in the tree was checked against the OASIS Part 1 text, and the ones that did
  not resolve were corrected (#578).** Fifteen sites cited **§11.2.12**, a section that does not
  exist — Part 1 §11.2 ends at §11.2.10 (`$format`). The whole compliance table used **4.01**
  numbering (`$filter` as §11.2.6.1 … `$skiptoken` as §11.2.6.7) in a document declaring 4.0, where
  those clauses are §11.2.5.1–.7. Several wrong-section cites shipped in the **public XML docs**
  inside the NuGet `.xml` (the `ETag` response header as §8.2.6 — it is §8.3.1; `If-Match` as §8.2.5
  — it is §8.2.4; `Content-Type` as §8.2.1 — it is §8.1.1). A citation with no line number is not
  checkable, so each corrected site now carries one.

- **A fabricated OData quotation is withdrawn from the documentation *and* from the source (#578,
  #566).** **Twelve** sites justified excluding bound actions from the `If-Match` precondition gate
  by asserting that an action-invocation resource *"has no representation and therefore no entity
  tag"*, citing Protocol §11.5.4 — six of them under `src/`, one of those the runtime exception
  message the bound-action ceiling throws, which had been carrying the false citation to operators
  in text that ships. **That phrase appears nowhere in Part 1** — `grep -ic
  "no representation"` over the specification returns `0` — and four clauses say the opposite:
  §11.4.1.1 is a MUST covering *"a Data Modification Request **or Action Request**"*, §8.2.4 and
  §8.3.1 name Action Requests explicitly, and §11.5.4.1 instructs the **client** to send `If-Match`
  for exactly this case. The behaviour is unchanged and now ships labelled as a **known deviation**
  tracked by [#566](https://github.com/en-gen/OhData/issues/566) — in `docs/etags.md`,
  `docs/spec-compliance.md`, `CLAUDE.md`, the four comment sites in `OhDataEndpointFactory` and the
  three test fixtures that had been asserting the exclusion as correct behaviour. The separate
  *no-continuation* argument for a bound action survives on its own footing and is re-grounded in
  §11.2.5.7 — a next link is one that *"allows retrieving the next partial set of items"*, and
  `POST /{Set}/{Action}` is not GET-addressable — which is what the corrected exception message
  now says.

- **`README.md` no longer asserts a security claim this release withdrew (#578).** It said the
  resource check *"covers property/navigation/`$ref` routes too … so there's no bypass"* — the exact
  claim #481 measured false, having found `$ref` `POST`/`PUT`/`DELETE` and the navigation-`POST`
  route **executing writes anonymously** against an admin-gated child set. `docs/authorization.md`
  was corrected during the release; the README was untouched by all 25 preceding commits.

- **Two documents prescribed remedies that now throw (#578).** `docs/deep-insert.md` promised a
  `400` carrying `"Post handler returned null."` — a string #496 removed from the assembly — and
  `docs/bound-operations.md` told developers to return `Results.BadRequest(...)` from an operation
  handler, which #498 §3 refuses at **bind time**, so following the documented advice crashed the
  app at startup.

- **An "Upgrading from 1.6.0" checklist opens the release notes (#578).** The breaking-change prose
  below it is honest and nearly unusable as a migration list; the checklist separates what stops the
  app starting, what changes status on the wire, and the silent behaviour changes.

- **The #487 *"46 emissions / 24 distinct subjects"* figure is withdrawn (#578).** It is not
  reproducible from source and no test pinned it — this repo's own measurement-provenance rule. It
  may be restored only alongside a test that pins it.

### Tests

- **Regression coverage for #344 (silent data loss on an inherited navigation under `$expand`).** The
  defect itself was closed by #462's `HasSameMetadataDefinitionAs` fix — a follow-on neither issue
  named — but no fixture covered #344's own shape: an entity set rooted at the **derived** type with
  its navigations declared on the base EDM type. Every existing suite roots its set at the base type,
  where the two reflection walks agree. The new fixture is **verified to fail** — all four cases,
  collection and `GetById`, collection- and single-valued navigation — by restoring the single `!=`,
  with the base-rooted byte-identity control staying green. #344's *second* face (the EF pushdown
  member-init dropping a TPH-derived row's own structural properties when `$expand` is added) is a
  projection-shape problem and is **not** fixed here.

- **Two test fixtures whose shared or regenerating state made tests silently vacuous or racy (#451,
  #515).** Test-only; no shipped code changed and no wire behaviour moves.

  **#451.** `ExpandPagingEnabledTests`' EF InMemory fixture called
  `UseInMemoryDatabase(Guid.NewGuid().ToString())` **inside** the `AddDbContext` options lambda. That
  lambda runs once per `DbContext` *instantiation*, not once at registration, so every scope got a
  different database name and therefore a **fresh empty database** — the seeding scope and the
  request scope could never see each other. It was latent only because every test on the fixture
  asserts on startup log output, which an empty database cannot disturb. The name is hoisted to one
  value per fixture at both sites in the file, the fixture now seeds two parents through the host's
  own scope, and a new row-serving test reads them back over HTTP. **Verified to fail** against the
  unfixed fixture with `Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 0` — every row
  gone, under a `200`. A repo-wide sweep found no other instance: the four remaining
  `UseInMemoryDatabase` call sites are all correct (three use a fixed literal name and seed
  if-empty; one is a per-test factory method that deliberately wants a fresh database and seeds it on
  the spot).

  **#515.** `IgnProductProfile.LastPosted`/`LastPut`/`LastPatchChangedNames` were process-wide
  `static` fields reset-then-asserted by `IgnorePropertyIntegrationTests` **and**
  `OpenTypeIgnoreContainmentTests`, the latter clearing two of them from `InitializeAsync` so its
  *setup* could land inside the other's assertion window — #484's race exactly. It had been papered
  over with a shared `[Collection]`, which schedules around the shared state rather than removing it
  and costs parallelism between two classes with no reason to be serialised. Following #484's shape:
  the captures are now an `IgnProductWriteCaptures` singleton registered per host, the statics and
  every reset site are **deleted** (a capture that cannot be reset cannot be reset at the wrong
  moment), and the `[Collection]` attribute is gone. The issue's stated reason for not doing this the
  first time — `IgnProductProfile` is registered from three files across six call sites, and a missed
  one is a request-time DI failure rather than a compile error — is closed structurally: the capture
  is a **required constructor parameter**, all six sites route through one `IgnProductHost` helper,
  and `IgnProductCaptureRegistrationTests` pins that a host skipping the registration throws
  `InvalidOperationException` naming `IgnProductWriteCaptures` out of `MapOhData()`, i.e. at host
  build, before any request.

- **The k6 layer grew from one 410-line smoke script into a smoke suite plus a conformance suite.**
  Test infrastructure only; no shipped code changed. k6 is the only place this repo exercises the
  **real containerized TestBench over real HTTP** — all 3,459 xUnit tests use
  `WebApplicationFactory`/TestServer, which is in-process and bypasses the HTTP stack, which is why
  `RequestBodySizeFeatureTests` has to install a fake `IHttpMaxRequestBodySizeFeature` through an
  `IStartupFilter` to test anything about request-body limits at all. Before this change `smoke.js`
  never touched `$expand`, navigation routes, `$ref`, bound operations, `If-Match`, `Prefer`,
  `@odata.bind`, `$search`, `$apply` or an unrecognized `$`-option, and **asserted no response header
  anywhere** — it would not have caught a single defect this release found. Now `smoke.js` covers
  every route family at 230 checks / 75 requests, `conformance.js` carries the matrices at 998
  checks / 287 requests, and `OData-Version` is asserted on **every** request in both (§8.1.5
  requires it universally and it was checked nowhere; the one unmapped-route exemption is *pinned*
  rather than granted). The conformance suite mirrors the `s_*ImplementedOptions` arrays one-for-one
  across 16 options with everything absent from an array **derived** as `501`, asserts the three
  distinct refusal wordings, §11.2.9's `/$count` contract on the **number** rather than the status,
  the error envelope across `400`/`404`/`406`/`412`/`413`/`415`/`500`/`501`, and
  `If-Match: W/"<live>"` → `412` beside `If-None-Match: W/"<live>"` → `304` — the strong-vs-weak
  rule, invisible without both halves.

- **Three pre-existing k6 harness defects fixed.** `.gitignore`'s `/k6/reports/*` rule is
  root-anchored and matched nothing (the real path is `tests/k6/reports`), so every run left
  untracked files behind; `handleSummary` replaced k6's default summary, so `smoke.js` printed
  **nothing** to the CI console; and the `checks` threshold was `rate>0.99`, which at ~1,200
  deterministic assertions lets a real regression ship green — a failing `check()` does not by
  itself fail a k6 run, the threshold is the only gate. It is `rate==1.00` in both scripts now.

### Build

- **Dependabot updates are grouped, and the PR limits now match this repo's own five-in-flight
  rule.** Measured on a single push to a PR: `build-and-test` 4m25s + `Analyze (csharp)` 5m09s +
  `k6` 1m03s, plus GitHub's own Code Quality (4 jobs, 5m11s) and dependency submission (52s) —
  **≈17 minutes of runner time per push**. Ungrouped, at the previous `10` + `5` limits, a quiet
  week could open fifteen bump PRs and spend roughly **four hours** of that moving test-package
  version numbers. The routine half now collapses into one PR per ecosystem.

  The split is **not** "important versus unimportant" — it is *does this change what an adopter
  resolves*. Six ids are excluded from the group because they are `PackageReference`s of the five
  packable projects, so a bump there moves a dependency **range** in the published `.nuspec`:
  `Microsoft.AspNetCore.OData`, `Microsoft.AspNetCore.OpenApi`, `Microsoft.OData.ModelBuilder`,
  `Microsoft.OpenApi`, `NSwag.Generation.AspNetCore`, `Swashbuckle.AspNetCore.SwaggerGen`. This
  very release shipped exactly such a change (`Microsoft.OpenApi`'s minimum, `2.12.0` → `2.12.2`),
  and it needs its own PR and its own CHANGELOG line. `Microsoft.OData.Client` is excluded for a
  different reason: it is dev-only, but it is the **compatibility target** of
  `OhData.MicrosoftODataClient.Tests`, and claims in `CLAUDE.md` and this file are measured
  against a specific version of it — a silent batch bump would invalidate a measured claim with
  nothing pointing at it. **Majors are not grouped either** (`update-types: [minor, patch]`), so a
  major lands alone; `xunit.runner.visualstudio` went `3.1.5` → `4.0.0` during this cycle.

- **Negative result, recorded so nobody retries it: `linguist-detectable=false` does not turn off
  GitHub's Code Quality language jobs.** Code Quality runs `Analyze (java-kotlin)`,
  `Analyze (python)` and `Analyze (javascript-typescript)` on every push — ~2m20s — solely because
  `tests/olingo/` (2 `.java`), `tests/pyodata/` (2 `.py`) and `tests/k6/` (4 `.js`) exist. Those are
  manual cross-client conformance harnesses: they ship in nothing, **no workflow builds or runs the
  first two**, and no linter or formatter covers any of them, so the analysis buys nothing. Marking
  all three `linguist-detectable=false` in `.gitattributes` was tried and **measured on PR #585:
  all three jobs still ran**, so Code Quality does not select languages from linguist attributes.
  The change was reverted rather than kept for its cosmetic effect on the repository language bar.
  The only remaining lever is a repository setting (Settings → Advanced Security → Code quality),
  which no file in this repository controls.

- **Verified rather than assumed: CI's `Pack (dry run)` really is an API-compatibility gate.** It
  runs `dotnet pack --no-build`, and package validation is semaphore-gated, so "the step is green"
  and "the step compared anything" are different claims. Ablation — flipping
  `OhDataRegistration.EntitySetNames` from `public` to `internal` and running CI's exact command —
  fails with `error CP0002` against the real `1.6.0` baseline package **on both target
  frameworks**. Two things that were *not* optimized, and why: a NuGet cache saves ~8s (restore is
  already **11s** of a 4m25s job, so a lock-file regime buys nothing), and matrixing the seven test
  steps would cut ~45s of wall-clock while paying the 76s restore-and-build six more times.

- **`feature/`, `bug/` and `hotfix/` branch names resolve their version label again (#520).**
  `GitVersion.yml`'s `feature:`, `bugfix:` and `hotfix:` configs set `label: '{BranchName}'` without
  the `(?<BranchName>.+)` capture group the placeholder resolves from. Same class as #518: a
  GitVersion regex serves two jobs at once, and a pattern satisfying one silently breaks the other.

- **CI: Codecov Test Analytics added; superseded PR runs are now cancelled; the format check's
  duplicated half is dropped.** No effect on the shipped packages.

- **`Microsoft.OpenApi`'s minimum rises to `2.12.2`** (was `2.12.0`) in
  `EnGen.OhData.AspNetCore.OpenApi`. The range is still `[2.12.2, 3.0.0)`.

- **`PackageValidationBaselineVersion` moves to `1.6.0`** on all five packable projects, so this
  release's API surface is diffed against the previous *shipped* one. No `CompatibilitySuppressions.xml`
  exists in the repository and none was needed. Note what ApiCompat does **not** cover: it compares
  API *shape* only — a changed default, a changed status code, or any other behavioural break passes
  it silently, so a green pack is not evidence that this release is non-breaking. The `### Breaking`
  section above is.

- **The `[1.6.0]` CHANGELOG link definition was missing and `[Unreleased]` still compared against
  `v1.5.0`.** Both are repaired; `## [1.6.0]` had been rendering as literal text since it shipped.

---

## [1.6.0] - 2026-08-27

### Added

- **`Prefer: odata.maxpagesize` is now honoured on nested `$expand` collections and their
  continuation (#412).** The preference governed the **root** collection only. Measured pre-fix with
  `MaxExpandTop = 4`, `ExpandPagingEnabled = true` and `Prefer: odata.maxpagesize=2`:
  `GET /Authors?$filter=Id eq 1&$expand=Books` served **four** books and linked at `?$skip=4`, and
  `GET /Authors(1)/Books` ignored the header outright. Both now serve two and link at `?$skip=2`.

  Protocol §8.2.8.5 settles it: the preference asks that *"each collection within the response"* stay
  within the requested size, and its own example spells out next links *"for all returned orders
  collections"*. It also refutes the blocker #412 itself recorded — the concern that a `$skip`-only
  link carries no page size, so hop 2 could not reproduce hop 1. The spec expects the page size to
  travel on the **request** (*"the client MAY specify a different value … with every request following
  a next link"*), so the continuation route simply reads the same header and #313's deliberately
  narrow `$skip`-only link surface does not widen by one character. Correctness does not depend on the
  client resending anything: `$skip` is absolute and each hop advances it by the rows it actually
  served. Both spellings — `maxpagesize` and `odata.maxpagesize` — are accepted and pinned.

  **Clamped down, never up, and never over the ceiling.** The effective nested page is
  `min(requested, MaxExpandTop)`, mirroring how the root clamps to `MaxTop`, so a client cannot lift
  the server's DoS bound. It narrows the **page** and never the **ceiling** — lowering the ceiling from
  a request header would let a header turn a `200` into a `400`. It is read on **one** arm, the bare
  leaf that is emitting a `Nav@odata.nextLink`, so a non-pageable over-ceiling shape keeps its `400`
  and ignores the header entirely: trimming without a link is silent truncation, and that stays
  impossible. The SQL bound moves on the continuation — the one hop where the framework owns the query
  — from `ceiling + 1` to `pageSize + 1`, still as a parameter so the plan cache is intact.

  **`Preference-Applied` is deliberately untouched, and that non-change is pinned too.** §8.2.8.5
  makes the echo a `MAY` and gives it **one** value for the whole response, so the "same header, two
  collections" ambiguity #412 raised needs no second header — there is no per-collection echo to emit,
  and the root's existing header already reports a page size actually applied. This also means the
  echo's token spelling is untouched: it says `maxpagesize` where OData 4.0 spells it
  `odata.maxpagesize`, which is #372's defect on milestone 1.9.0, and closing it accidentally here
  would make that fix invisible.

- **`$metadata` now advertises `ExpandRestrictions/Expandable = false` on entity sets that reject
  `$expand` (#303).** Such a set previously advertised `MaxLevels = 3` and nothing else, which reads
  as *"expand up to 3 levels"* for a set that `400`s **every** `$expand` — actively misleading, and
  #367's headline evidence.

  `Expandable` is **omitted** when `$expand` is enabled, because `true` is the vocabulary's own
  default for that property. That is what makes this a strict addition: every entity set that already
  advertised correctly is byte-identical, and the only CSDL delta anywhere is one `PropertyValue` line
  on sets with `$expand` disabled.

  **`MaxExpandTop`, `MaxExpandBreadth` and `MaxTop` stay unadvertised, and this establishes why rather
  than approximating it.** #303 asked for `MaxExpandTop` as an `Org.OData.Capabilities.V1` annotation.
  It cannot be: the vocabulary contains exactly **one** numeric slot — `MaxLevels` — and in every type
  that carries it, it means a nesting or traversal **depth**, never a count of entities. There is no
  term at any scope for a maximum result count, page size or `$top` ceiling, and advertising
  `TopSupported = false` would be false, since a nested `$top` *is* supported up to the ceiling.
  Verified two independent ways: the Capabilities CSDL bundled in the exact `Microsoft.OData.Edm`
  version this repo resolves, and the upstream OASIS source. No custom `OhData.V1.*` term is minted —
  a non-standard annotation is not discoverable by any client that does not already know OhData, so it
  buys no interoperability while implying some. The vocabulary claim ships as a **live assertion**
  rather than a comment, so an Edm bump that introduces a count term fails the suite and reopens #303.

  For reference, `Microsoft.AspNetCore.OData` 9.5.0 advertises none of these — its model-bound
  settings are CLR-side and never reach the CSDL — so matching its shape would mean emitting nothing
  at all.

- **`MaxExpandBreadth` — a breadth guard on `$expand`, defaulting to `50` (#429, shipping #202's
  never-shipped guard).** `$expand` cost was bounded on the **depth** axis (`MaxExpansionDepth`) and
  completely unbounded on the **breadth** axis: there was no navigation-count limit of any kind. A
  request whose `$expand` contains more than `MaxExpandBreadth` navigation expansions, counted
  across **every level of the tree**, is now rejected with `400` (`InvalidQueryOption`) before any
  handler runs — on all three collection read paths and on `GET /{Set}({key})`.

  > **⚠ BREAKING CHANGE, in the restrictive direction.** A client sending an `$expand` with more
  > than 50 navigation expansions now gets `400` where it previously got `200`. Fifty is far above
  > any realistic request, so an existing consumer is unlikely to hit it — but it is a new rejection
  > on a previously-accepted request, and it is configurable:
  > `WithDefaults(d => d.MaxExpandBreadth = N)` server-wide, or `MaxExpandBreadth` on the profile.
  > There is deliberately no "unlimited" setting; a guard that defaults to unlimited protects nobody.

  **Why depth alone was not enough.** Translation cost multiplies by ~3 per level *and* by the
  number of navigations expanded at each level. Measured at the **default** `MaxExpansionDepth` of
  3, on a six-navigation model, with no breadth guard:

  | navigations per level | wall clock | response |
  |---:|---:|---:|
  | 1 | 240 ms | 1,440 B |
  | 4 | 1,010 ms | 1,696 B |
  | 6 | 4,084 ms | 1,952 B |

  4.1 s of single-core CPU for a 1,952-byte response, at defaults, unauthenticated — and the EF
  compiled-query cache is no defence, because each distinct navigation **subset** is a distinct
  cache key, so cycling subsets never warms it. (That table is the original #429 measurement; the
  "why 50" figures below were taken later on a faster machine, where the same shape reproduces at
  ~1.6 s. Compare each set internally, not across the two — the ratios hold in both.)

  **Why the count spans the whole tree rather than one level.** A per-level cap of `B` under the
  depth ceiling of 6 still admits `B⁶` expansions — 55,986 at `B=6`. Counting every node bounds both
  axes together. Counting *distinct navigation names* would be weaker still: the most expensive
  shapes measured reuse six names over six levels. A `$levels=N` expansion counts as `N` — its
  resolved level count — because that is what it costs.

  **Why 50.** Far above any realistic request (a three-level chain expanding three navigations at
  every level is 39 nodes and is already unusual; typical rich requests are under 15), while keeping
  the worst legal request measurable: ~0.4 s at the default depth of 3, and — over a systematic
  sweep of every branching vector within the budget at the maximum legal depth of 6 — **1.0–1.4 s**
  for the worst legal request (shape `[1,1,1,1,2,6]`, only 18 nodes: deep-and-narrow is more
  expensive per node than flat-and-wide). Unguarded, the same model reaches 2,850 nodes and **36 s**
  for a 111-byte error response; that request now returns `400` in **56 ms**, essentially all of it
  URL parsing.


- **A startup `Warning` for a bare `$expand` with no ceiling, and the `ExpandPagingEnabled` knob it
  points at (#313).** These are the replacement for the arbitrary `MaxExpandTop` default removed
  above — the framework stops guessing a number and instead names the exposure to the one person who
  can price it.

  At `MapOhData()` OhData now logs one `Warning` per navigation that is collection-valued,
  delegate-less, on a profile that has `GetQueryable`, `ExpandEnabled` **and** `ExpandPushdownEnabled`,
  when that profile's resolved `MaxExpandTop` is `null` — exactly the navigations a bare
  `?$expand=Nav` will materialize in full. It names the entity set, the navigation and
  `MaxExpandTop` **and** `ExpandPagingEnabled`, in that order — the second does nothing without the
  first — and prescribes no number: leaving it unset is a legitimate choice for a collection you know
  is small. Emitted once at startup, never per request. Because `ExpandEnabled` is `false` by default,
  a registration that never opts into `$expand` gets **no** warning at all — measured across the
  suite, 1370 of 1512 registrations (90.6%) are silent and the loudest emits 7.

  `ExpandPushdownEnabled` is in that list because it was measured to matter, not because the design
  called for it: with expand pushdown off no `EngagedExpand` is built, so `?$expand=Books` over a
  seeded five-book author returns `"Books":[]` and issues no child query at all. There is no
  materialization for `MaxExpandTop` to bound, so warning would name a knob that changes nothing
  for that registration. It defaults to `true`, so this narrows little in practice.

  `ExpandPagingEnabled` (profile-level `bool?`, inheriting `EntitySetDefaults.ExpandPagingEnabled`,
  default `false`) is the companion opt-in: whether a *truly bare* collection `$expand` over the
  resolved `MaxExpandTop` is served as its first `MaxExpandTop` children plus a
  `Nav@odata.nextLink` continuation instead of being rejected with `400`. It is a separate opt-in
  from the ceiling because a continuation link is **worse** than a `400` for a client that does not
  read nested annotations — that client sees a complete-looking collection that has been silently
  truncated. `MaxExpandTop` is also the page size, for the first page and every continuation alike;
  there is deliberately no second page-size knob, and a `bool?` (unlike a second `int?`) lets a
  profile-level `false` genuinely opt **out** of a server-wide `true`.

  **What the knob does is the entry below.** As this entry originally shipped,
  `Prefer: odata.maxpagesize` was unhonoured on nested collections (#412) — an unmet spec `SHOULD`
  rather than a violation, since nothing claimed it was applied. **That is no longer true: #412 was
  closed later in this same release**, so the preference now sizes the nested page and its
  continuation as well as the root's. `MaxExpandTop` remains the default nested page size and the
  hard ceiling; the preference clamps **down** from it and can never lift it. See the `### Added`
  entry for #412.

- **`ExpandPagingEnabled` pages a truly bare `$expand` instead of rejecting it, via
  `Nav@odata.nextLink` and a `$skip`-only continuation route (#313).** With **both** knobs set —
  `MaxExpandTop` to a ceiling and `ExpandPagingEnabled` to `true` — a bare `?$expand=Nav` whose
  related collection exceeds the ceiling is served as its first `MaxExpandTop` children plus

  ```jsonc
  { "Id": 1, "Books": [ /* MaxExpandTop rows */ ],
    "Books@odata.nextLink": "https://host/odata/Authors(1)/Books?$skip=3" }
  ```

  and `GET /{Set}({key})/{Nav}?$skip=N` is registered to serve the rest. Follow it to exhaustion the
  way you would any server-driven page.

  **Only the truly bare leaf pages.** "Bare" means a nested option list that normalizes to the
  identity transform: `$skip=0` and `$count=false` are the only two no-ops the parser lets through
  and both page. Everything else keeps the `400` it has today — a nested
  `$filter`/`$orderby`/`$select`/`$skip>0`/`$count=true` (a `$skip`-only link cannot carry it), a
  level with its own nested `$expand` and any `$levels` (neither is SQL-bounded at all, so a link
  there would advertise a bound that does not exist), depth ≥ 2, a navigation whose element type has
  a composite or unresolvable key, and any navigation a profile in the candidate set declares with a
  delegate. An explicit nested `$top` never links: the client asked for exactly N rows and got them.

  **A link and a trim are one step, or neither happens.** Pageability is decided per navigation at
  startup, but a link can still turn out to be unbuildable for an individual *row* — a nullable key
  property holding `null` has no addressable continuation. That row falls back to the ceiling's `400`,
  the same answer any other non-pageable over-ceiling shape gets; the expanded array is never trimmed
  without a `Nav@odata.nextLink` beside it. That invariant ("no bound without either a link or a
  `400`") is what the whole feature rests on, so it is asserted directly rather than inferred, by
  `BareExpandNullKeyTests`.

  **The continuation accepts `$skip` and nothing else.** Every other system query option — including
  `$select`/`$orderby`/`$top`/`$count`, which the delegate-backed navigation route does accept —
  returns `400 UnsupportedQueryOption`, rejected by the `$` sigil rather than a name allowlist so an
  option this build has never heard of is refused rather than silently ignored. There is nothing to
  carry: the link is only ever emitted for an expand that had no nested options at all. The page size
  is `MaxExpandTop`, for the first page and every continuation alike; it is never `MaxTop`, which is
  an independent knob with its own default.

  **`$format` is the one exemption, and it is not a data option.** §11.2.10 content negotiation is
  implemented once, on the group filter that wraps the entire OData surface — it never reaches this
  handler and cannot change a single row. Refusing it would have made this the only route in the
  surface that `400`s a conformant, already-supported option, and would have broken the common client
  habit of appending `$format` to a server-issued link. An unsupported `$format` **value** is still
  rejected, by that same group filter, unchanged.

  **Ordering is total on both sides.** The continuation composes an unconditional
  `OrderBy(childKey)` — resolved through the same call that composes the first page's tiebreaker —
  rather than reusing the root's `EnsureStableOrder`, which skips appending the key when the source
  is already ordered and would leave a pre-ordered parent's continuation without a total order. The
  emitted plan is an `INNER JOIN … LIMIT/OFFSET` index seek, not the partitioned `ROW_NUMBER()`
  window the first page uses.

  **A continuation for a key that does not exist returns `200` with an empty `value` and no link**,
  where `Microsoft.AspNetCore.OData` returns `404`. A `SelectMany` cannot distinguish "no such
  parent" from "a parent with no children", and an existence probe would cost a second round trip on
  every continuation. Documented divergence.

  **Inert unless you opt in.** With `ExpandPagingEnabled` at its `false` default — or with it `true`
  and no `MaxExpandTop` — no route is registered and no annotation is emitted. Measured as a
  byte-for-byte equality rather than asserted: with `MaxExpandTop` unset, turning the flag on changes
  neither the status, the response body nor a single emitted SQL statement; with a ceiling set, the
  same holds for every shape **outside** the truly-bare subset; and `$metadata` is byte-identical in
  every configuration, since the continuation route is a routing-table fact and not an EDM one. (The
  ceiling itself is the separate opt-in described in the `### Changed` entries above — this entry is
  about the knob, not about `MaxExpandTop`.)

  One new startup failure, and it can only fire on a registration that opted in: an entity-level
  bound function sharing a name with a pageable navigation now throws from `MapOhData()`. Both would
  claim `GET /{Set}({key})/{Name}`; the pre-existing collision check compares bound functions against
  structural properties only, which excludes declared navigations, so that pairing was legal until
  the continuation route existed. The same check still does not cover a bound function colliding with
  a **delegate-backed** navigation route — that collision predates #313 and is tracked in
  [#416](https://github.com/en-gen/OhData/issues/416).

  > **`EnGen.OhData.Client` reads this link — through the annotated terminal operations, and only
  > through those.** #417 ships in this same cycle (see the `### Added` entry below), so
  > `ToAnnotatedPageAsync`, `ToAnnotatedAsyncEnumerable` and `GetAnnotatedAsync` surface
  > `NextLinkFor(x => x.Nav)`/`CountFor(x => x.Nav)` and the emission is verified end to end against a
  > real server by `ExpandPagingSeamTests`. What stays true is narrower and is about **which call you
  > make**: `ToListAsync`/`ToPageAsync`/`ToAsyncEnumerable`/`GetAsync` still bind the envelope only, so
  > a paged nested collection looks complete through them; and any **third-party** client that ignores
  > unknown annotations sees a silently truncated collection with no error to notice. That last case
  > is the failure mode `ExpandPagingEnabled` is a separate opt-in to avoid, and it is unchanged.

  What this does **not** change: a sibling profile's delegate does not blank a *root-level* `$expand`.
  The root resolves navigation treatment against the URL-named profile alone, so those rows are served
  raw there — pre-existing and unchanged here. This entry originally called that *"tracked in #415"*;
  [#415](https://github.com/en-gen/OhData/issues/415) has since been closed as **refuted** in this
  same release — the frozen Model B spec settles root-level scoping as correct (*"Root (depth 1): KEEP
  as-is"*), so it is a decision rather than an open gap. The continuation route and the link emission
  both use the full cross-profile candidate set, so this stage does not widen that exposure.

  Documented in full — the two-knob interaction, the exact pageable set, the continuation surface and
  the deliberate limits — under
  [Nested server-driven paging](docs/query-options.md#nested-server-driven-paging-expandpagingenabled-313),
  with the conformance reading (JSON Format §24 producer item 15) in
  [`docs/spec-compliance.md`](docs/spec-compliance.md).

- **`EnGen.OhData.Client` can read per-entity OData annotations, so a nested `Nav@odata.nextLink`
  reaches the caller (#417).** The client bound four envelope members (`@odata.context`,
  `@odata.count`, `@odata.nextLink`, `value`) and dropped everything else — System.Text.Json cannot
  bind an `@`-bearing member to a CLR property — so a server that paged an expanded collection handed
  back a **prefix** that was indistinguishable from a complete collection. This is the client half of
  the `ExpandPagingEnabled` entry above; the two are verified against each other end to end by
  `ExpandPagingSeamTests`, which runs this client against a real server with both #313 knobs on,
  under the default naming policy and under camelCase.

  Three new terminal operations, each the annotation-preserving counterpart of an existing one:

  | New | Counterpart of | Returns |
  |---|---|---|
  | `EntitySetClient<T>.ToAnnotatedPageAsync` | `ToPageAsync` | `ODataAnnotatedPage<T>` |
  | `EntitySetClient<T>.ToAnnotatedAsyncEnumerable` | `ToAsyncEnumerable` | `IAsyncEnumerable<ODataAnnotatedEntity<T>>` |
  | `KeyedEntitySetClient<T>.GetAnnotatedAsync` | `GetAsync` | `ODataAnnotatedEntity<T>?` |

  ```csharp
  ODataAnnotatedPage<Author> page = await client.For<Author>()
      .Expand(a => a.Books)
      .ToAnnotatedPageAsync();

  foreach (ODataAnnotatedEntity<Author> entry in page.Entries)
  {
      Uri? more = entry.NextLinkFor(a => a.Books);   // non-null ⇒ entry.Entity.Books is a PREFIX
      long? size = entry.CountFor(a => a.Books);     // the FULL related-collection size
  }
  ```

  Three new public types: `ODataAnnotatedPage<T>` (`Entries`/`Items`/`TotalCount`/`NextLink`/
  `Annotations`), `ODataAnnotatedEntity<T>` (`Entity`/`Annotations`/`NextLinkFor`/`CountFor`) and
  `ODataEntityAnnotations` (`NextLinkFor`/`CountFor` by wire name, plus `TryGetValue`/`Values`/
  `IsEmpty` for the open-ended rest of the vocabulary — `JsonElement` is the ceiling there because
  past `nextLink` and `count` the client cannot know a CLR type). The expression accessors resolve a
  member through the client's own `PropertyNamingPolicy`, `[JsonPropertyName]` winning, exactly as
  the emitted query options do. Only annotations **directly attached** to an entity are captured; the
  reader does not descend into expanded children to collect theirs.

  **Nothing that does not call the new methods changes.** Preserving annotations means buffering the
  body and reading it a second time, so it is a separate method rather than a client-wide option —
  every existing read still streams (`ResponseHeadersRead` + a single `ReadFromJsonAsync`) and binds
  entities through literally the same code, so an annotated read cannot bind an entity differently
  from a plain one.

  Two API details worth reading before you migrate a call:

  - **`ToAnnotatedPageAsync` does *not* force `$count=true`, while `ToPageAsync` does.** It honours the
    builder instead, so add `IncludeCount()` or `TotalCount` comes back `null` — a **silent**
    difference if you swap one for the other. The reason is that an unconditional `$count=true` is a
    `400` against a server whose `CountEnabled` is off, which would make the annotated read strictly
    less usable than the plain one.
  - **Every link in the annotation surface is a `Uri`** — `ODataAnnotatedPage<T>.NextLink` and
    `NextLinkFor` on both types — so one concept has one representation. The pre-existing
    `ODataPage<T>.NextLink` stays a `string`: it is shipped public API and changing it would break
    callers for no benefit. That single seam surfaces as a **compile error** when migrating
    `ToPageAsync` → `ToAnnotatedPageAsync`, not as a silent difference. A returned `Uri` may be
    relative (OData permits either form); use `OriginalString` to follow it exactly as issued.

  `ToAnnotatedAsyncEnumerable` follows the collection's **own** `@odata.nextLink` across pages,
  exactly as `ToAsyncEnumerable` does. It never follows a **nested** link: that addresses a different
  resource with a different element type, so resuming it is the caller's decision to make with the
  `Uri` handed back. Documented under
  [annotation-preserving reads](docs/client/terminal-operations.md#annotation-preserving-reads).

- **`$levels` may now carry other nested expand options (#254).** A `$levels=N` / `$levels=max`
  self-referential expand combined with `$filter`, `$orderby`, `$skip`, `$top`, `$count`, or `$select`
  is no longer deferred off the pushdown path — it pushes, with those options applied at **every level
  of the recursion**, matching the semantics Microsoft's own OData stack implements (`$levels=N` is
  rewritten into `N` nested expands each carrying the same options). So
  `?$expand=Children($levels=2;$filter=active eq true)` filters at both levels (a filtered-out node's
  whole subtree disappears with it), `($levels=2;$count=true)` emits `Children@odata.count` at every
  level, and `($levels=2;$select=name)` keeps the self-navigation itself at every level while pruning
  the other properties. A `$levels` expand carrying its **own nested `$expand`** remains deferred
  (depth accounting between the `$levels` budget and the nested branch's remaining depth is ambiguous
  against `MaxExpansionDepth`). The delegate-safety invariant is unchanged: a self-referential
  navigation declared **with** a delegate is still never EF-included by the `$levels` path, whatever
  nested options ride along.

- **One-line `AddOhData()` companion registration (#285).** Each OpenAPI-companion package now ships
  a single convenience extension that is the canonical wiring recipe for that doc stack — implementors
  no longer need to know the transformer/filter/processor class names:
  - `EnGen.OhData.AspNetCore.OpenApi`: `OpenApiOptions.AddOhData(...)` registers both the operation
    and schema transformers; optional `authRequirements` / `securitySchemeId` (+ `requiredScopes`)
    parameters also wire the opt-in per-operation auth-requirements and security transformers
    (#219/#220).
  - `EnGen.OhData.AspNetCore.NSwag`: `AspNetCoreOpenApiDocumentGeneratorSettings.AddOhData(sp, ...)`
    registers both the operation and schema processors; same optional `authRequirements` /
    `securitySchemeId` (+ `requiredScopes`) parameters as the OpenApi variant.
  - `EnGen.OhData.AspNetCore.Swashbuckle`: `SwaggerGenOptions.AddOhData()` registers both the
    operation and schema filters (this package has no auth/security filters, so no auth parameters).

  Each method returns the options/settings object for chaining. The explicit per-class registration
  remains available as an à la carte alternative. Docs now lead with `AddOhData()` as the
  recommended registration. Additive API only — no runtime behavior change.

- **A startup `Warning` for a convention-discovered navigation the profile never declared (#440).**
  `public Customer? Customer { get; set; }` beside an `int? CustomerId` — the ordinary EF Core
  reference navigation — is discovered by `ODataConventionModelBuilder` and advertised in
  `$metadata`, but if the profile never declared it with `HasOptional`/`HasRequired`/`HasMany` then
  OhData's own navigation set does not contain it — so **this entity set will never serve that
  navigation**: `?$expand=Customer` is accepted and answers `200` with the navigation omitted, and no
  navigation route stands behind it either. A client generated from `$metadata` keeps asking for
  related data it can never receive, and nothing in any response says why.

  Closing that means *declaring* the navigation or hiding it with `Ignore()`. Both are valid and
  only you know which, so the framework names the disagreement and stops there — the same
  "a cost the framework can detect but must not decide" line the `#313` unbounded-`$expand` warning
  draws. **It is a warning, not a throw:** throwing would break startup for every adopter with a
  plain EF reference navigation on a profiled entity, with no migration but editing every profile.

  Emitted once per `(entity set, navigation)` at `MapOhData()`, gated on `ExpandEnabled` — with
  `$expand` off the entity set expands nothing at all, so the discrepancy is `$metadata`-only and the
  warning stays silent. Measured over this repository's full test suite: **24 distinct
  `(entity set, navigation)` hits against 358 distinct registered entity sets**, all true positives.

  > **The message states only what is still true, and it has been rewritten every time the framework
  > closed one of the consequences it named** — in the same commit as the fix, never after it. It has
  > never mentioned the pushdown disqualification `#322` fixed; the structural-property routes and
  > the `200`-with-`null` both came out with their fixes (below). What is left is not a defect report
  > at all but the advertise/serve disagreement itself, which no fix can remove because the framework
  > must not decide whether an undeclared navigation was meant to be exposed.
  > `Issue440UndeclaredConventionNavWarningTests` carries an explicit guard for each retired
  > consequence, so the message cannot drift back into describing behaviour that no longer exists —
  > which is the mistake `#313` stage 3 made and had to correct.

### Changed

- **Nested navigation values are now withheld from `PUT` and `PATCH` too, and the flag that
  governs it is renamed `AllowDeepWrites` (#457).** Two breaking changes in one commit; they are
  independent.

  > **⚠ BREAKING CHANGE 1 — behaviour.** With `AllowDeepWrites` at its default of `false`, a
  > nested navigation value in a `PUT` body is set to `null` before the `Put` handler runs, and one
  > in a `PATCH` body never enters the `Delta<TModel>` at all. Previously both reached the handler
  > regardless of the flag. A handler that *deliberately* relied on `PUT` carrying a graph now
  > receives `null` navigations; set `AllowDeepWrites = true` on that profile to restore it. `POST`
  > is unchanged.

  > **⚠ BREAKING CHANGE 2 — API.** `EntitySetProfile.AllowDeepInsert` (protected) and
  > `EntitySetDefaults.AllowDeepInsert` (public) are renamed `AllowDeepWrites`. Both old names
  > remain as `[Obsolete]` forwarding properties over the same storage, so existing code compiles
  > with a warning rather than an error and an assembly compiled against 1.5.0 keeps binding.
  > Projects building with `TreatWarningsAsErrors` will need the new name.

  **Why.** Deep insert (OData §11.4.2.2) is `POST`-only in the spec, so a `POST`-scoped flag was
  correctly named for what it did. **Deep update** — nested graphs in `PUT`/`PATCH` — is a
  *separate*, named 4.01 feature (§11.4.3.1) at Advanced conformance, and `docs/deep-insert.md` has
  declared it out of scope since 1.0.0. It was never enforced: System.Text.Json bound the nested
  values anyway, `PUT` forwarded them and `PATCH` bound them into the delta, so a handler doing
  `db.Update(model); SaveChanges();` on an update it never expected to carry a graph could persist
  part of one. This is enforcement of an already-documented scope decision, not an extension of the
  flag past its name — and once one flag governs both features, a name saying only "insert"
  describes one of the two.

  `PATCH` withholds rather than nulls, deliberately: `Delta<T>` is a change *set*, so a nulled
  navigation would still be named by `GetChangedPropertyNames()` and still written by
  `delta.Patch(existing)` — turning a graph the client sent into an unrequested relationship
  *clear*. It also keeps the delta consistent with the subsystem it feeds, since `Delta<TEntity>`
  and `DeltaMappingCompiler` handle scalars and structural properties only.

  All three routes share **one** strip set — the profile-declared navigations unioned with the
  EDM's (#461) — so a convention-discovered navigation the profile never declared is withheld on
  every verb, and the two halves cannot drift.

- **`MaxExpansionDepth` is now hard-capped at `6`; configuring more throws at startup (#328).**
  `EntitySetDefaults.MaxExpansionDepthCeiling` is a new public constant, and both configuration
  entry points — `WithDefaults(d => d.MaxExpansionDepth = N)` and a profile's own
  `MaxExpansionDepth` — throw `ArgumentOutOfRangeException` above it.

  > **⚠ BREAKING CHANGE.** A registration that configured `MaxExpansionDepth` above `6` no longer
  > starts: it throws `ArgumentOutOfRangeException` from `AddOhData` (defaults) or from the profile
  > constructor. It fails loudly at boot, not under load, and the default of `3` is unchanged — so
  > **a deployment that never set the knob is unaffected**, as is any value from 1 to 6. The
  > exception message carries the measured cost curve and what to do instead.

  **Why.** Relational query translation for a pushed nested projection is `Θ(3ⁿ)` in the nesting
  depth: EF Core re-translates each nested-collection subtree three times with no memoization, so
  every extra level triples the CPU spent *building* the query, before a single row is read. It is
  not a data-volume problem — it reproduces with no database, no connection and no rows, through
  `ToQueryString()` alone. Measured on a 16-node self-referential chain returning a ~6 KB body, one
  navigation per level:

  | depth | translation |
  |---:|---:|
  | 5 | 0.09 s |
  | **6** | **0.24 s** ← the ceiling |
  | 8 | 3.8 s |
  | 10 | 32 s |
  | 12 | 291 s |

  291 seconds is 4.9 minutes of single-core CPU for **one unauthenticated request with no body**,
  and growth is a clean ×3.0 per level — there is no cliff to stay below, only a curve to stop
  climbing.

  **Why 6 and not 3 (the default).** The blow-up is at 10+, not at 5: depth 5 costs ~90 ms, and
  `docs/query-options.md` and two of this project's own tests already use `MaxExpansionDepth = 5`.
  Capping at the default would have invalidated a documented configuration for a shape that is not
  expensive.

  **This is a mitigation, not a fix.** Nothing about `$levels=12` over a 16-node chain returning
  6 KB is unreasonable; it is expensive only because of upstream re-translation. The real answer is
  one flat query per level instead of one nested projection
  ([#430](https://github.com/en-gen/OhData/issues/430)). If you need a deeper graph today, fetch it
  as separate requests, or expand a **delegate-backed** navigation — those are loaded once per level
  by the expansion pipeline rather than composed into one nested projection, so they never pay the
  `3ⁿ` cost.

- **`MaxExpandTop` now defaults to `null` — no ceiling — instead of `1000` (#313).** The framework
  cannot know how large any given child collection is, so it no longer guesses: it ships the
  configuration point and lets the implementor set it. `1000` was an invented number, and a
  developer who never touched the knob was getting two behaviours decided by that guess.

  > **⚠ BREAKING CHANGE — this turns OFF two protections that were previously ON by default.** It is
  > breaking in the *permissive* direction, which is the easy direction to overlook: no request that
  > worked stops working, so nothing fails in staging and no test goes red. What changes is that a
  > registration which never set `MaxExpandTop` was silently getting a `1000`-entity ceiling, and no
  > longer is. **An explicit nested `$top` above the ceiling is no longer rejected, and the nested
  > `$count` materialization is no longer bounded.** If you were relying on the implicit default —
  > and by definition you were, if you never set the knob — set it explicitly:
  > `WithDefaults(d => d.MaxExpandTop = 1000)` reproduces the previous behaviour exactly. A startup
  > `Warning` names every navigation left exposed (see the `### Added` entry below), so this is
  > announced at boot rather than discovered under load.

  This is the first of **five** entries on #313 that together make bounding an `$expand` a decision
  the implementor makes rather than one the framework guesses. Read them in this order:

  1. **this entry** — the default moves to `null`, so the whole ceiling becomes opt-in;
  2. **the ceiling entry below** (`### Changed`) — once set, `MaxExpandTop` bounds the *bare*
     `$expand` too, at every depth;
  3. **the startup `Warning` + `ExpandPagingEnabled` entry** (`### Added`) — the framework names the
     exposure instead of guessing a number, and introduces the companion knob;
  4. **the continuation entry** (`### Added`) — what that knob does: pages a truly bare `$expand` with
     `Nav@odata.nextLink` and a `$skip`-only route, instead of rejecting it;
  5. **the `OhData.Client` entry** (`### Added`, #417) — the client half, without which the link in
     (4) would be invisible to OhData's own consumers.

  One limit spans all five and is stated once here so it is not rediscovered per entry: the bare-shape
  ceiling and its **continuation link** are computed in the expand pushdown's JSON shaping pass, which
  runs on the **`GetQueryable` collection route only**. `GET /{Set}({key})?$expand=Nav` is therefore
  never *linkable* — pre-existing (nothing bounded the bare shape on any route before #313), and for
  the reason #418 established: on that route the framework composed neither side of the child order,
  so a `$skip` continuation would silently skip and duplicate rows across the page boundary. The
  **explicit** nested-`$top` ceiling is unaffected and does apply there (#301).

  > **Superseded within this same release, on the *ceiling* half.** As written above — and as this
  > entry read until #418/#463/#464 landed — `GET /{Set}({key})?$expand=Nav` was also **unbounded**,
  > serving the whole related collection with `MaxExpandTop` set. It is not any more: a raw-served
  > collection navigation over the ceiling now returns `400` on **every** read path and at **every**
  > depth (see the `### Fixed` entries for #418 and for #463/#464/#466). What survives from the
  > original claim is only that the ceiling on that route is always a `400` and never a
  > `Nav@odata.nextLink`, whatever `ExpandPagingEnabled` says.

  **Both of those behaviours are now opt-in, and that is the intended contract, not a regression to
  work around.** On a registration that does not set `MaxExpandTop`:

  - `?$expand=Children($top=999999)` is **answered** rather than rejected with `400`. The nested-`$top`
    ceiling (#254 E1) does not exist until a value is set.
  - `?$expand=Children($count=true)` materializes the related collection **with no SQL row bound**.
    The `Take(ceiling + 1)` and its `ROW_NUMBER() OVER (PARTITION BY …)` window are not composed, and
    the emitted count is the full filtered count with no possibility of a ceiling `400`.

  Set it to get either back — `WithDefaults(d => d.MaxExpandTop = 1000)` reproduces the previous
  behaviour exactly (verified byte-identical in response body *and* emitted SQL across every
  `$expand` shape the suite covers), or set it per profile. A profile-level `MaxExpandTop = null`
  still means *inherit*, not "uncapped".

  **Strictly more permissive:** every request that succeeded before still succeeds, and nothing that
  worked stops working. Two shapes that used to return `400` now return `200`. The one visible
  side effect beyond that: for a bare `$expand=Nav($count=true)` with no nested `$top`/`$skip`, the
  child-key `ORDER BY` tiebreaker is no longer composed either — it existed only to make the bounded
  page deterministic, and with no bound there is no page to stabilize.

  A bare `$expand=Nav` was never bounded by this setting before this cycle. It is now — but only
  once the knob is set; see the #313 entry below, which is inert on a registration that leaves
  `MaxExpandTop` unset.

- **`@`-containing keys in an open type's payload are now treated as control information and ignored,
  where they used to be rejected with `400` (#398).** OData JSON Format 4.01 reserves `@` for control
  information — §4.5 for the leading form (`@odata.type`, `@odata.id`, `@odata.etag`) and §18 for the
  embedded per-property form (`Name@odata.type`, `Items@odata.count`) — so such a name is not a
  property name at all and cannot be a dynamic property. OhData previously applied the
  `odataIdentifier` grammar to every unmatched key on an open type, and since `@` is in no category
  that grammar admits, a conformant body carrying root or inline annotations was refused.

  **What changes.** Inside an open complex value, a member whose name contains `@` at *any* position
  is now skipped rather than policed, **and removed from the body before it is bound** — so it does
  not reach the dynamic-property container and is not echoed back. Skipping alone would have been
  worse than the `400` it replaces: `System.Text.Json` captures every unmatched member as extension
  data, and the read path holds bag keys to the same `odataIdentifier` grammar, so a stored `@` key
  would have made the row return `500` on every later read, permanently.

  ```jsonc
  // before: 400 InvalidBody   after: 201, annotation ignored, "tier" stored as the only dynamic key
  POST /odata/Things   { "Meta": { "@odata.type": "#Ns.T", "tier": 3 } }
  ```

  This follows `Microsoft.AspNetCore.OData` structurally rather than by imitation: that package
  contains no `@`-handling code at all, because ODataLib's JSON reader consumes control information
  before the deserializer runs, making an `@`-containing name incapable of becoming a dynamic
  property. OhData binds with `System.Text.Json`, which has no equivalent reader stage, so the rule
  is written down explicitly.

  **Unchanged, deliberately.** A declared member whose JSON name contains `@` (via
  `[JsonPropertyName]`) still binds — declared names are matched first. `@odata.bind` is still
  `501 Not Implemented` on every write route; that check runs earlier (see the `@odata.bind` entry
  below — the first cut of this change ran it on the collection `POST` only). And an `@` key one level
  *below* an accepted dynamic key is still `400`: down there the contract has run out, the whole
  subtree is opaque data stored and echoed verbatim, so there is no annotation to distinguish and an
  unaddressable key is still a stored fault.

- **`Ignore()`d properties are contained against an open type's dynamic-property bag (#398).**
  `Ignore()` works by *removing* a member from its `JsonTypeInfo`, and `System.Text.Json` extension
  data captures exactly what a `TypeInfoResolver` modifier removed — so on a type carrying both, a
  request body naming a withheld property would bind it *into the bag* and every later read would
  echo it back **under the withheld name**. Measured at raw `System.Text.Json` level.

  The withheld members' **JSON** names are now captured off the *pre-ignore* contract (never
  re-derived from the naming policy, which would be a hand-written re-implementation of
  `System.Text.Json`'s naming rules in the one place a naming bug becomes a disclosure) and threaded
  into both directions of the open-type path: a write naming one is dropped from the body before
  binding, and a container key spelled like one is a hard error on the way out.

  **This is a no-op today and is meant to be** — `Ignore(...)` names a root member of an *entity*
  type and a container lives on a *complex* type, so the two cannot meet until open types widen to
  entity roots. It ships ahead of that widening so the security-critical half is not landing at the
  same moment as its first real exercise. A write naming a withheld property is **silently dropped**,
  not `400`, matching what the closed-type path already does for unknown members and what
  `Microsoft.AspNetCore.OData` does (`ODataInputFormatter` deliberately clears ODataLib's
  `ThrowOnUndeclaredPropertyForNonOpenType`); the spec settles neither.

- **Two documented claims about open types were measured false and corrected (#398).** No behaviour
  change; both were load-bearing for the entity-root design.

  `IsNavVisibleInBaseOptions`' comment said a `[JsonIgnore]`d member is removed from
  `JsonTypeInfo.Properties`. Measured false on .NET 10.0.11 — such members stay in `Properties` with
  `Get`/`Set` `null` and a `ShouldSerialize` returning `false`, so the method's answer came from the
  `ShouldSerialize` branch rather than the fallthrough. Right answer, wrong reason, and the reason
  matters: the open-type modifier snapshots its declared-name collision set from that same
  collection, so a `[JsonIgnore]`d navigation *does* collide with a bag key rather than being
  shadowed by one.

  `docs/open-types.md` and `OpenTypeJsonOptions` said `Delta<T>` "has no mechanism" for routing an
  undeclared key into a dynamic bag, and named that as the blocker for open entity roots. Also false:
  `Delta<T>` has carried a `dynamicDictionaryPropertyInfo` constructor parameter all along, which
  `Microsoft.AspNetCore.OData` supplies from the EDM, and it was measured working — merge semantics,
  dictionary creation, and an `updatableProperties` allowlist that refuses a withheld name.

- **OData open types — dynamic property bags on complex types, ON BY DEFAULT (#389).** A complex type
  with an `IDictionary<string, object?>` member serializes and binds **flat**: its entries are
  siblings of the declared properties on the wire, never nested under the member's own name. Reads
  (collection `GET`, `GET` by key, navigation and property routes, `$expand` targets), writes
  (`POST`/`PUT`/`PATCH`, including property-route writes), and `$select=<container>` all round-trip
  undeclared keys, and `$metadata` declares `OpenType="true"` with the container omitted.

  **On by default because a complex type with a dictionary member *is* an open type**, and the CSDL
  OhData emits has always said so — `ODataConventionModelBuilder` marks it `OpenType="true"` and omits
  the member from the declared properties regardless. Leaving the payload nested made `$metadata` and
  the body disagree, and made conformance something you had to know the spec to ask for. It also
  diverged from `Microsoft.AspNetCore.OData`, whose `ODataResourceSerializer` reads the same
  annotation and appends dynamic properties flat with no opt-in flag at all.

  > **⚠ BREAKING CHANGE, and it is not detectable by diffing responses.** This alters the wire shape
  > **and the write binding** of every complex type in the model that has a dictionary member. An
  > existing client body `{"Meta":{"Bag":{"a":1}}}` stops binding `{"a":1}` to the `Bag` **property**
  > and starts binding a dynamic **key** named `Bag` holding that dictionary — the handler persists
  > `Bag = { "Bag": {"a":1} }`. **The response echo of the mis-bound value is byte-identical to the
  > correct one**, so this will not show up in a staging response diff or in a test that compares
  > payloads. Because of that, `MapOhData()` now logs **one warning per affected complex type** at
  > startup, naming the CLR type and the container member.
  >
  > **Opt out with `AddOhData(o => o.WithOpenTypes(false))`**, which restores the pre-#389 shape in
  > which the container is an ordinary nested declared property.
  >
  > **Detection recipe:** *do any of your complex types have an `IDictionary<string, object>`
  > member?* If none do, nothing changes — the registration's serializer options are not even derived,
  > no open-type write-body walk runs, nothing is logged, and every response (error responses
  > included) is byte-identical between the default and `WithOpenTypes(false)`.
  >
  > **One clause here was narrowed by #456, later in this release.** This read *"no write body is
  > buffered or walked"*. The **walk** half is still exactly true — the open-type key scan runs only
  > where the EDM really has an open complex type. The **buffering** half is not: `PUT` and the
  > navigation-`POST` create route now buffer the request body on **every** registration, so they can
  > scan it for `@odata.bind` (#456). That buffering is unconditional and unrelated to open types, it
  > is capacity-hint-clamped at 81,920 bytes, and what actually bounds it is your host's Kestrel
  > `MaxRequestBodySize` unless you set `MaxRequestBodyBytes` (#474). Responses stay byte-identical
  > between the default and `WithOpenTypes(false)` either way.

  **A bag key equal to one of the complex type's own declared property names now fails the request**
  with `500` and the OData error envelope, naming the type and the key in the log (previously: the
  declared property won, the key was dropped, and a warning was logged). Emitting both would produce a
  duplicate JSON property name, which every .NET reader tested resolves in the bag's favour, making
  the declared value unreachable. The spec does not decide between dropping and failing — CSDL 4.01
  §6.3/§9.3 say only that dynamic properties are "uniquely named", and JSON Format defers to
  RFC 8259's SHOULD — so this follows `Microsoft.AspNetCore.OData`, which throws
  `DynamicPropertyNameAlreadyUsedAsDeclaredPropertyName` in the same situation. The deciding argument
  is that the condition is **systematic, not per-row**: a client cannot cause it (a body key matching a
  declared name binds to the declared property and never reaches the bag), so the only source is
  server-side code, and if it fires at all it fires for every row carrying that key. **Accepted cost:
  a collection endpoint faults on the bad data rather than serving the remaining rows.** The match is
  ordinal, so a key differing only in case does not fail — see [docs/open-types.md](docs/open-types.md)
  for the recorded consequence of that.

  **A bag key that is not a valid `odataIdentifier` fails the same way** (`500` + the OData error
  envelope), from the same single inspection pass — whether it is a name at all (`""`,
  `"   "`, `null`) or a name that merely happens to be illegal (`"has space"`, `"@odata.type"`,
  `"kebab-case"`, a key of only format characters such as U+200B). Emitting any of them produces a
  property no conforming OData reader can address; previously they were emitted verbatim. **The read
  and write paths now hold bag keys to exactly the same grammar** — `400` on the way in, `500` on the
  way out — so a container's contents are fully validated rather than merely checked for emptiness. A
  client cannot cause this either, since such a key in a write body is already rejected with `400`, so
  this closes the server-side-data hole only. **This is a deliberate divergence from
  `Microsoft.AspNetCore.OData`, which silently skips the empty key and polices nothing else**
  (`ODataResourceSerializer.cs:820`, `if (string.IsNullOrEmpty(dynamicProperty.Key)) continue;`).
  Matching that skip would mean reintroducing the clone-and-substitute machinery this release
  deleted — the container getter now only *inspects* and returns the same reference, which is what
  removed the corner where a pre-seeded container silently lost every write — and resurrecting it to
  produce a *silent* drop is the wrong trade.

  > **Serialize-path cost, measured.** Full-grammar validation was previously declined here on cost
  > grounds. It is implemented with an ASCII fast path (`SearchValues<char>` +
  > `MemoryExtensions.ContainsAnyExcept`, one vectorized pass) that falls back to the rune-and-category
  > walk only when a non-ASCII character is present, plus a bounded process-wide cache of validated
  > **non-ASCII** keys — ASCII keys are cheaper to revalidate than to look up, so they never consult
  > it. In isolation that is **4.6 ns/key** against **16.9 ns/key** for the naive rune walk and
  > **5.4 ns/key** for the declared-name hash lookup already in the same loop. *In situ*, under
  > BenchmarkDotNet (`OpenTypeKeyValidationBenchmarks`), serializing a 1,000-row page carrying 20
  > dynamic keys per row costs this much more than the old whitespace-only check:
  >
  > | Key shape | Delta | Marginal |
  > |---|---|---|
  > | Repeating ASCII keys — the common case, 20 names reused on every row | **+4.0%** | 5.8 ns/key |
  > | 20,000 distinct ASCII keys | +6.1% | 9.0 ns/key |
  > | 20,000 distinct **non-ASCII** keys | +14.7% | 28.8 ns/key |
  >
  > Only the last row consults the validated-key cache at all, and it is the shape that saturates the
  > 1,024-entry table; reaching it implies a handler synthesising per-row non-ASCII key names rather
  > than a bounded, schema-like vocabulary. A model with **no** open complex type is unaffected —
  > none of this code runs for it. A scalar-bitmask variant measured slower still, so what cost there
  > is is inherent to the scan rather than to `SearchValues`.
  >
  > **Correction.** This entry previously quoted **+26% (4.28 ms → 5.41 ms), ~56 ns/key** here. That
  > figure came from a stopwatch harness, is **refuted** by the BenchmarkDotNet run above, and should
  > not be requoted — it overstates the common-case cost by roughly 6.5×. See
  > [docs/open-types.md](docs/open-types.md), which carries the same table and the same refutation.

  **No model changes are required, and none are accepted as a substitute.** Support is driven from the
  EDM: `ODataConventionModelBuilder` already infers the container and records it as a
  `DynamicPropertyDictionaryAnnotation`, which OhData reads at `MapOhData()` to mark exactly that
  member as `System.Text.Json` extension data on the registration's serializer options. The consumer's
  CLR model needs no `[JsonExtensionData]` (or any other) attribute, so a type published in a shared
  contract package works as-is. Nothing is matched by property name or convention.

  A dynamic key that is not an OData simple identifier (CSDL §4.1 `odataIdentifier` — empty, or
  containing `@`, `.`, whitespace or `-`: `@odata.type`, `Meta@odata.count`, `has space`) is rejected
  on write with `400` naming the key, since a bag key is persisted verbatim and echoed on every later
  read. The grammar is the ABNF's Unicode categories (`L`/`Nl` leading, plus `Nd`/`Mn`/`Mc`/`Pc`/`Cf`
  following), counted in code points, so non-Latin identifiers are accepted, as are both the NFC and
  NFD spellings of an accented one for any name within the 128-character limit (decomposition adds
  code points, so only a name already at the cap can differ between the two forms). The check covers
  every route that binds a body reaching a bag — `POST`/`PUT`/`PATCH`, the property-route writes, the
  navigation-`POST` create route, and each **action** parameter — and applies at every depth,
  including through arrays and through dictionary-valued declared members, since the value of a
  dynamic key is stored verbatim too. A container that `System.Text.Json` cannot use as extension
  data — most commonly a getter-only `public IDictionary<string, object?> Bag { get; } = new();` —
  fails at `MapOhData()` with a message naming the member and the fix.

  **Not supported, deliberately** (see [docs/open-types.md](docs/open-types.md)): entity-**root**
  dynamic containers, and `$filter`/`$orderby` over an *individual* dynamic key (the latter faults in
  Microsoft's query binder before any SQL is generated, so no query reaches the database). Note also
  that `PATCH` of a complex member **replaces** the whole complex value rather than merging it — the
  pre-existing behavior for any complex member, but open types widen its blast radius to the entire
  bag; the docs carry the read-modify-write recipe.

- **`MaxExpandTop` bounds nested `$top` and nested `$count` — default `1000` (#254).** New ceiling on
  `EntitySetDefaults` and `EntitySetProfile`, shaped exactly like `MaxTop` (positive integer or `null`
  for no ceiling; profile overrides the global default). The **root** entity set's resolved value
  governs at every nesting depth, the same rule `MaxExpansionDepth` follows. Two enforcement points:
  - an **explicit nested `$top`** above the ceiling (`?$expand=Children($top=5000)`) is rejected with
    `400 InvalidQueryOption` before any handler runs — at any depth, on all three collection read
    paths, and whether or not the navigation would have been pushed down (a delegate-backed navigation
    is rejected too, mirroring the root `MaxTop`, which rejects regardless of read path);
  - a **nested `$count`** materialization is bounded in SQL to `MaxExpandTop + 1` rows **at a
    projection leaf** (a level with no nested `$expand` of its own), and a related collection larger
    than the ceiling returns `400` rather than a truncated count — OData §11.2.4.2 requires
    `Nav@odata.count` to report the full filtered collection, so silent truncation is not an option.
    (At a level that also carries its own nested `$expand`, or anywhere inside a `$levels` recursion,
    the ceiling is enforced per level after an *unbounded* materialization instead of as a SQL `LIMIT`:
    windowing a collection *and* projecting a further collection out of it in the same query requires
    SQL `APPLY`/`LATERAL`, which not every provider supports. The check is always correct either way —
    never a truncated count. This entry originally said
    [#299](https://github.com/en-gen/OhData/issues/299) *"tracks tightening"* that
    unbounded-materialize-then-`400` cost and *"stays open"*; #299 has since been closed by
    **documenting** the caveat rather than by removing it, so the cost is unchanged and is now a
    recorded limitation rather than a tracked one. The narrower nested-`$top`-with-`$count` case *was*
    fixed, separately, by #334 — see its `### Fixed` entry.)

  Also (unreleased, so not itself a change from any published version, but worth calling out
  explicitly): nested paging (`$skip`/`$top`) with no nested `$orderby` now appends the child entity's
  key as a deterministic tiebreaker before paging (mirroring the existing root-level stabilization) —
  this **reorders** the expanded array from whatever order the provider happened to return to explicit
  child-key order whenever a nested `$top`/`$skip` is present without an accompanying `$orderby`.

  **BEHAVIOR CHANGE:** requests that previously returned `200` now return `400` — a nested `$top`
  above `1000`, or a nested `$count` over a related collection with more than `1000` rows. Set
  `WithDefaults(d => d.MaxExpandTop = N)` to raise it, or `= null` to restore the previous unbounded
  behavior (a profile-level `MaxExpandTop = null` means *inherit* the resolved default instead — it
  does not itself remove the ceiling). An **omitted** nested `$top` without `$count` stayed unbounded
  in #254 — that hole is closed by #313 below, in this same unreleased cycle, on any registration
  that sets the knob.

- **`MaxExpandTop`, once set, now bounds the bare `$expand` too — every collection expand level, at
  every depth (#313).** #254 left the single most common `$expand` shape uncovered: a collection level
  carrying neither a nested `$count` nor an explicit nested `$top` composed no SQL `Take` and got no
  size check at all, so `?$expand=Children` over a 5,000-row related collection returned all 5,000
  rows even with a `MaxExpandTop` of `1000` configured. It now returns `400 InvalidQueryOption` with
  the same "exceeds the maximum of N entities — narrow it with a nested `$filter`" message the
  `$count` and `$top`/`$skip` breaches already used.

  **Read this together with the `MaxExpandTop` default change above: the knob is unset by default, so
  this entry describes an opt-in, not a flip.** On a registration that never sets `MaxExpandTop`
  nothing here happens — no SQL bound, no key tiebreaker, no `400`, no `ROW_NUMBER()` window.

  **The rule is broader than "bare", and the blast radius is worth reading literally:** the ceiling
  applies to any collection expand level with **no** nested `$count` and **no** explicit nested `$top`,
  *whatever else it carries*. So `Children($select=…)`, `Children($orderby=…)`, `Children($filter=…)`
  and `Children($skip=N)` are all in scope — none of them bounds the collection either. An explicit
  nested `$top` still wins (it is validated against the ceiling up front and windows the collection
  itself, so no default bound is composed beside it).

  Enforcement mirrors the shape split #254 established. At a projection **leaf** the bound is pushed
  into SQL as `Take(MaxExpandTop + 1)`, so a breach is detected without transferring the collection —
  EF Core translates that inside a collection projection as the standard top-N-per-group
  `ROW_NUMBER() OVER (PARTITION BY … ORDER BY …)`, the same form an explicit nested `$top` has always
  produced. At a level with its own nested `$expand`, and at **every level of a `$levels=N`
  recursion**, it stays a post-materialization check in the JSON pass (`APPLY`/`LATERAL`, per #298).
  `$levels` mattered specifically: `Children($levels=1)` is a spec-equivalent restatement of
  `$expand=Children` — identical response bodies — so leaving it unchecked would have made the whole
  ceiling bypassable with one parameter.

  > **What setting `MaxExpandTop = N` now buys you, and costs you.** Three things arrive together and
  > cannot be taken separately: (1) any request whose expanded collection exceeds `N` related
  > entities answers `400` instead of `200`; (2) nested collections come back in **child-key order**
  > (the deterministic tiebreaker #254 added for nested paging now applies to the bare shape too),
  > where before the order was whatever the provider yielded; and (3) the leaf query plan gains the
  > `ROW_NUMBER() OVER (PARTITION BY …)` window in place of a plain `LEFT JOIN`. Measured end to end
  > over HTTP, both arms differing only in this setting (`BareExpandCeilingBenchmarks`, 20 parents,
  > every arm under the ceiling): **1.02× at 5 children per parent (1.811 → 1.848 ms), 1.37× at 50
  > (2.844 → 3.898 ms), 1.37× at 500 (24.662 → 33.737 ms)**; allocation is unchanged (≤1.01×). Near
  > free where related collections are small, ~1.4× where they are large — which is exactly where the
  > unbounded materialization it removes is worth removing. Leaving the knob unset keeps all three
  > off and is byte-identical, in response body *and* emitted SQL, to the pre-#313 behavior.

  **What the ceiling still does not reach, deliberately.** A **delegate-backed** navigation is never
  in the engaged pushdown tree, so `MaxExpandTop` does not bound it and #313 does not close its
  denial-of-service surface — its size stays entirely the handler's responsibility (a nested
  `$top`/`$skip` on one is separately `400`, #294). Bounding it would mean the framework silently
  truncating a collection the developer's delegate deliberately returned, which is a direct weakening
  of the delegate-safety invariant; the right fix is a *contract* change — a delegate overload taking
  `(key, skip, take, ct)` — and is not part of this cycle. Two other costs stay open and are recorded
  rather than hidden: at a level with children, and anywhere inside a `$levels` recursion, the ceiling
  is enforced only **after** an unbounded materialization ([#299](https://github.com/en-gen/OhData/issues/299)),
  so it is a data ceiling everywhere and a materialization ceiling only at a projection leaf.

  **And one reach limit that was a gap rather than a decision — closed later in this same release.**
  This ceiling — the *bare* shape — is computed in the pushdown's JSON shaping pass, so **as shipped
  in this entry** it applied on the **`GetQueryable` collection route only**, as does #254's
  nested-`$count` ceiling. `GET /{Set}({key})?$expand=Nav` composes no SQL bound (it expands through
  the delegate/batch-handler pipeline) and so returned the whole related collection with
  `MaxExpandTop` set — pre-existing rather than introduced here, since nothing bounded the bare shape
  on any route before this change. The **explicit** nested-`$top` ceiling is a pre-handler validation
  and does reach that route (#301), as it does `GetAll` and Priority-1.

  > **Superseded by #418, #463 and #464, all in this release.** A **second** mechanism now
  > size-checks every raw-served collection navigation against the same ceiling — at every level of
  > the `$expand` tree, on every read path (`GetAll`, Priority-1, a non-EF `IQueryable`,
  > `GET /{Set}({key})`, a branch the pushdown declined, and every level below a raw-served parent) —
  > and always as a `400`, never a link. So the reach limit described above no longer exists: what
  > remains is the *split* between the two mechanisms, which is about how the collection was
  > **loaded**, not which route was called. The pushdown's pass bounds the *fetch* and can page it;
  > the raw check bounds the *data* after your handler already materialized it. See the `### Fixed`
  > entries for #418 and for #463/#464/#466, and the reach table in
  > [docs/query-options.md](docs/query-options.md), which is the authority.

- **#298 / #300 fixes: two silent-degrade regressions in the #254 pushdown, both now fixed.** Post-merge
  adversarial review of #297 found that (a) `$count=true` on a pushed expand level that ALSO carried a
  nested `$expand` (e.g. `Books($count=true;$expand=Chapters)`) composed an untranslatable SQL shape
  (windowing a collection **and** projecting a further collection out of it — `APPLY`/`LATERAL`) that
  degraded the whole request to `200` with the affected data silently empty; and (b) inside a `$levels`
  recursion, an explicit nested `$skip`/`$top` hit the identical untranslatable shape, so
  `$expand=Children($levels=2;$skip=1)` also silently came back empty under a `200`. Both are fixed by
  no longer composing the untranslatable SQL bound in either case — the `MaxExpandTop` ceiling (case a)
  and the `$skip`/`$top` window (case b) are applied instead in the JSON pass, exactly the trade the
  `$levels` count path already made. **FAIL LOUD:** additionally, any OTHER expand-pushdown shape the
  provider cannot translate now returns `400 InvalidQueryOption` instead of silently degrading to
  EDM-only under a `200` — this closes the general class of bug #298/#300 belonged to, not just the two
  specific shapes. `docs/query-options.md` and #301 (the same `MaxExpandTop` nested-`$top` ceiling,
  previously missing on `GET /{Set}({key})`) are also part of this fix.

### Fixed

- **The write-path body scanners now read a request body the way the deserializer reads it
  (#511).** `PUT` and the navigation-`POST` create route buffer the body and scan the raw UTF-8
  twice — once for `@odata.bind` (#456) and once for "which navigations did this body name?" (#506)
  — before handing the same bytes to `JsonSerializer.DeserializeAsync`. Both scans built a
  **default** `Utf8JsonReader` while the deserializer reads with the registration's serializer
  options, and both deliberately swallow their `JsonException` so the deserializer stays the sole
  author of the malformed-body message. Every configuration the two readers disagreed about
  therefore turned a scan into a silent *"nothing found"* — a fail-open. Measured against a live
  host:

  | Request | before | after |
  |---|---|---|
  | `PUT` — `EF BB BF` + body naming a navigation | 200, graph **unstripped** | 200, stripped |
  | `PUT` / nav-`POST` — `EF BB BF` + `x@odata.bind` | 200 / 201, annotation discarded | 501 |
  | `PUT` — naming policy `SnakeCaseLower`, nav `BackOrders`, body `back_orders` | 200, **unstripped** | 200, stripped |
  | `PUT` — host `ReadCommentHandling.Skip`, navigation named after a comment | 200, **unstripped** | 200, stripped |

  A UTF-8 BOM needs no configuration at all to hit: `Utf8JsonReader` throws at its first byte while
  `JsonSerializer` and `JsonDocument` both skip it — which is also why the collection `POST`, which
  parses through `JsonDocument`, answered `501` to bytes `PUT` answered `200` to. Both scanners now
  take their reader from one helper that skips a leading BOM and derives comment handling, trailing
  commas and max depth from the serializer options, exactly as `DeserializeAsync` does internally.

  The navigation-name lookup table was keyed by the EDM name (`[JsonPropertyName]` ?? CLR name —
  policy-free by design, OData §4.4) plus the CLR name; the deserializer matches the
  *policy-converted* name. camelCase differs from the CLR name only by case, so a case-insensitive
  table hid it; `SnakeCaseLower` and `KebabCaseLower` do not. Rather than adding the policy as a
  third key, the table's primary key now comes from the **serializer contract itself**
  (`JsonTypeInfo.Properties[].Name`), so it cannot drift from the binder again. The EDM and CLR
  names remain as aliases, and on a default host the table is unchanged.

  No behaviour changes on a default host, and the deserializer remains the only component that
  reports a malformed body.

- **Three ways the error path was less trustworthy than the success path (#493, #494, #495).** All
  three were measured on the pre-fix tree, and all three are things a consumer meets under load
  rather than in a test.

  **A handler's `TaskCanceledException` escaped the error envelope entirely (#493).** The group
  filter declined the whole `OperationCanceledException` family, on the theory that a cancellation
  means the client went away. That is a fact about the *request*, not about the exception type —
  and `TaskCanceledException` is what `HttpClient` throws on its **own** timeout, so every handler
  awaiting an outbound dependency was affected. Measured with a `GetAll` throwing one on a request
  that was never aborted: `500` with an **empty body**, no envelope, and **no OhData log at all** —
  which is exactly the failure mode that filter exists to eliminate, on arguably the most common
  outbound-dependency failure there is. The filter now also asks whether the request really was
  aborted, so a genuine client disconnect is still left to ASP.NET Core (unchanged, and still
  pinned) while a dependency timeout gets the envelope and the log it always should have.

  **An `$expand` infrastructure fault was reported to the client as `400` (#494).**

  | Fault during an `$expand` request | before | after |
  |---|---|---|
  | Connection-pool exhaustion (`SqlConnection.Open`) | **400** `InvalidQueryOption`, logged at `Debug` | **500** + envelope, logged |
  | Disposed `DbContext`, or "a second operation was started on this context" | **400** `InvalidQueryOption` | **500** + envelope, logged |
  | A query the provider genuinely cannot translate | 400 `InvalidQueryOption` | 400, now logged at `Warning` |

  The three `$expand`-pushdown sites caught `InvalidOperationException`/`NotSupportedException`/
  `ODataException` around the whole materialization and answered *"could not be translated by the
  underlying data provider"*. The premise — that a real infrastructure fault could only arrive as a
  `DbException` or a `TimeoutException` — is false: SqlClient reports pool exhaustion as a plain
  `InvalidOperationException`, and `ObjectDisposedException` **derives** from it. So under load an
  `$expand` request told client retry logic *not to retry* while the same request without `$expand`
  correctly `500`d, and at `Debug` the operator saw a spike of client errors and no server-side
  signal at all.

  Fixed by asking **when** rather than **what**, since the type cannot separate the two populations:
  the provider's translation phase (`GetEnumerator()`) is separated from its materialization phase
  (the first `MoveNext()`), and only the translation window yields the `400`. Everything from the
  first row onward propagates to the envelope as a logged `500`.

  > **⚠ BREAKING CHANGE.** If your client treats a `400` from an `$expand` request as *"this query is
  > malformed, do not retry"*, the conditions in the first two rows above now produce `500`. That is
  > the point — those conditions are transient and retrying is correct. Genuinely untranslatable
  > queries keep their `400`, byte-identically.

  **Every `Dictionary`-shaped OData envelope was serialized under the *host's* `HttpJsonOptions`
  (#495), and the scope is wider than the issue named.** Two consequences, and the non-faulting one
  is the worse of the pair:

  - **Shape.** System.Text.Json applies `DictionaryKeyPolicy` to dictionary keys, and OhData's
    envelope keys — `error`/`code`/`message`/`target`, `@odata.context`/`value`/`@odata.count`/
    `@odata.nextLink` — are contractual identifiers, not names a policy may rewrite. Measured with a
    host `DictionaryKeyPolicy` of `SnakeCaseUpper`: `{"ERROR":{"CODE":"NotFound",…}}` on **every**
    error response the framework produces, and `{"@ODATA.CONTEXT":…,"VALUE":[…]}` on **every
    collection GET**, the service document and both bound-operation envelopes. That ships a parseable
    body no OData client can read, under a `200` or a `4xx`, with nothing anywhere reporting a
    problem. (`JsonObject` responses — `GetById`, the nav-single route — were never affected; STJ
    writes a node's member names verbatim.)
  - **Fault.** A throwing host `JsonConverter<string>` took the group filter's **own** `500` envelope
    with it: an empty, envelope-less `500` with no log. The last-resort error path was itself the
    thing that failed.

  The division is now **OhData owns the envelope's names; the host owns value formatting** — #252's
  split, one level out. A default host is byte-identical, asserted for all seven envelope shapes
  against strings captured from the pre-fix tree and pasted verbatim; a host that never set a
  `DictionaryKeyPolicy` does not even allocate a second options instance.

  > **⚠ BREAKING CHANGE, for hosts that set `JsonSerializerOptions.DictionaryKeyPolicy`.** Your error
  > bodies, collection envelopes and service document change bytes: the envelope keys stop being
  > rewritten by your policy. If you were parsing the rewritten form, parse the OData form instead.
  > Every other host is unaffected.

- **Delta mappings were validated against a model of `Delta<TEntity>` that is not the model
  `Delta<TEntity>` implements (#479, #480).** One root cause, two shipped symptoms, and both target
  the most ordinary EF Core entity shapes there are:

  | Entity shape | before | after |
  |---|---|---|
  | A `[NotMapped]` (or `[IgnoreDataMember]`, or unmarked-under-`[DataContract]`) mapping target | startup OK, then **every write to it silently lost** — no exception, no log | **`MapOhData()` throws**, naming the property and why |
  | Only a *protected* parameterless ctor beside a public parameterized one — the standard EF entity — plus positional records and abstract types | startup OK, then `MissingMethodException` **on every request** | **`MapOhData()` throws**, naming the type |

  `DeltaMappingCompiler` checked only for a public setter. `Delta<T>` additionally requires a public
  **getter** and applies its own ignore rules, and `Delta<T>.Reset` calls
  `Activator.CreateInstance(entityType)` on every `Create`. A target outside `Delta<T>`'s tracked set
  makes `TrySetPropertyValue` answer `false` — which both `Create` overloads **discarded**.

  Closed structurally rather than case by case: the compiler now **asks a real `Delta<TEntity>`**
  which properties it tracks, so there is no transcription of Microsoft's predicate left to drift
  from. And both `Create` overloads now **throw** on a `false` instead of dropping it — after startup
  validation a `false` means the validation is wrong, so it is an invariant assertion rather than an
  error path, and a `500` in the OData envelope beats a `200` that silently persisted a partial write.

  > **⚠ BREAKING CHANGE — at startup, which is where you want it.** An app whose delta profiles map
  > onto any of the shapes above **fails to start** where it previously started and then misbehaved.
  > The message names the property or type and what to do about it. The new rejection is one-way
  > *narrowing*: OhData's own public-setter rule still runs ahead of the tracked-set check, so nothing
  > that compiled before reaches `UpdatableEntityProperties` differently, and the documented "an
  > ignored or unmapped property cannot be patched onto the entity even by a hostile request body"
  > boundary is untouched.

  A different-typed `new`-shadowed entity property now fails at startup too — incidentally, because
  `Delta<T>`'s own bookkeeping rejects it. Pinned by a test rather than deliberately fixed.

- **Three capabilities the server advertised and did not serve (#465, #467, #468).** Same shape each
  time: a claim in generated documentation or in CSDL that no code path honours.

  **`$search` on the Priority-1 route (#465).** The `GetODataQueryable` collection route appended
  `", $search"` to its OpenAPI description whenever the profile had a `Search` handler, and never
  invoked it — there is no `$search` leg in that route body at all. Invoking it is not coherent with
  the Priority-1 contract either: on the `GetQueryable`/`GetAll` paths `Search` *replaces the source*
  and the framework applies the remaining options on top, which it can because it owns the pipeline
  there; Priority-1 hands the whole `ODataQueryOptions` to the profile, so honouring `$search` would
  mean bypassing the profile — dropping `$filter`/`$orderby` on exactly the requests carrying
  `$search`, and routing around any row-level scoping the handler applies.

  > **⚠ BREAKING CHANGE.** `GetODataQueryable` **plus** a `Search` handler is now **refused at
  > startup**, with a message pointing at `options.Search` inside `GetODataQueryable` — where
  > `$search` on that path has always been the profile's own business. If you have that combination
  > today, `$search` is not being applied to anything; move it into the handler.

  **`OhDataQueryOptionsMetadata` fields meant two different things (#467).** The record is attached to
  five route shapes — three collection GETs, `/$count` and `GetById` — and read by all three companion
  packages, so one field with two meanings produced the same wrong document three times.
  `$top`/`$skip` were documented on metadata *presence* alone, so `GetById` and `/$count` advertised
  paging they both drop; `/$count` set `CountEnabled: true` to mean *"this route **is** a count"*
  while its consumers read it as *"this route documents the `$count` option"*; and `/$count`'s
  `FilterEnabled` mirrored the profile flag although its `GetAll` fallback answers `400` for any
  `$filter`. Fixed once at the metadata attachment — one site, three consumers — so the packages stay
  in agreement by construction.

  > **⚠ BREAKING CHANGE — generated API documents change.** `Microsoft.AspNetCore.OpenApi`, NSwag and
  > Swashbuckle output all change for the affected routes: `$top`/`$skip` disappear from `GetById` and
  > `/$count`, `$count` disappears from `/$count`, and `$filter` disappears from a `/$count` whose
  > source cannot apply it. Regenerate any client built off those documents.
  > `OhDataQueryOptionsMetadata` also gains a required `TopSkipSupported` parameter on its primary
  > constructor — deliberately required, because the correct value differs per route and any default
  > would silently restore the defect for a construction site that omitted it. The 1.5.0
  > seven-parameter constructor and its `Deconstruct` are **retained** (both forwarding
  > `TopSkipSupported: true`, which is what that overload meant before #467) so an assembly compiled
  > against 1.5.0 keeps binding. The record is constructed only by OhData; the companion packages only
  > read it.

  **`$metadata` claimed every unbound function import was in the service document (#468).** The
  `IncludeInServiceDocument="true"` flag is `Microsoft.OData.ModelBuilder`'s default and OhData never
  set it, while the service document was hand-built from the profile list and contained entity sets
  only. For a **parameterized** import that claim is not merely false but invalid CSDL — §13.6
  reserves the document for imports invocable by name alone. Parameterized imports now clear it;
  parameterless ones keep it **and are really listed**; and the service document is now derived from
  the same EDM container `$metadata` is written from, so the two generators cannot diverge again.
  Action imports are never listed — not GET-addressable, and CSDL has no such attribute for them.

  > **⚠ BREAKING CHANGE — two wire changes and two new startup failures.** The service document now
  > lists parameterless unbound function imports alongside entity sets, and `$metadata` no longer
  > carries `IncludeInServiceDocument` on parameterized ones. Separately, **`EdmValidator.Validate` is
  > now called from `MapOhData()`** — it had never been called anywhere in the assembly, and neither
  > `CsdlWriter.TryWriteCsdl` nor `CsdlReader.TryParse` enforces these rules, so invalid CSDL reached
  > the wire and broke only the consumers that validate it (codegen tools). **An app whose EDM is
  > invalid now fails to start**, with the offending construct named. Measured when it was first wired
  > in, it catches this issue's own violation in four fixtures.
  >
  > It also caught a second long-shipping one: `AddFunction`/`AddAction` with an un-named lambda
  > resolves to a compiler-generated name (`<Register>b__0_1`) that is not a legal OData identifier,
  > which `CsdlReader` accepts and so had been shipping invalid `$metadata` silently. That is now
  > rejected **earlier and better** — `AddFunction`/`AddAction` validate the resolved name themselves
  > and throw `ArgumentException` naming the parameter and the remedy (*pass an explicit `name`*),
  > because a generic CSDL failure quoting `<Register>b__0_1` at `MapOhData()` tells a developer
  > neither the cause nor the fix. They call the shipped identifier validator rather than transcribing
  > the grammar a third time.

- **One serialized entity could poison the navigation-suppression cache for the process lifetime,
  turning an entity set into a permanent `500` (#482).** `GetNavSuppressedOptions` populated its
  per-type map only for the type it was *called with*, while the `System.Text.Json` resolver modifier
  consulted that map at contract-**resolution** time and gave up when the type had no entry. STJ
  caches a resolved contract forever, so any entity type STJ merely reached *transitively* froze
  **un-suppressed** — permanently resurrecting #343 for that entity set.

  Measured through the shipped code: serve one entity whose open-type dynamic bag holds a live
  instance of another entity type, and the next read of *that* entity set throws
  `JsonException: A possible object cycle was detected` on an ordinary parent/child fixup graph —
  rendered as `500` on a **plain GET with no query string**, on every request, forever. Two further
  edges were measured to produce the same `500`: an `object`-declared CLR member, and a **complex**
  type carrying an entity-typed member (the convention builder models that as a navigation *on the
  complex type*, which entity-scoped suppression never visited).

  Fixed by removing the ordering dependency entirely: one walk of the schema at `MapOhData()` maps
  every EDM entity type to its CLR type, and the modifier then computes each type's suppression set
  itself, as a pure function of the type. Reaching is no longer how a type gets its set, so there are
  no edges left to enumerate — and priming it before the first request also closes a
  concurrent-first-request race.

  Two things fall out of the fix. The CLR→EDM lookup is now keyed off the model builder's own
  annotation rather than `FindDeclaredType(clrType.FullName)`: measured, with `mb.Namespace =
  "Custom.Ns"` that lookup returns `null` for *every* type, so #343's runtime-type union was a silent
  no-op on any renamed schema. And an **EDM-renamed** navigation (`HasMany(…).Name = "Kids"`) produced
  a navigation the name lookup could not see at all, so it was suppressed on **no** route,
  order-independently — now closed by reading the builder's own property annotation alongside the name
  lookup. Cost is ~0.9 µs per EDM entity type at startup (0.44 ms for a 400-type model); the
  per-request path is still two dictionary probes. Known and out of scope: `$expand` of an EDM-renamed
  navigation is still spliced as an empty array.

- **The deep-write strip no longer nulls navigations the request body never mentioned (#506).**
  `PUT` regressed on this in [#504](https://github.com/en-gen/OhData/pull/504) (merged, never
  released); the collection `POST` had done it since 1.0.0. Measured against a live host, with a
  model carrying `public List<Child> Kids { get; private set; } = new();` — the standard EF
  encapsulation shape, which the convention model builder maps as a navigation:

  | Request | before | after |
  |---|---|---|
  | `PUT /P476Parents(1)` — body `{"id":1,"title":"t"}` | 200, handler received `Kids == null` | 200, handler received the constructor's empty list |
  | `PUT /P476Parents(1)` — body naming `kids` | 200, `Kids == null` | 200, `Kids == null` *(unchanged)* |

  Two causes. `deepWriteNavPropsToStrip` filters on `p.SetMethod is not null`, and
  `PropertyInfo.SetMethod` returns **non-public** accessors — so a private-setter navigation
  `System.Text.Json` could never bind was in the strip set. And the strip itself was unconditional:
  `navProp.SetValue(model, null)` for every navigation, whatever the body contained.

  The filter is deliberately **left wide** — narrowing it to a public setter would exempt a
  `[JsonInclude] { get; private set; }` navigation that STJ binds perfectly well, which opens a
  deep-write hole rather than closing one. What changed is the *gating*: a navigation is stripped
  only when the body named it. Matching uses the same resolution the binder used
  (case-insensitive, `[JsonPropertyName]`-aware) and reads the **root** object's members only.
  `PATCH` already behaved correctly — its skip fires while iterating the body's own properties —
  and is unchanged.

  The strip exists to stop a handler that does not expect a graph from silently persisting part of
  one. If the body sent no graph there is nothing to prevent, and nulling anyway destroys state the
  handler would otherwise have had: a handler diff-syncing `model.Kids` against the loaded entity
  saw `null` instead of an empty list — a `NullReferenceException` in `.Count`, or a "null means
  clear the relationship" misread.

  Also corrects the comment above the filter, which claimed *"properties without a public setter
  can't be deserialized into by STJ in the first place, so they're excluded — nothing to strip."*
  The filter excludes no such thing; the strip was written trusting that it did.

  > **⚠ BREAKING CHANGE — the collection `POST` only, and separate from the `PUT` regression
  > above.** `POST /{EntitySet}` has nulled *every* navigation on the deserialized model since
  > 1.0.0, regardless of the body. It now leaves untouched the ones the body did not name. A `Post`
  > handler that relied on "the framework always hands me `null` navigations" now receives whatever
  > the model's own constructor put there. Bodies that **do** carry a nested graph are unaffected on
  > every verb. `POST` is fixed alongside `PUT` rather than after it because leaving one verb gated
  > and the other unconditional would reintroduce exactly the per-verb write-path divergence this
  > milestone spent ten PRs removing.

- **`If-Match` is now enforced on the `$ref` and navigation-`POST` write routes, and compared per
  RFC (#478).** `CheckETagAsync` was called from five places — entity `PUT`/`PATCH`/`DELETE` and
  the two structural-property write handlers. Every other state-changing route the framework owns
  **discarded** a received `If-Match` and performed the write with a success status. Measured on a
  `TestServer` against the real pipeline, replaying a stale ETag after an out-of-band change:

  | Request with stale `If-Match` | before | after |
  |---|---|---|
  | `PUT /Parents(1)` *(control)* | 412 | 412 |
  | `POST /Parents(1)/Children/$ref` | **204**, `addRef` ran | **412**, delegate not called |
  | `DELETE /Parents(1)/Children/$ref` | **204**, `removeRef` ran | **412**, delegate not called |
  | `PUT /Parents(1)/Friend/$ref` | **204**, `setRef` ran | **412**, delegate not called |
  | `POST /Parents(1)/Children` | **201**, `post` ran | **412**, delegate not called |

  RFC 9110 §13.1.1: an origin server **MUST NOT** perform the method when a received `If-Match`
  evaluates false. Silently discarding the precondition is the exact failure mode conditional
  requests exist to prevent — the client believes it performed a checked write. Concretely: a
  client reads a parent's ETag, then conditionally unlinks and relinks a relationship; both calls
  succeed against a parent that changed underneath. That is a lost update on relationship state.

  The gate runs **before** the request body is read and before the handler delegate is invoked, so
  a refused write provably mutates nothing. It is **not** atomic — no transaction is opened, and
  the TOCTOU window between the check's `GetById` and the delegate's write is unchanged. See
  [docs/etags.md](docs/etags.md#concurrency-note).

  Two comparison bugs on the pre-existing routes are fixed with it:

  - `If-Match: W/"<current-etag>"` returned **200**. RFC 9110 §13.1.1 requires **strong**
    comparison and §8.8.3.2 says a weak validator can never participate in one, so it must be
    `412`. Weak entries are now dropped rather than unwrapped; a weak entry alongside a matching
    strong one still succeeds on the strong one.
  - `If-None-Match: "<current-etag>"` on a write returned **200**. §13.1.2 makes the condition
    false, so it is now `412`. `If-None-Match` keeps **weak** comparison (so `W/"<current>"` *does*
    match there — the two headers deliberately differ), and when both headers are present
    `If-Match` wins outright per §13.2.2.

  > **⚠ BREAKING CHANGE.** A client that sends `If-Match`, `If-None-Match`, or a `W/`-prefixed
  > validator to any of these routes and relies on it being **ignored** now gets `412`. Requests
  > that send no conditional header are byte-identical, and a profile without `UseETag` is
  > unaffected. OhData never emits weak ETags, so a `W/` validator can only have been client-built.

  Rejecting weak validators is a **deliberate divergence from `Microsoft.AspNetCore.OData`**, not a
  case of matching it. MS emits weak ETags unconditionally and compares them ignoring weakness, so
  `If-Match: W/"..."` is accepted there; that pairing is jointly non-conformant with §13.1.1, and
  the project's "work with MS conventions" policy does not extend to reproducing a non-conformance.
  It is safe here specifically because OhData has no weak-ETag path at all. Two existing tests that
  pinned the old unwrapping behaviour were inverted with this change.

  MS ships **no** automatic `If-Match` enforcement on any route — it is always the controller
  author's job, which works there because the author owns the route. OhData owns these routes and
  hands the delegate only `(TKey, child, CancellationToken)`, so "follow MS" would have meant
  nobody enforcing the header and nobody being able to. That asymmetry is what makes this a
  framework defect rather than a documentation gap.

  Bound and unbound **actions** are a deliberate, documented exclusion: the target resource of
  `POST /Set(key)/Action` is the action-invocation resource (Protocol §11.5.4), which has no
  representation and therefore no entity tag, whereas OData §11.4.6 defines a `$ref` write as a
  modification of the addressed entity itself.

- **`MaxExpandTop` was enforced on one substrate at one depth, and `$levels` served one level off the
  raw path (#463, #464, #466).** Three symptoms of one defect: a bound whose enforcement was sited
  where the *implementation* happened to converge rather than where the *rule* applies. Measured with
  a ceiling of `2`:

  | Request | before | after |
  |---|---|---|
  | `GET /Authors(2)?$expand=Books($expand=Chapters)` | **200**, every chapter served (checked at depth 1 only) | **400** |
  | `GET /Authors?$expand=Books` on `GetAll`, Priority-1, or a non-EF `IQueryable` (`$search` produces one) | **200**, every book, no bound at all | **400** |
  | `?$expand=Children($levels=3)` on any raw-served path | **200**, **one** level, silently — while the explicit nested spelling served three | **200**, three levels, byte-identical to the explicit spelling |
  | `?$expand=Children($levels=2)` on a **delegate-backed** navigation | **200**, one level, silently | **400** |

  #463 and #464 are the reach halves. The single-entity ceiling never recursed into nested clause
  items and resolved its navigation set once at startup from the root profile; the collection-route
  ceiling and its #313 continuation link both lived behind the EF Core pushdown's JSON shaping pass,
  so on any non-EF source the configured DoS bound *silently did not exist*. Both are now one
  mechanism, running where all five read routes already converge and resolving the treatment **per
  level** exactly as the descent it measures does. Always a `400`, never a trim-and-link, for #418's
  reason unchanged: the framework composed neither side of the child order on a raw-served
  collection, so a `$skip` continuation would silently skip and duplicate rows across the page
  boundary.

  #466 is the `$levels` half, and it needed nothing loaded to fix — the rows were already in the
  graph the handler returned. It is **not** implemented on the delegate substrate and is rejected
  there instead: serving level 2 means running a delegate at depth 2, and *which* delegate is not
  settled on that substrate (the pushed path stays on the URL-named set all the way down, #318;
  Model B resolves depth ≥ 2 from the exact-EDM-type union, #293 — both frozen). Picking one would be
  an owner decision about gate resolution, so `$levels` resolving above 1 on a delegate-backed
  navigation follows #294's precedent and returns `400`. `$levels=1` restates a bare `$expand` and is
  unaffected.

  > **⚠ BREAKING CHANGE — on opted-in registrations only.** `MaxExpandTop` is `null` by default, so an
  > unconfigured registration is byte-identical. **With a ceiling set**, an over-ceiling raw-served
  > collection goes `200 → 400` on `GetAll`, Priority-1, a non-EF `IQueryable`, and at depth ≥ 2
  > anywhere. Independently of the ceiling, `$levels` resolving above 1 on a delegate-backed
  > navigation goes `200` (one level) `→ 400`.

  Corrected with it: `EntitySetDefaults.MaxExpandTop`'s XML doc claimed it bounds *"every collection
  `$expand` level"* once set — false on four reachable paths — and `docs/query-options.md`'s reach
  table is now split by **how the collection was loaded**, not by which route was called. The residue
  is stated rather than implied: on a raw-served expansion the nested window is still not *applied*
  (#352). One more claim was falsified during review and corrected in the same commit — a navigation
  merely *declared* with a delegate but reached under a raw-served parent is **never invoked**, so its
  rows are the parent handler's own and the ceiling does bound them; the #313 O6 exemption turns on
  the delegate having **run**, not on how it was declared.

- **A `PATCH` body could move the entity key, and two profiles over one CLR model type unioned their
  allowlists (#454, #458).** Independent defects, one commit; both are cases of two mechanisms
  disagreeing about the same thing.

  **`PATCH` could rewrite the key (#454).** The key-mismatch guard and the delta-building loop
  consulted different sets: the guard matched on the key's **CLR** name and stopped at the **first**
  case-insensitive hit, while the loop resolved **every** body property case-insensitively *and*
  `[JsonPropertyName]`-aware into a last-writer-wins `Delta<T>` with no key exclusion.

  | `PATCH /Set(1)` body | before | after |
  |---|---|---|
  | `{"Id":1,"Id":999}` | **200**, key rewritten to `999` | **400** (target `key`) |
  | `{"id":1,"Id":999}` | **200**, key rewritten to `999` | **400** (target `key`) |
  | `{"code":"ZZ"}` with a `[JsonPropertyName("code")]`-renamed key | **200**, key rewritten — the guard could not see it at all | **400** (target `key`) |

  Both halves now resolve through the same helper against the same CLR property: every occurrence is
  validated, and the key is **never written into the delta**, so immutability is structural rather
  than a consequence of the guard having happened to see every occurrence. `PUT` is structurally
  immune — it deserializes to `TModel` first, so STJ has already collapsed duplicates to the value the
  handler will receive — and two tests exist solely to fail if `PUT` is ever refactored toward
  raw-`JsonElement` parsing.

  **Divergent allowlists over one CLR type are now refused at startup (#458).** `FilterProperties`/
  `OrderByProperties`/`SelectProperties`/`ExpandProperties` are applied to the shared per-CLR-**type**
  `EntityTypeConfiguration<TModel>`, and `ModelBoundQuerySettings` is keyed by type while OhData's
  configuration surface is keyed by entity set. Two profiles over the same model type therefore
  **unioned** their allowlists, and each entity set silently accepted filtering, sorting, selecting
  and expanding on properties its own profile deliberately withheld — **measured for all four
  options, in both registration orders**, each withheld property going `400 → 200` the moment the
  sibling was co-registered, with responses byte-identical to the correctly-gated case and therefore
  invisible to an adopter.

  This cannot be scoped down: per-entity-set model-bound settings do not exist. In
  `Microsoft.OData.ModelBuilder` 2.x the fluent `Filter`/`OrderBy`/`Select`/`Expand`/`Count`/`Page`
  API is declared only on `StructuralTypeConfiguration<T>` and `PropertyConfiguration`, every
  `GetModelBoundQuerySettings` overload in `Microsoft.AspNetCore.OData` resolves off an
  `IEdmStructuredType` rather than a navigation source, and the capability-vocabulary annotations that
  *can* sit on an entity set are never read by the query validators.

  > **⚠ BREAKING CHANGE.** `MapOhData()` now throws `InvalidOperationException` on a divergent pair,
  > mirroring the check `Ignore()` already performs. Legitimate multi-set-per-type registrations are
  > untouched: two unset (permissive) allowlists agree, a sibling whose capability flag is off makes
  > no call at all, an `AdvancedConfigure` override owns the EDM and is not compared, and separate
  > registrations build separate models. If you hit this, the two profiles were never both being
  > honoured — pick one allowlist per model type, or split them into separate registrations.

- **`OhData.Client`: `@odata.nextLink` was followed cross-origin with your credentials attached, and
  a throwing captured value silently became `eq null` (#459, #460).**

  **The `nextLink` walker leaked credentials and could not terminate (#460).** The walker builds a
  fresh request for a URL named in a **response body**, and `HttpClient` attaches its
  `DefaultRequestHeaders` — `Authorization` among them — to it. That is not a redirect, so
  `HttpClientHandler`'s cross-origin credential stripping never runs: a response-body injection
  (compromised server, caching proxy, MITM on a plaintext hop) hands your bearer token to a host of
  its choosing. Separately, `while (NextLink is not null)` had no cap, so a server echoing the same
  link forever drives the walk unboundedly until the process dies.

  Two additive options on `OhDataClientOptions`:

  - **`FollowCrossOriginNextLinks`, default `false`.** A link whose origin — scheme, host, port —
    differs from `HttpClient.BaseAddress` fails the read with `InvalidOperationException` and **no
    request is made**. A *relative* link is untouched (`HttpClient` resolves it against
    `BaseAddress`, which makes it same-origin by construction), and an unparseable link is passed
    through so the request layer keeps producing its own error. Refusing was chosen over stripping
    `Authorization` and following anyway, because the class of the problem is *default headers*, not
    that one name — an `X-Api-Key` in the same collection leaks identically.
  - **`MaxNextLinkHops`, default `10_000`** (values below 1 rejected at configuration time). A
    termination guarantee, not a paging policy: the default is set where no legitimate run reaches it
    — a million entities at a server page size of 100 — and `int.MaxValue` is effectively unlimited.
    The cross-origin opt-in does not disable it; trusting a service's origins says nothing about
    whether its paging terminates.

  > **⚠ BREAKING CHANGE.** A client walking a `nextLink` that points at a **different origin** than
  > `BaseAddress` now throws instead of following it. Set `FollowCrossOriginNextLinks = true` to
  > restore the old behaviour — and note that with the flag on, the credential *does* reach the
  > foreign origin, which is pinned by a test deliberately so anyone redefining the flag has to look
  > at it. A walk longer than `MaxNextLinkHops` now throws rather than running forever.

  **A captured value whose getter throws became `eq null` (#459).** `FilterTranslator`'s evaluator
  returned `null` for two different conditions — *"the value is null"* and *"evaluating it threw"* —
  because every reflection and compile/invoke path ended in `catch { return null; }`. So
  `Filter(x => x.Name == src.Bad)` with a throwing getter emitted `Name eq null`: **a query the
  caller never wrote**, executed against the server, returning rows where `Name` is null, with no
  exception anywhere. The three outcomes are now distinguishable — evaluated (the value may
  legitimately be null, unchanged), threw (`NotSupportedException` with the cause attached, no
  literal), and nothing attempted because the expression reads a lambda range variable (the existing
  *"method is not supported"* diagnostics, unchanged).

  > **⚠ BREAKING CHANGE.** A filter over a captured value whose getter throws now throws
  > `NotSupportedException` instead of quietly issuing a different query. Two smaller changes ride
  > along at the compile fallback: the interpreter/JIT retry now covers **compilation only**, so a
  > user getter with side effects is no longer invoked twice, and `TargetInvocationException` is
  > unwrapped so the message names the real cause.

- **Per-type configuration was resolved by the *declared* type while both serializers resolve the
  *runtime* type — so a derived row leaked `Ignore()`d members (#462, #343, #469).** One defect
  class, three symptoms, all reproduced on a **plain GET with no query string** over an ordinary EF
  Core TPH shape:

  | Symptom | before | after |
  |---|---|---|
  | An inherited `Ignore()`d property on a derived row (#462) | **served** on the derived row while the base row in the same page withheld it — on the collection route *and* `GetById`. Disclosure. | withheld on both |
  | A navigation declared only on the **derived** EDM type (#343) | emitted inline with **no `$expand` naming it**; two derived instances referencing each other through one returned **500** (`A possible object cycle was detected`) | suppressed, per JSON Format §4.5.1/§11.2.4.2 |
  | An **inherited** navigation the client explicitly `$expand`ed (#469) | **silently dropped** from every derived row, while base rows in the same page kept theirs | served |

  The batched collection path hands System.Text.Json an `object`-declared element and the
  single-entity path calls `SerializeToNode(value, value.GetType(), …)`, so **any** per-type map keyed
  by the exact CLR type misses a derived instance. #293 was the first instance of this class; these
  are the rest. All four affected sites now route through one shared base-chain walk whose entire
  callable surface is `Resolve` + `IsEmpty` — there is no dictionary left in scope for a fifth site to
  key by exact type, and a reflection test asserts that. Two deliberately different policies: withheld
  names **union** up the chain (a withheld-name set is a disclosure boundary, so a derived profile's
  own `Ignore()` set must not shadow its base's), while a single-valued configuration such as a
  `new`-shadowed dynamic-property container is nearest-wins.

  #469 was found by the shared fixture and named in neither issue: `PropertyInfo`s were compared with
  `!=`, which also compares `ReflectedType`, and for an inherited navigation on a derived instance the
  two sides disagree — measured on .NET 10.0.11, the EDM-name lookup reports `ReflectedType =
  RtDerived` while STJ's `AttributeProvider` reports `RtBase`. Shipping #343 without it would have
  left a derived entity unable to serve any expanded navigation at all.

  > **⚠ BREAKING CHANGE — the wire shape of derived rows changes, in three directions at once.** On a
  > polymorphic entity set: an inherited `Ignore()`d member **disappears** from derived rows (that is
  > the disclosure fix, and the reason this is not optional); a navigation declared only on a derived
  > type **disappears** unless expanded — and it cannot be expanded, because the `$expand` clause
  > binds against the declared type; and an inherited navigation you **did** `$expand` now appears
  > where it used to be missing. Non-derived shapes are byte-identical, asserted against baselines
  > captured from the pre-fix build and pasted verbatim.

- **A nested `$top`/`$skip` on a delegate-backed navigation reached through a delegate-less parent
  was accepted and never applied (#320).** #294 closed the silent-ignore hole one level up; this is
  the same hole one level deeper. The rejection fired only when the expand walker reached the
  navigation **itself** as delegate-backed, and the walker's raw-served branch does not recurse — so a
  delegate-backed navigation reached only through a delegate-less parent's materialized graph was
  never classified. Measured: `?$expand=Bs($expand=Cs($top=1))` returned `200` with all three `Cs`
  rows and the `Cs` delegate **never invoked** — byte-identical to the same request with no `$top` at
  all.

  The rejection is now resolved from the navigation's Model B **treatment** rather than from where the
  walker happened to reach, using the same resolution pair the real descent uses so the scan cannot
  disagree with the descent it stands in for. It also moved above the blank branch, so a navigation
  whose candidate sets disagree about whether it is delegate-backed now rejects — with its own message
  naming the disagreement — instead of silently emptying *and* ignoring the window.

  > **⚠ BREAKING CHANGE, in the fail-loud direction.** `?$expand=Parent($expand=DelegateNav($top=N))`
  > goes `200` → `400`. It cannot turn a *honoured* request into a `400`: a nested window is honoured
  > only on a branch the SQL pushdown windowed, and pushdown engages a branch only when every level is
  > raw-served, so wherever the scan finds a delegate the branch was certainly never pushed. Two
  > controls pin that direction. It costs nothing on the hot path — the scan is skipped entirely
  > unless a pure clause walk finds a nested window in the subtree.

  **Deliberately still open, and commented at the site:** a raw-served navigation on a branch that was
  never pushed down at all (in-memory `GetAll`, non-EF `IQueryable`, or a branch deferred for a
  structural reason) still ignores its nested `$top`/`$skip` silently. Rejecting it would make the
  answer depend on whether pushdown happened to engage — an internal optimisation decision invisible
  to the client — and would `400` requests that are honoured today. It needs its own decision
  alongside #352's scheduled retirement of this whole rejection.

- **`GET /{Set}({key})?$expand=Nav` served the whole related collection with `MaxExpandTop` set
  (#418).** The bare-`$expand` ceiling lived behind the collection route's JSON shaping pass, and
  `GetById` expands through a pipeline whose raw-served branch is deliberately a no-op. Measured with
  a ceiling of `2` over five books:

  | Request | before | after |
  |---|---|---|
  | `GET /Set?$filter=Id eq 1&$expand=Books` | 200, two books + `Books@odata.nextLink` | unchanged |
  | `GET /Set(1)?$expand=Books` | **200, all five**, no link, no `400` | **400** `InvalidQueryOption` |

  **A `400` rather than a trim-and-link, and the reason generalizes.** A continuation needs three
  things and only two exist here: the parent key (in the URL) and a continuation route (already
  registered when both knobs are set). The third is a *shared order* between page 1 and the
  continuation. On the collection route the framework composes both sides — it appends the child-key
  `ORDER BY` to page 1's SQL and the continuation composes the same one. On `GetById` it composes
  neither: the child rows arrive already materialized inside the `TModel` your handler returned, in
  that handler's order (measured: a plain `LEFT JOIN` with no `ORDER BY` over the child). Re-sorting
  the serialized array does not reconcile them — a JSON compare is not the column's collation. A link
  over a disagreeing order silently skips and duplicates rows across the page boundary, which is worse
  than the `400` and invisible to the client. `ExpandPagingEnabled` therefore does nothing on this
  route, and the message says so.

  **Every nested shape is covered, not only the bare one.** Measured: `GetById` applies **no** nested
  option to a delegate-less navigation — `$filter`, `$orderby`, `$select`, `$skip`, `$top` and
  `$count` are each silently ignored there while the collection route honours all six. A ceiling that
  fired only for the bare shape would be bypassable by appending any one of them. (That silent-ignore
  is a separate, larger defect and is not fixed here.)

  > **⚠ BREAKING CHANGE — on registrations that set `MaxExpandTop`.** `GET /{Set}({key})?$expand=Nav`
  > over the ceiling goes `200` → `400`. Inert on the shipping default (`MaxExpandTop = null`) and for
  > every under-ceiling response, pinned byte-identical against the pre-fix build.

  **What this does not buy: a materialization bound.** The collection is loaded by your `GetById`
  handler before the framework sees it, so this is a data ceiling only (the #299 trade). The
  worst-case load for `GET /{Set}({key})?$expand=Nav` is unchanged and equals whatever that handler
  eager-loads; a `GetById` that does not `Include` the navigation serves `[]` and never trips it.

- **A shared `ODataQueryContext` returned intermittent `400`s to valid requests under concurrent load
  (#426).** The factory cached one `ODataQueryContext` per entity set for the process lifetime, under
  a comment reading *"Both are read-only after construction"*. That comment was false and the
  consequence was production-reachable: **any** OhData server under concurrent load intermittently
  answered `400 InvalidQueryOption` to a valid request — **including one with no query string at
  all**.

  `ODataQueryOptions`' constructor **writes** to the context it is handed, and `Initialize` reads
  `context.Request` back off that shared field instead of the constructor's own `request` parameter.
  Two requests in flight against one context race on it, and the loser dereferences a different
  request's `HttpContext` — concurrently with its owner, or after it has been recycled — throwing
  `NullReferenceException` out of `DefaultHttpContext.get_RequestServices`, which #402's deliberately
  broad catch then relabels `400`. The constructor opens with
  `Contract.Assert(context.RequestContainer == null)`: Microsoft stating outright that the type is
  per-request.

  | 16 threads × 2,000 iterations | failures per 32,000 |
  |---|---|
  | shared context (before) | 43 / 31 / 16 / 89 — every one an NRE from `get_RequestServices` |
  | fresh context (after) | **0** |

  `TryBuildQueryOptions` now takes the `IEdmModel` and builds the context itself, so none of the five
  read-route call sites can pass a shared one. Cost, measured: **+263 to +362 ns and +448 B per
  options build** — under 1% of the time and 2.7% of the allocation of the cheapest end-to-end read
  route, and under 0.05% / 0.4% of a collection page. The `ODataQuerySettings` instances cached on the
  same line **stay** cached; that was checked against the `Microsoft.AspNetCore.OData` source rather
  than assumed.

  The `ConcurrencyTests` flakiness recorded as #384 was this, not a test defect.

- **Two more places the OData error envelope did not hold (#402, #396).** Same shape both times: a
  failure occurring *outside* the scope the guard covers.

  **`GET /Set?$skiptoken=` returned `500` (#402).** Microsoft rejects an empty `$skiptoken` from
  `SkipTokenQueryOption`'s own constructor — while `ODataQueryOptions` is still being built — with an
  `ArgumentException`, and the routes' only guard was `catch (ODataException)` over the whole handler
  body. So it escaped to the group filter as a **client-reachable `500`**; it is now `400`
  `InvalidQueryOption`.

  The fix is not a longer catch list. `TryBuildQueryOptions` narrows its `try` to *exactly* the
  construction and widens the catch to everything but a cancellation — broad is right **there and only
  there**, because nothing inside that `try` but option parsing, so any failure is a statement about
  the request URL. Narrowing first is what makes it safe: the surrounding whole-handler `try` also
  contains the data fetch, where a broad catch would relabel a database outage as a client error. All
  five construction sites are covered. `ODataException` keeps its message pass-through, so the eight
  empty-value cases (`$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$search`/`$apply`/`$compute`) are
  byte-identical; anything else gets a generic message and the real exception is logged at `Warning`.

  **Operation routes bypassed the envelope entirely (#396).** A minimal-API `IResult` executes
  **after** the endpoint-filter chain unwinds, so a fault while the result serializes is outside the
  group filter's `try` — and the status line and headers are already committed. Measured pre-fix on a
  bound function whose result faults during serialization: `Request finished … - 200` plus a
  **truncated body**, with nothing logged. That is worse than an envelope-less `500` — a success
  status with a malformed body defeats client-side error handling completely — and it is generic to
  any throwing converter, cyclic graph, or faulting getter.

  Every route now materializes its response body **before returning**. Most already did; the four that
  handed a raw CLR graph to `Results.Json` (both unbound operation results, the bound-operation DTO
  branch, and the structural-property read envelope) now serialize to UTF-8 in the handler and return
  a write-only result. Byte-identical by construction — same declared `TValue`, so the same
  `JsonTypeInfo` resolves — with `Content-Length` now explicit. Measured cost: the common small-DTO
  shape gets **faster** (0.74×, the async state machine dominates a tiny payload); 189 KB costs +18%,
  9.4 MB +74%.

- **Nested `$expand` serialization was never batched, and a hot-path attribute lookup was uncached
  (#337, #338 — the rest of #333).** Two performance defects below the root page, shipping alongside
  the #334 nested-`$top` fix below.

  `SerializeBoundedCollection`'s batched fast path was gated on *"the clause keeps no navigation"*,
  which no `$expand` request can satisfy by construction — so **~99% of an `$expand` payload's bytes
  were still serialized one entity at a time** (1 batched call over ~1 KB versus 1,000 individual
  calls over ~82 KB on the measured shape). Nested sibling sets now route through the batched path.
  Separately, the navigation JSON-key resolver ran an uncached `GetCustomAttribute` on the hot path,
  reached ~3,000 times per request on a 1,000-row three-navigation `$expand`; it is now memoized on
  `(PropertyInfo, options)` — the computation's exact dependency set, since `PropertyInfo` alone would
  collide across registrations with different naming policies.

  **Output is byte-identical**, verified by capturing raw response bodies across a 34-URL matrix
  (plain/nested/selected `$expand`, `$levels`, renamed and `[JsonIgnore]`d navigations, EF
  self-referential and in-memory cyclic graphs, delegate-backed navigations, empty/single/
  null-containing collections, polymorphic elements) before and after. Adversarial review of the first
  cut found and fixed a real regression before merge: batching hands STJ an `object`-declared element,
  which triggers polymorphic re-entry and leaked a `"$kind"` discriminator into every `$expand`ed
  polymorphic collection — an arbitrary STJ key in an OData payload, which additionally vanished under
  `$select`. The polymorphic assertion that now guards it was mutation-tested against declared-type
  batching and fails there.


- **A non-string `@odata.id` on a `$ref` write returned `500` instead of a `400` envelope (#455).**
  `POST`/`PUT` to `/{Set}({key})/{nav}/$ref` called `JsonElement.GetString()`, which throws
  `InvalidOperationException` for every `ValueKind` except `String` and `Null`. The handler catches
  only `JsonException` and `FormatException`, so a body that is well-formed JSON and merely
  semantically wrong escaped to the group filter as a generic `InternalServerError` — telling the
  client the server broke when in fact the client sent something invalid. The framework
  hand-deserializes write bodies precisely to guarantee the opposite; this route was the one
  exception.

  | body | before | after |
  |---|---|---|
  | `{"@odata.id": 123}` | `500 InternalServerError` | `400 BadRequest` |
  | `{"@odata.id": true}` | `500` | `400` |
  | `{"@odata.id": {"uri":"…"}}` / `["…"]` | `500` | `400` |
  | `{"@odata.id": null}` | `204`, delegate called with `""` | `400`, delegate not called |
  | `{"@odata.id": "…"}` | `204` | `204`, unchanged |

  > **⚠ Behaviour change beyond the crash.** An explicit `"@odata.id": null` never threw — it
  > returned `null`, the `?? ""` turned it into an **empty entity-id**, and that was handed to the
  > profile's `addRef`/`setRef` delegate under a `204`. A link request reported success while naming
  > no entity at all. OData §11.4.6.2 wants the entity-id of the entity to link; a null member names
  > none, which is the same client error as omitting the member — already a `400`.

- **`@odata.bind` was silently accepted on `PUT`/`PATCH`/nav-`POST`/property-writes for most
  registrations (#456).** `@odata.bind` is documented non-support and answers `501 Not Implemented`
  — but only the collection `POST` ran that check unconditionally. Every other write route deferred
  it into `PrepareWriteBody`, which returns early unless the registration's EDM actually declares an
  open complex type. On the majority of registrations the check therefore never ran, and the
  annotation was accepted with `200`/`201` and **discarded**: the client asked to bind a
  relationship, got a success, and nothing happened.

  ```
  PATCH /Orders(1)  {"Name":"x","Lines@odata.bind":["Lines(5)"]}    200  ->  501
  PUT   /Orders(1)  {…,"Category@odata.bind":"Categories(5)"}       200  ->  501
  POST  /Orders(1)/Notes  {…,"Order@odata.bind":"Orders(1)"}        201  ->  501
  PUT   /Orders(1)/Name   {"value":{"x@odata.bind":"…"}}            200  ->  501
  POST  /Orders           {…,"Category@odata.bind":"…"}             501  ->  501  (unchanged)
  ```

  The check moved *above* `PrepareWriteBody`'s open-type gate, which covers `PATCH`, the
  structural-property writes and every bound/unbound action parameter. `PUT` and the navigation-`POST`
  create route needed more: on the non-open path they never call `PrepareWriteBody` at all — they
  stream the request body straight into the deserializer — so each now buffers the body once and
  scans the raw UTF-8 with a `Utf8JsonReader` before handing the same buffer to the **same**
  `DeserializeAsync(Stream)` overload as before. That last detail is deliberate: reading those two
  bodies through `JsonDocument` instead would change how a malformed body is worded (`Path: $`
  versus not), which is the byte-identity regression `OpenTypeDefaultOnIsByteIdenticalTests` exists
  to catch. Malformed-body, wrong-type and depth-limit responses are unchanged on every route.

- **Deep insert's strip set missed a navigation the profile never declared (#461).** The write-side
  twin of #446. `deepInsertNavPropsToStrip` was built from the profile-**declared** navigation names,
  so a navigation the OData convention builder discovered but the profile never declared with
  `HasOptional`/`HasRequired`/`HasMany` was not in it — System.Text.Json bound the nested value and
  handed it to `Post` intact, **with `AllowDeepInsert` at its default of `false`**. A handler doing
  `db.Add(model); SaveChanges();` then persists rows nobody opted into.

  ```
  POST {"Id":1,"Note":"r","CustomerId":7,"Customer":{…}}
    undeclared navigation  before: handler received Customer  ->  after: handler received null
    declared navigation    before: handler received null      ->  after: handler received null
  ```

  The most ordinary shape there is — **a profile that declares no navigations at all** — was fully
  exposed, and nothing on the wire showed it: the `201` echo omits every EDM navigation either way.
  The fix is #446's rule applied to one more set: the EDM is the authority on what is a navigation.
  `#440`'s startup warning, which said only that the entity set *"will never serve it"*, now also
  states what the write path does with it.

- **A sibling profile's delegate silently disabled `$expand` paging — and the unbounded-`$expand`
  warning — on the set that still served the navigation raw (#421).**
  `#313`'s two root-level resolvers (`ResolveExpandPagingNavigations`, which drives both continuation-
  route registration and `Nav@odata.nextLink` emission, and the `WarnUnboundedBareExpand` startup
  diagnostic) resolved `ServeRaw` over the cross-profile **union** — every profile exposing the same
  EDM entity type. The root **read path** beside them resolves over the URL-named set alone, which
  the [frozen Model B spec](https://github.com/en-gen/OhData/issues/293) settles as correct
  (*"Root (depth 1): KEEP as-is"*, and why [#415](https://github.com/en-gen/OhData/issues/415) was
  refuted). Both resolvers now use that same candidate set.

  Measured, with a delegate-less `BeAuthors` and a delegate-backed `BeDelegatedAuthors` over the same
  EDM type (`MaxExpandTop = 3`, `ExpandPagingEnabled = true`):

  | request | before | after |
  |---|---|---|
  | `GET /BeAuthors?$filter=Id eq 1&$expand=Books` (5 books) | `400` — forever, no escape hatch | `200`, 3 books + `Books@odata.nextLink` |
  | `GET /BeAuthors(1)/Books?$skip=3` | `404` (no route registered) | `200`, the remaining 2 books |
  | `GET /BeDelegatedAuthors(1)/Books` | the delegate | the delegate, unchanged |
  | startup, `MaxExpandTop = null` | **no** unbounded-`$expand` warning | one warning naming `'BeAuthors'` / `'Books'` |

  The `400` was the proof the old behaviour protected nothing: it is only reachable *after* the rows
  have been materialized and counted, so `/BeAuthors?$expand=Books` was already serving those books
  raw. Withholding the route removed the paging escape hatch without removing an exposure, and left
  `ExpandPagingEnabled` inert for that entity set with no diagnostic.

  **The delegate-safety invariant is unchanged**, because it never depended on the union: a
  navigation *this* profile declares with a delegate resolves to `RunDelegate` on its own candidate
  set and still gets no continuation route and no link. And the aligned candidate set is byte-for-byte
  the one the root read path uses, so the pageable set is now exactly *{navigations
  `GET /{Set}?$expand={Nav}` already serves raw} ∩ {pageable}* — the continuation composes off the
  parent profile's own `GetQueryable` under that set's own authorization, so it can expose no row the
  `$expand` beside it does not already expose to the same caller. Pinned directly by
  `TheContinuationServesNoRowTheRootExpandDoesNotAlreadyServeRaw`.

  **Both call sites got the same answer**, which is what the code already required of them: the
  diagnostic describes what a root `$expand` materializes, and the route is the escape hatch from
  that same materialization's ceiling, so they must be resolved from the same candidate set or they
  drift. Only registrations with **two or more entity sets over one EDM entity type** are affected;
  every single-profile registration is byte-identical, and `ExpandPagingEnabled` is still `false` by
  default. Two of the three flipped tests were asserting the withheld route on brownfield fixtures
  (`BeAuthors`, and the `$levels` suite's `LvNodes`/`LvSecureNodes` pair); they now assert the paging.

- **`$expand` of a convention-discovered navigation the profile never declared returned `null` under
  `200` (#440).**
  Only a *declared* navigation is ever loaded — `pushdownExpandNavs` is built from the profile's own
  navigation set — but the member was still serialized, so a client that asked for related data got
  `"Customer": null` (or `[]`) beside a `CustomerId` that pointed at a row which exists.

  | request | before | after | declared control (before *and* after) |
  |---|---|---|---|
  | `GET /{Set}?$expand=Customer` | `{"Id":1,…,"CustomerId":7,"Customer":null}` | `{"Id":1,…,"CustomerId":7}` | `{…,"Customer":{"Id":7,"Name":"C7"}}` |
  | `GET /{Set}(1)?$expand=Customer` | `{…,"Customer":null}` | `{…}` | *(n/a — control loads it)* |

  **The navigation is now omitted.** OData JSON Format v4.01 §8.3 defines the inline representation
  of a navigation property as the representation of an *expanded* one, so a null single-valued
  navigation is the positive statement that the relationship is empty — a statement this server never
  evaluated. §8.1 gives the non-expanded representation instead: the navigation link, which under
  `metadata=minimal` is computed and therefore not written at all. Omission is the payload in which
  every assertion is true, and it is what `OmitUnexpandedNavigations` already does for every
  navigation a request did not expand.

  **Not a `400`, deliberately**, despite the framework's fail-loud convention (#294/#402/#405). That
  convention rejects an option the *client* got wrong, or one the server parsed and could not honour.
  This is neither: the request is valid against the `$metadata` this server published, and the gap is
  the *server's* configuration. Rejecting would charge the client for the developer's omission on the
  ordinary `public Customer? Customer { get; set; }` shape — turning a currently succeeding request
  into an error for every adopter who has one. The loud channel for a configuration gap is startup,
  and the `#440` warning above is it.

  Implemented where the decision already lives: `ExpandLevelAsync`'s `ServeRaw` branch now separates
  its two populations, using a new flag `ResolveNavTreatment` reports alongside its (unchanged)
  treatment — "did any candidate at this level route *or* declare this navigation", the complement of
  Model B's own frozen *"a candidate that neither routes nor declares the nav has no opinion on it"*
  clause. **No `NavTreatment` value moves**, the pushdown gate reads only the treatment, and
  `Issue322ModelBClassificationTests` pins the whole decision table through it.

  > **One shape is deliberately excluded, and one behaviour changes as a cost.**
  > A `$levels` expand of an undeclared *self-referential* navigation is resolved through
  > `BuildLevelsNavBinding`, which does not consult the profile's navigation set — so
  > `?$expand=Children($levels=2)` genuinely is pushed to SQL and genuinely does load. Omitting it
  > would delete fetched data, so navigations pushed that way keep their value (pinned, and verified
  > to fail if the exclusion is removed).
  > The cost: on a **non-EF** `GetQueryable`/`GetAll` whose in-memory graph already holds the related
  > object, a bare `?$expand=Cust` used to echo it and now omits it, while the **declared** control on
  > the same model still serves it. That divergence is the rule, stated: *declaring* a navigation is
  > what makes it servable. It buys a bigger consistency — the same profile and the same request used
  > to answer `null` on an EF source and real data on a `List<T>`, so the answer depended on the query
  > provider. It no longer does. `Issue322NonEfProjectionUnificationTests` carries both provenances in
  > the same assertions, so the divergence is visible rather than argued.

- **Structural-property routes were registered over a convention-discovered navigation the profile
  never declared — including writes (#440).**
  A profile's `StructuralProperties` is "every public readable CLR property **minus every
  profile-declared navigation**", and route registration read it directly. So an undeclared
  navigation got the full property-route surface, aimed at a navigation:

  | request | before | after | declared control (before *and* after) |
  |---|---|---|---|
  | `GET /{Set}(1)/Customer` | `204` | **`404`** | `404` |
  | `GET /{Set}(1)/Customer/$value` | `400` | **`404`** | `404` |
  | `PUT /{Set}(1)/Customer` | `204` | **`404`** | `404` |
  | `PATCH /{Set}(1)/Customer` | `400` | **`404`** | `404` |
  | `DELETE /{Set}(1)/Customer` | `204` | **`404`** | `404` |

  The writes are the sharp end: each built a one-property `Delta<TModel>` over a **navigation**
  member and handed it to the profile's `Patch` handler. Nobody opted into that, and a profile that
  *declared* the same CLR member had no such routes at all — so two profiles over one model exposed
  different route tables purely by declaration provenance.

  The fix subtracts the **EDM's own** navigation names from the set route registration iterates —
  the same subtraction #322 applied to the projection's structural member set, for the same reason.
  It is applied at the registration site rather than inside `BuildStructuralProperties`, which runs
  *while* the EDM is being built and therefore has no EDM to consult. `StructuralProperties` itself
  is unchanged, so nothing else moves: the companion OpenAPI/NSwag/Swashbuckle packages do not read
  it, and #313's continuation-route collision check reads the profile's navigation set instead. The
  profile's navigation set is again **not** re-sourced from the EDM (see #322 below for the measured
  reason), so `$expand`'s delegate-safety decision table is untouched.

  A genuine structural property on the same entity set keeps its full route surface, and so does the
  navigation's own foreign key (`CustomerId` is a scalar, and the subtraction is by navigation
  *name*) — both pinned, so "everything 404s" cannot pass vacuously.

- **An undeclared convention navigation silently disqualified its whole entity set from `$select`
  and `$expand` pushdown (#322).**
  A profile's `StructuralProperties` is "every public readable CLR property **minus every
  profile-declared navigation**", so a navigation the convention builder discovered but the profile
  never declared survived as a structural property flagged complex-typed — and the projection's
  complex-member bail then abandoned the member-init `Select` for every request whose projection
  member set included it. The result was a silent fall back to the `#305` `Include` path (no column
  pruning) that turned into a **`400`** as soon as the request carried a nested
  `$filter`/`$orderby`/`$expand`. Measured, on three models differing only in the navigation's
  provenance:

  | request | before | after |
  |---|---|---|
  | `?$expand=Books($filter=…)` | `400` | `200`, predicate in SQL |
  | `?$expand=Books($orderby=…)` | `400` | `200`, ordering in SQL |
  | `?$select=name,publisher&$expand=Books($filter=…)` | `400` | `200` |
  | `?$select=name,publisher` | `SELECT Id, Name, PublisherId` | `SELECT Name, Id` |
  | `?$expand=Books` | `200` | `200`, SQL identical |
  | `?$select=name&$expand=Books($filter=…)` | `200` | `200` (never affected) |

  Two scope notes, both measured rather than assumed. A **bare** `?$expand=Books` emits
  **byte-identical SQL** on both trees — the `Include` fallback and the member-init projection
  produce the same `LEFT JOIN` for an unoptioned expand, so what that shape recovers is the
  materialization path and `$select` column pruning, not the emitted query. And a nested `$top`
  *alone* was never affected: the `#305` fallback uses an EF Core **filtered include**, which
  carries `Take()`, so `?$expand=Books($top=1)` already answered `200` with a `ROW_NUMBER()` window
  in SQL. Only a nested `$filter`/`$orderby` (which a filtered include cannot carry) and a nested
  `$expand`/`$levels` produced the `400`.

  > **One payload difference, and it is a unification.** On a **non-EF** `GetQueryable` whose
  > in-memory graph already holds the related object, a `$select` that *names* the undeclared
  > navigation together with a `$expand` of it now serializes `null` where it previously echoed the
  > in-memory value (`?$select=note,cust&$expand=Cust`:
  > `{"Cust":{"Id":5,"Name":"IN-MEMORY"}}` → `{"Cust":null}`). `$expand` pushdown is EF-gated, so
  > nothing ever *loaded* that value — it was only whatever the profile's own graph happened to
  > carry, and it survived the projection solely because the navigation had been misclassified as a
  > projectable column. A **declared** delegate-less navigation on the same model, same source and
  > same request already returned `null` before and after, so the two provenances are now
  > indistinguishable on this path. Pinned, with that declared control alongside it, by
  > `Issue322NonEfProjectionUnificationTests`. Nothing else moves: without the `$select` there is no
  > projection to drop the value, and on an EF-backed source the navigation is un-`Include`d and
  > therefore `null` either way.
  >
  > **Re-scoped by #440 above, in the same release.** The undeclared side of that comparison is now
  > *omitted* rather than `null` — `null` was the answer #440 identified as the wrong one — so the
  > two provenances diverge again on this path, this time by design: declaring a navigation is what
  > makes it servable at all. The #322 half of the claim is unchanged and still pinned: an undeclared
  > navigation is no longer treated as a projectable column.

  The fix subtracts the **EDM's own** navigation names when building the projection's structural
  member set: the EDM is the authority on what is a navigation, and a navigation is not a
  projectable column. Scoped to that one dictionary — the profile's navigation set is *not*
  re-sourced from the EDM, because that set is what `$expand`'s delegate-safety model partitions
  (#292/#293), where "a candidate that neither routes nor declares the navigation has no opinion on
  it" is load-bearing; sourcing it from the convention builder would collapse the honored-sole-route
  case to a blanked one and silently drop delegate-loaded data. The full decision table is now
  pinned by test. The remaining correctness symptoms of the same disagreement are #440 above.

- **The projection-ineligibility `400` recited the eligibility rule instead of naming the failing
  check (#322).**
  `"…requires a projection-eligible model, which this one isn't (an eligible model has a public
  parameterless constructor, settable non-complex properties, and — if it uses ETags — a direct
  UseETag selector over structural properties)"` was returned to developers whose model satisfied
  every clause of it. The message now names the one check that failed, as reported by the check
  itself: `"…and 'NoCtorParent' is not one because 'NoCtorParent' has no public parameterless
  constructor (a positional record has none)"`.

- **A nested `$count=true` discarded the nested `$top` SQL bound, so the whole related collection was
  materialized to return a page of it (#334).**
  `?$expand=Children($top=10;$count=true)` fetched `MaxExpandTop + 1` rows to return 10 — and with
  the ceiling unset, which has been the shipping default since #313, it composed **no row bound at
  all**. Only the emitted SQL changes; every response body is byte-identical, pinned across 12
  nested-clause shapes × 3 ceilings by `NestedCountTopByteIdentityTests` against values captured
  from the pre-fix build.

  **Why it happened.** Under the #254/#298/#304 "`$count` defers paging to the JSON pass" design the
  count *was* the materialized array's length, so an exact count required the full filtered
  collection and `ApplyNavShape` had to compose the ceiling bound in place of the client's `$top`.
  The root cause is one level down: OhData projects into the CLR entity type, `new TModel { … }`,
  which has **nowhere to put a count scalar**. `Microsoft.AspNetCore.OData` has had that slot all
  along — `SelectExpandWrapper`'s `PropertyContainer` carries `Collection` and `TotalCount` side by
  side — which is exactly why `$count=true` never perturbed its `$top` translation.

  **The fix.** A projection carrier supplies the missing slot, and the count becomes a *second,
  independent* expression rooted at the same navigation node — filtered but never ordered or
  windowed — mirroring `SelectExpandBinder`'s own `CreateTotalCountExpression` / `ProjectAsWrapper`
  split. Neither chain reads the other, so the window composes to SQL exactly as it does without
  `$count`. It stays one round-trip (page and count come from one snapshot, so they cannot disagree
  under concurrent writes), and the carrier is unwrapped to `TModel[]` immediately after
  `ToArray()` — nothing in the JSON shaping pipeline ever sees a wrapper type.

  A correlated `COUNT(*)` is a scalar aggregate, **not** the `APPLY`/`LATERAL` shape a windowed
  collection projected out of a windowed collection needs, so it composes beside the `ROW_NUMBER()`
  window on SQLite too — verified from captured SQL and pinned as a live regression, because #300
  established that the other shape does not translate there.

  ```
  ?$expand=Children($top=10;$count=true)      MaxExpandTop=null   MaxExpandTop=1000
    before                                    (no bound at all)   WHERE "row" <= 1001
    after                                     WHERE "row" <= 10   WHERE "row" <= 10
  ```

  **`Nav@odata.count` is unchanged and still exact** — OData §11.2.4.2 requires the count of the
  full *filtered* collection, never the returned page, and a fix that bounded the fetch by
  under-reporting the count would have been a far worse defect than the one it replaced. The nested
  `$filter` rides into the count subquery; the `$orderby`/`$skip`/`$top` deliberately do not.

  The `MaxExpandTop` ceiling is **re-sited, not relaxed**: the breach signal moves from "the
  materialized array is longer than the cap" to "the exact count is greater than the cap". Those are
  the same predicate — the pre-fix array was `Take(cap + 1)`-bounded, so `arr.Count > cap` already
  *meant* `trueCount > cap` — except that the signal is now exact rather than a saturated proxy, so
  a breach is still a `400` with a byte-identical message even when only the requested window was
  fetched.

  **Scope.** A collection-valued, projection-**leaf**, non-`$levels`, top-level expand carrying
  `$count` **and** an actual nested `$skip`/`$top` window, on the `GetQueryable` collection route.
  Everything else keeps the pre-#334 path exactly: a `$count` with no window (there is nothing to
  bound, so engaging the carrier would buy a count subquery for no benefit), a counted nav that
  itself carries nested `$expand` children or sits at depth ≥ 2, `$levels` + `$count`,
  `GET /{Set}({key})`, the #305 Include fallback, and a delegate-backed navigation (never pushed
  down, so never carrier-decorated).

  **Measured** (BenchmarkDotNet, `ExpandComparisonBenchmarks.ExpandNestedOptions`,
  `$expand=Employees($top=10;$orderby=id;$count=true;$select=Id,Name)`, 20 departments × 50
  employees; `executed benchmarks: 2` per run):

  | | before | after |
  |---|---|---|
  | OhData allocated | 1,794.4 KB | **437.3 KB** (−75.6%) |
  | OhData Gen0 / Gen1 per 1,000 ops | 93.75 / 31.25 | **0 / 0** |
  | OhData mean | 3.220 ms | 2.300 ms (median 2.072 ms) |
  | `Microsoft.AspNetCore.OData` allocated (control) | 555.9 KB | 566.3 KB |
  | allocation, MS ÷ OhData | 0.31× | **1.29×** |

  This was the one scenario in the server-comparison suite where
  `Microsoft.AspNetCore.OData` decisively beat OhData; OhData now allocates **less** than it does,
  with no gen-0 or gen-1 collections at all, and its median latency is lower. Read the allocation
  row as the result: it reproduced to within 0.4 KB across runs, whereas the timings on this
  category carry documented run-to-run noise (the unchanged Microsoft arm itself moved 2.247–2.689 ms
  across the same three runs).

- **`$levels=max` bypassed the depth cap the numeric form is validated against (#428).**
  Microsoft's `SelectExpandQueryValidator` rejects a *numeric* `$levels=N` when
  `N > min(MaxExpansionDepth, modelBoundMaxDepth)`; for the `max` literal it only requires that
  minimum to be non-zero — it does not clamp. OhData then resolved `max` against
  `MaxExpansionDepth` alone and never consulted the model-bound cap, in **two** independently
  transcribed places (the pushdown projection builder and the JSON keep/strip pass). So
  `$levels=max` was served at depths every numeric spelling returned `400` for. Measured with the
  model-bound cap lowered to 5 and a profile at `MaxExpansionDepth = 8`:

  ```
  $levels=5     -> 200    609 ms   joins=6
  $levels=6..9  -> 400              <- rejected by the model-bound cap
  $levels=max   -> 200  5,477 ms   joins=9   <- served at depth 8
  ```

  At ~3× translation cost per level (#328) that is a cost multiplier, not a cosmetic
  inconsistency: on a stock build a profile at `MaxExpansionDepth = 15` served `max` at depth 15 —
  `3¹⁶` translation units, extrapolated at **~2.2 hours of single-core CPU for one request**.

  Both call sites now share one `ResolveLevelsBudget` function that consults **both** bounds, and
  #328 additionally derives the model-bound cap from the new `MaxExpansionDepthCeiling`, so the two
  can no longer diverge at all. The second half is what actually closes the hole on a shipped build
  — the shared function is what stops it re-opening if the ceiling is ever widened, and
  `ExpandLevelsResolutionTests` asserts the tie as a tripwire.

- **`$levels=N` emitted N+1 join levels; the extra one was pure waste and cost a factor of 3
  (#335).** The `$levels` recursion terminated its deepest level with
  `n.Nav.Take(0).ToList()` — an expression that still *names* the navigation, so EF Core composed a
  real join for it: a full-table `ROW_NUMBER()` window whose every row was then discarded by
  `WHERE "row" <= 0`. The terminator is now `new List<T>()`, which names nothing and is evaluated
  per row on the client.

  The dead level was not a constant cost. Translation of a pushed nested projection costs ~3× per
  collection level (#328), so removing one level divides the whole request's translation cost.
  Measured end-to-end (16-node self-referential chain, SQLite in-memory, warm host):

  | `$levels` | before | after |
  |---|---:|---:|
  | 5 | 309 ms | 94 ms |
  | 6 | 883 ms | 238 ms |
  | 7 | 2,404 ms | 677 ms |
  | 9 | 9,856 ms | 2,196 ms |

  **Pure optimisation — no payload changes.** The terminating level still serializes as an empty
  array (`"Children":[]`), including its `Nav@odata.count` of `0` under `$count=true`.
  `LevelsJoinCountSqliteTests` pins both halves: the join count against the table is now exactly `N`
  for `$levels=N` and no `ROW_NUMBER()` is emitted, and four response bodies captured from the
  **pre**-fix build are asserted byte-for-byte against the post-fix one.

- **`Ignore()` containment was case-sensitive while body binding is case-insensitive (#398).** The
  withheld-name sets were built and compared with `StringComparer.Ordinal`, but the declared-member
  lookup they are consulted *after* uses `OrdinalIgnoreCase` whenever `PropertyNameCaseInsensitive`
  is set — which in an ASP.NET Core host is always. So with `Secret` withheld, a body key spelled
  `secret` missed the declared lookup (the member is no longer in the contract), missed the ordinal
  withheld set, and was classified as an ordinary dynamic key: bagged on the way in, echoed on the
  way out. Measured — `Secret` was contained; `secret`, `SECRET` and `sEcReT` all round-tripped. The
  read side was broken independently: a server-side bag key `secret` serialized out where `Secret`
  faulted.

  The withheld sets now carry the **binder's** comparer, at the one place they are built and at every
  place they are consulted (the write-body walk, the body rewriter, and the read-side container
  inspection). The declared-name collision check keeps its **ordinal** semantics deliberately and is
  now a separate set: its question is whether two keys would emit as one duplicate JSON key, which a
  case-differing key does not, so faulting on one there would reject data that serializes perfectly
  well. The two are no longer merged, and merging them is what produced the bypass.

  Only reachable once open types widen to entity roots (nothing today puts an `Ignore()`d entity
  member and a dynamic container on the same type), so no shipped release is affected.

- **`@odata.bind` went from `400` to silently accepted on every write route except the collection
  `POST` (#398).** Unreleased regression from the `@`-as-control-information rule above:
  `Thing@odata.bind` contains an `@`, so it was classified as control information and stripped, and
  only the collection `POST` ran a bind-specific check of its own. A `PUT`, `PATCH`, navigation-`POST`
  or structural-property write asking to link an existing entity got a `200`/`201` and nothing
  happened.

  The detection now runs at the shared write-body preparation step, so every route that binds a body
  through it — `PUT`, `PATCH`, the navigation-`POST` create route, the structural-property writes, and
  each bound/unbound action parameter — answers `501 Not Implemented`, the same status the collection
  `POST` has always given. `501` rather than `400` because deep insert by reference is *unimplemented*,
  not malformed, on every verb; the previous `400` was incidental, produced by `@` failing the
  `odataIdentifier` grammar rather than by anything that knew what `@odata.bind` meant.

  > **Superseded by #456, in this same release.** As shipped here the check sat *below* the write-body
  > preparation step's open-types gate, so those routes carried it **only** on registrations whose EDM
  > declares an open complex type — which this entry described as *"the only condition under which
  > they buffer the request body at all"*. On the majority of registrations, which have no open
  > complex type, `PUT`, `PATCH`, the navigation-`POST` create route and the structural-property
  > writes therefore went on accepting `@odata.bind` with a `200`/`201` and silently discarding it.
  > #456 hoists the check above that gate, and gives `PUT` and the navigation-`POST` route — which
  > never reach that step on the non-open path, streaming the body straight into the deserializer — a
  > buffered raw-UTF-8 scan of their own. So the premise no longer holds either: those two buffer the
  > request body on **every** registration now. See the `### Fixed` entry for #455/#456/#461, and the
  > body-limit note it carries (#474).

- **Three server-driven-paging defects that break a client following `@odata.nextLink` (#360, #399).**
  None had test coverage: every pre-existing paging fixture was sized so the final page is partial,
  and every walk started at offset 0.

  1. **A spurious `@odata.nextLink` on an exactly-full FINAL page** (`GetQueryable` and Priority-1
     `GetODataQueryable`, #360). The emit condition was "the page came back exactly `pageSize` long"
     with nothing to compare against a total, so a collection whose row count is an exact multiple of
     the page size ended every walk with one empty trailing page. Both paths now fetch one row *past*
     the page and emit the link only if that probe row actually came back, then discard it — the same
     single query, one row wider, with no extra round-trip (which matters because `GetQueryable`
     deliberately does not materialize, and Priority-1 hands query application to the profile).
     `@odata.count` is computed independently, pre-paging, and is unaffected. `GetAll` already got
     this right and is untouched.
  2. **A Priority-1 continuation the framework emitted but did not apply (#399)** — it was a
     `$skip=N` the profile was expected to re-apply via `ODataQueryOptions.ApplyTo`, so a profile that
     ignores the options it is handed served the identical first page forever and a `nextLink` walk
     never terminated. The framework now carries its own offset and applies it itself, so the
     continuation is correct whether or not the profile cooperates. The offset is also applied when
     the client grafts a `$top` onto the link, rather than being silently dropped and rewinding to
     the first page.
  3. **`GetQueryable` dropped an explicit client `$skip` from the continuation (#399).** The skip was
     applied to the query but left out of the offset the next link was built from, so
     `?$skip=10` at page size 10 served rows 10–19 and linked straight back to row 10 — an infinite
     rewind. The `$skiptoken` form needed no separate fix; it already encodes an absolute offset.

  > **Behaviour change — the Priority-1 continuation link changed shape.** It now carries a
  > framework-private custom query option (`ohdata-skiptoken`) instead of `$skip`. It cannot be
  > `$skiptoken`: `ApplyTo` throws on one it has no handler for, which would break every profile that
  > calls it. `@odata.nextLink` is opaque by spec (§11.2.5.7) and is meant to be followed verbatim,
  > but a client that **persisted** a Priority-1 `nextLink` across a deploy will find the old `$skip`
  > form is no longer produced — such a link still resolves, it just no longer means "continue from
  > where the walk was" to the framework. Re-issue the collection request instead of replaying a
  > stored link. `GetQueryable` (`$skiptoken`) and `GetAll` (`$skip`) link shapes are unchanged.

- **`$filter`/`$orderby` division-by-zero (and decimal overflow) no longer 500s on the
  LINQ-to-Objects and EF Core InMemory-provider read paths (#358, partial).** `$filter=Price div 0`/
  `mod 0` raised an unhandled `DivideByZeroException` (or, for a decimal arithmetic overflow,
  `OverflowException`) that reached the group-level exception filter as a generic `500`, rather than
  a `400 InvalidQueryOption` OData error. Fixed by wrapping only the enumeration/count of the
  `$filter`/`$orderby`-`ApplyTo`'d query — not handler invocation, `$expand`/ETag/serialization — in a
  narrow, guarded catch: it engages **only** when the request actually carries `$filter` or
  `$orderby`, so a handler's own arithmetic bug (unrelated to a client query option) still 500s,
  logged, exactly as before. **Known gap, deliberately not addressed here:** a real relational
  provider may raise its own `DbException` subclass instead of a CLR exception (SQL Server, msg 8134;
  PostgreSQL, SQLSTATE 22012) — those are not caught, so the `500` persists on those databases. A
  provider-independent fix (rejecting a literal-zero divisor in the parsed `$filter`/`$orderby` AST
  before `ApplyTo` runs) is tracked as a follow-up issue; this change does not close #358.

- **`OhData.TestBench.AspNetCore` no longer registers `AppDbContext` as a singleton (#356).** `DbContext`
  is not thread-safe and its change tracker is not safe to share across requests: one failed
  `SaveChanges()` left a poisoned entity in the single shared tracker's `Added` state forever, so every
  subsequent write (POST/PUT/PATCH/DELETE, bound actions) on any entity set, across both the v1 and v2
  registrations, failed with `500` for the remaining life of the process. Registered scoped instead
  (the `AddDbContext` default, and what OhData profiles are themselves registered `AddScoped` to
  expect). The flagship sample no longer teaches this anti-pattern.
- **`UseETag` no longer loses sub-second precision or depends on server culture — same-second writes
  are no longer lost updates (#351).** ETag inputs were hashed with a bare, parameterless
  `value.ToString()`: no format specifier and no `IFormatProvider`. Every date/time type therefore
  contributed a *general* (human-readable) rendering that drops the fractional second — and, for
  `TimeOnly`, whole seconds — so two genuinely different entity states written inside the same second
  hashed to a byte-identical ETag. A client holding a stale ETag then **passed** the `If-Match`
  precondition and silently overwrote a newer version with a `200` where RFC 7232 §3.1 / Protocol
  §8.2.5 require `412`. This was not an exotic path: it is exactly the `UpdatedAt = DateTimeOffset.UtcNow`
  pattern `docs/etags.md` recommends and the TestBench profiles use — any two writes that land in the
  same second collide, regardless of how fine the clock is. The same line was culture-sensitive, so a
  `de-DE` server produced a different ETag from an `en-US` server for byte-identical entity state
  (spurious `412`s across a mixed-locale fleet), and a `th-TH` server rendered dates in the Buddhist
  calendar.

  ETag inputs now go through a dedicated formatter that is round-trippable **and** culture-invariant
  per type: `"O"` for `DateTime`/`DateTimeOffset`/`DateOnly`/`TimeOnly`; `"c"` for `TimeSpan` and
  `"D"` for `Guid` (`"O"` is not a legal specifier for either and throws); invariant-culture default
  formatting for `float`/`double` (the shortest *round-trippable* form since .NET Core 3.0),
  `decimal` (already exact, and scale-preserving), integers, `bool`, `char`, `string` and enums; and
  `IFormattable` under invariant culture for anything else. Binary row-version inputs are still
  hashed raw, and now cover every shape such a column realistically arrives as — `byte[]` plus
  `ImmutableArray<byte>`, `ReadOnlyMemory<byte>`, `Memory<byte>` and `ArraySegment<byte>`, none of
  which implements `IFormattable` or overrides `ToString()` and all of which previously hashed to
  their own *type name* (one shared ETag for every row in the set). A `DateTime` whose `Kind` is
  `Local` is hashed by its wall-clock reading plus a Kind marker rather than by `"O"`'s local-offset
  suffix, so the ETag never becomes a function of the server's timezone configuration or of a tzdata
  update. The type discriminator is derived from type *names* only, never assembly identity, so the
  `net8.0` and `net10.0` builds of this package agree and an application version bump does not rotate
  anything.

  The hash framing was hardened at the same time, closing three adjacent collision vectors: each
  value is now length-prefixed (so `("ab","c")` and `("a","bc")` cannot produce the same bytes),
  `null` carries a distinct marker (so clearing a string property to `null` no longer hashes the same
  as setting it to `""`), and each value carries a CLR type discriminator (so the string `"1"` and
  the integer `1` cannot collide).

  **BEHAVIOR CHANGE:** every ETag value produced by `UseETag` changes. The previous values were
  unsound, so this is unavoidable and is the safe direction: a client presenting an ETag minted by an
  older build gets `412 Precondition Failed` on a conditional write, or a full `200` representation
  instead of `304 Not Modified` on a conditional read, and re-fetches. No data is at risk from the
  transition; the pre-fix values were the ones that put data at risk. The ETag *mechanism* —
  weak/strong tag form, header handling, and the `If-Match`/`If-None-Match` comparison — is unchanged.

  **BEHAVIOR CHANGE:** `MapOhData()` now throws `InvalidOperationException` for a `UseETag` selector
  whose declared type cannot be hashed faithfully — a navigation property, an entity reference, a
  `List<T>`, a POCO, `object`. Such a selector never worked: with no `ToString()` override it
  formatted to its own type name, so every entity in the set shared one ETag and `If-Match` silently
  became a check that always passes, observable only as a lost update. Supported selector types are a
  binary buffer, `string`, `bool`, an enum, any `IFormattable` type, or a `Nullable` of those; the
  exception names the entity set, the selector and its type, and points at the remedy (select a
  scalar projection, e.g. `x => x.Related.Id`). See `docs/etags.md`.

- **`$expand` pushdown no longer silently returns `[]` for a standard bidirectional EF relationship
  (#323).** A related type that navigates back to its parent (e.g. `Author.Books` / `Book.Author`) used
  to defer the whole branch off pushdown — even a 3- or 5-level expanded back-reference, a
  grandparent-skip shape, or a plain self-referential `$expand=Children($top=1)` — because the static
  guard treated ANY back-reference as an unconditional cycle risk. It wasn't: the real risk is
  materializing a related entity *bare* (untransformed) inside a pushed projection, which only happened
  at leaf expands (an intermediate level with its own nested `$expand` was already projected into a
  fresh POCO and never risked a cycle). **The fix:** every pushed-down expand — leaf included — is now
  materialized through the same member-init projection intermediate levels and `$levels` already used
  (`BuildShapedNavAccess`), which structurally forecloses a serialization cycle regardless of
  back-references; the static guard (`BuildExpandNavBinding`) is narrowed accordingly to defer only a
  related type that is BOTH cyclic AND not member-init-projectable. The #305 `Include` fallback (for a
  root model that can't support a member-init projection at all) keeps a conservative guard instead —
  fails loud with `400` on a leaf whose related type navigates back to the root model, since `Include`
  populates *tracked* entities and EF Core's own relationship fixup could still close a cycle there.

  **BEHAVIOR CHANGE:** a `$expand` that previously silently deferred to EDM-only under a `200` (with
  the navigation's default CLR value, typically `[]` or `null`) for a bidirectional relationship now
  actually pushes down and returns the real related data via a SQL `JOIN`. A request that hits the
  narrower #305 Include-fallback path and has a leaf whose related type navigates back to the root model
  now returns `400` instead of the same silent `[]`/`null`.

  **Wire change (accepted by design):** because every pushed leaf expand is now a fresh member-init
  projection rather than the bare related entity, a public CLR property on the related type that is
  **not** an EDM structural property (e.g. `[NotMapped]`, a get-only computed property not derived from
  bound scalars) is no longer materialized on a leaf-expanded entity — it serializes as its type's
  default value. This makes leaves consistent with intermediate levels, which already dropped such
  properties; a computed get-only property whose getter derives from bound scalar properties still
  serializes correctly. See `docs/query-options.md` for the full breakdown.

  Not fixed by this change: a self-referential entity set still 500s on a plain `GET` with no
  `$expand` at all (tracked-entity fixup cycles before serialization) — tracked separately as #325
  (fixed below).

- **A self-referential/bidirectional entity set no longer 500s on serialization, in ANY shape — plain
  `GET` with no `$expand` at all (#325), or a cycle that doesn't reference the root model on the #305
  `Include` fallback (#326).** Root cause: whole-graph serialization is bounded by the CLR object
  graph, but OData's "omit an un-expanded navigation" rule (`OmitUnexpandedNavigations`) is bounded by
  the `$expand` clause and previously ran strictly AFTER serialization — so any graph deeper/wider
  than the clause was handed to `System.Text.Json` first, and EF Core's own relationship fixup (which
  wires up `Parent`/`Children`-shaped navigations among tracked entities loaded in the same query, with
  or without `$expand`) made that graph cyclic. **The fix (Option B — clause-bounded, level-wise
  serialization):** a new `SerializeBounded` walker serializes an entity with every one of its EDM
  navigations suppressed at the `JsonTypeInfo` level (the same `TypeInfoResolver`-modifier mechanism
  `Ignore()`'d properties already use), then splices in — via reflection, recursively — only the
  navigations the `$expand` clause (or an active `$levels` budget) actually kept. Recursion is
  therefore bounded by the clause, never by the object graph, so a reference cycle is structurally
  unreachable. Replaces all five whole-graph `JsonSerializer.SerializeToNode` call sites: the root
  collection-GET serialize, the delegate-expansion splice, navigation-collection routes, single-entity
  responses (`GET`/`POST`/`PUT`/`PATCH`), and bound-operation collection results. A deep-insert POST
  response body (which deliberately serializes its nested-create graph inline, unbounded) is
  unaffected — that caller passes no EDM type, which `SerializeBounded` treats as an explicit
  opt-out and falls back to the pre-#325 whole-graph behavior. Delegate safety (Model B, #292/#293) is
  unchanged and, if anything, strengthened: a navigation not in the `$expand` clause is never read off
  the CLR object at all, and a delegate's own answer still always overwrites whatever this walker
  guessed by reading the graph before the delegate ran.

  **`#326` relaxed rather than widened:** the issue proposed rejecting (`400`) two additional cycle
  shapes the #323 Include-fallback guard (`FindCyclicLeafExpand`, "Change C") missed — a sibling
  cross-reference and a self-referential leaf element type. Since `SerializeBounded` makes ALL of
  these shapes safe to serve, rejecting them would be backwards; Change C's guard is removed
  entirely and all three shapes (including the original root-back-reference case Change C used to
  catch) are now served with real data instead of `400`.

  **BEHAVIOR CHANGE:** a plain `GET` (no `$expand`) over a self-referential/bidirectional entity set
  that previously 500'd now returns `200` with the un-expanded navigations correctly omitted. A
  `$expand` on the #305 Include fallback whose related type has any kind of back-reference — to the
  root, to a sibling leaf, or to itself — that previously returned `400` now returns `200` with real
  data.

  **BEHAVIOR CHANGE:** an **`$expand`'d collection navigation whose CLR value is `null`** (e.g. an
  uninitialized `List<T>` property) now serializes as `[]` instead of `null`. This is more
  §4.5.1-correct — a collection-valued navigation is a JSON array, never `null` — and matches how an
  un-loaded collection nav already serialized when populated-but-empty; only the previously-`null`
  case changes.

  **Not fixed by this change (deliberate, OWNER DECISIONS):** a cycle closed by an entity-typed CLR
  property that is **not** an EDM navigation (e.g. excluded from the EDM model entirely, distinct from
  `[NotMapped]`-for-EF) is the same blind spot `OmitUnexpandedNavigations` always had —
  `SerializeBounded` only bounds EDM-declared navigations — and still surfaces as a `500`.

## [1.5.0] - 2026-07-21

Query-pushdown and spec-correctness milestone: `$select` and `$expand` now push into the backing
`IQueryable` (column-pruned `SELECT`s and SQL `JOIN`s on the EF Core path), response JSON defaults to
PascalCase so payloads match `$metadata`, and a dependency-free delta mapper bridges DTO-backed entity
sets. **Several breaking changes** ship here — the `AddProfile` → `AddEntitySetProfile` registration
rename, the PascalCase-default casing flip (server and client), and `[JsonPropertyName]` now driving a
property's OData name (structural and navigation alike); see **Breaking** below.

### Breaking

- **Namespaces consolidated.** All user-facing and runtime types move to a single **`OhData`**
  namespace (from `OhData.Abstractions`, `OhData.Abstractions.AspNetCore.OData`, and
  `OhData.AspNetCore`), and the DI/endpoint extension methods move to the framework namespaces they
  extend: `AddOhData`/`AddOhDataVersion` are now in **`Microsoft.Extensions.DependencyInjection`** and
  `MapOhData`/`MapOhDataVersion` in **`Microsoft.AspNetCore.Builder`** (so they light up on
  `builder.Services.`/`app.` with no OhData-specific `using`). The `OhData.Abstractions` and
  `OhData.Abstractions.AspNetCore.OData` namespaces are removed. The companion packages' public
  transformer/processor/filter types move out of the shared `OhData.AspNetCore` namespace into their own
  package namespaces — `OhData.AspNetCore.OpenApi`, `OhData.AspNetCore.NSwag`, `OhData.AspNetCore.Swashbuckle`.
  `OhData.Client`'s namespace is unchanged. **Migration:** replace `using OhData.Abstractions;`,
  `using OhData.Abstractions.AspNetCore.OData;`, and `using OhData.AspNetCore;` with `using OhData;` for
  types (the `Add*`/`Map*` extensions need no `using`); reference the companion transformers via their new
  package namespace (e.g. `using OhData.AspNetCore.OpenApi;`). `EntitySetDefaults` is now `sealed`.
- **`AddProfile<T>()` is renamed to `AddEntitySetProfile<T>()` (#243).** Hard rename, no `[Obsolete]`
  alias — the old name is removed. **Migration:** rename every `builder.AddProfile<MyProfile>()` call
  site to `builder.AddEntitySetProfile<MyProfile>()`. The `AddProfilesFrom*` assembly scanners are
  unchanged in signature (they now discover both `EntitySetProfile` and the new `DeltaProfile` in one
  pass), so registrations that rely on scanning need no edit. `AddDeltaProfile<T>()` is the new sibling
  for delta profiles.
- **Default response JSON casing flipped camelCase → PascalCase (#252, #258, #260).** OhData now owns
  its response property-name casing independently of the host's ASP.NET Core `HttpJsonOptions`,
  defaulting to **PascalCase** — the CLR/EDM names `$metadata` declares (OData §4.4) — so payloads match
  the model and strict case-sensitive OData-native clients (e.g. `Microsoft.OData.Client`) bind out of
  the box. The host's `HttpJsonOptions.PropertyNamingPolicy` is no longer inherited (its custom
  converters/encoder still are). The flip applies to every response path (collection/GetById reads,
  POST/PUT/PATCH echoes, `$select`/`$expand` output, `$value`, and function/action results). **This also
  flips OpenAPI/Swagger schema property casing to PascalCase** — the three schema generators
  (`OhDataOpenApiSchemaTransformer`, `OhDataNSwagSchemaProcessor`, `OhDataSwaggerSchemaFilter`) now follow
  OhData's owned policy across nested complex types and inherited base classes, closing the earlier
  doc-vs-wire drift (`[JsonPropertyName]` renames win in the schema as on the wire). **Migration:** to
  keep the previous camelCase wire/OpenAPI shape, opt back in explicitly with
  `AddOhData(o => o.WithJsonPropertyNamingPolicy(JsonNamingPolicy.CamelCase))`. Note that opting into
  camelCase desyncs payload casing from `$metadata`, which always uses the PascalCase CLR/EDM names.
- **`OhData.Client` now defaults property casing to PascalCase (#263).** Request bodies and
  `$filter`/`$select`/`$expand`/`$orderby` property names now use the CLR/PascalCase names by default,
  matching OhData.AspNetCore's PascalCase-default responses and `$metadata`. **Migration:** to keep
  camelCase, set `o.JsonOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase` on the
  `OhDataClientOptions` (mutate the existing `JsonOptions` in place so its other defaults —
  case-insensitive reads, ignore-null-on-write — are preserved).
- **`[JsonPropertyName]` now drives a property's OData name — structural *and* navigation (#253).** A
  model property carrying `[System.Text.Json.Serialization.JsonPropertyName("wireName")]` is now named
  `wireName` on **every** OData surface — `$metadata`, the response payload, and the server-accepted
  `$select`/`$filter`/`$orderby` spellings (and the property-route URL segment) — instead of the EDM
  using the CLR name while only the payload used the rename. This closes a **silent data-loss** bug:
  `$select=<ClrName>` (the only spelling the CLR-named EDM used to accept) returned a payload keyed by
  the rename, so the `$select` post-strip dropped the property from the response entirely. The client
  translators emit the rename too, so client-issued query options match. **`[JsonPropertyName]` now
  drives the OData name for navigation properties too** — the `$expand`/`$filter`/`$orderby` identifier,
  the nav-path URL segments (`/{EntitySet}({key})/{wireName}`, `.../{wireName}/$ref`,
  `.../{wireName}/$count`, `POST .../{wireName}`), `$metadata`, and the response key all use the JSON
  name, exactly like a structural property, and the client emits it. (This reverses the interim #184
  behavior where a navigation kept its CLR name as the `$expand` identifier.) An un-renamed navigation
  is unaffected (CLR name everywhere). **Migration:** for a `[JsonPropertyName]`-renamed property or
  navigation, the OData/`$metadata`/query-option/URL-segment name is now the JSON name, not the CLR
  name — update any `$select`/`$filter`/`$orderby`/`$expand`/`$metadata`/nav-path-bound client that
  referenced the old CLR name to use the JSON name (the old CLR name now returns `400` as an unknown
  property, or `404` on a nav-path segment). A rename that would collide with another property's OData
  name on the same type (structural or navigation) now fails fast at startup.

### Added

- **Dependency-free delta mapping (#243).** `DeltaProfile` + the injected `IDeltaFactory` give
  DTO-backed entity sets a clean PATCH/PUT/POST **write** path without AutoMapper. Declare mappings
  in a profile (`For<TModel, TEntity>()`, then only the divergences via `.Rename()` / `.Ignore()` /
  `.Convert()` — no `.Build()`); the framework discovers, compiles, and validates them once at
  startup. Handlers call `IDeltaFactory.Create<TModel, TEntity>(delta)` (delta → delta) or
  `Create<TModel, TEntity>(model)` (model → delta) and apply the result with the built-in
  `Delta<TEntity>.Patch(entity)` — the framework never applies or persists. Conversion is a strict
  safe subset (identity, reference-assignable, nullable-wrap `T → T?`); everything else
  (narrowing, `int → long`, enum↔string, `T? → T`) requires an explicit `.Convert(...)` lambda —
  `Convert.ChangeType` is never called implicitly. The produced `Delta<TEntity>.UpdatableProperties`
  allowlist is translated from the model side (structural properties minus `Ignore()`d names) so
  immutability/security constraints survive the DTO→entity boundary. Fail-fast at `MapOhData()` on
  any unmapped/unwritable/incompatible/duplicated mapping. Also adds expression-based `Delta<T>`
  sugar — `IsChanged(x => x.Prop)` and `TryGetChanged(x => x.Prop, out value)`. Scalars/structural
  only; ships in core `OhData.AspNetCore`. See [docs/delta-mapping.md](docs/delta-mapping.md).
- **`$expand` Include pushdown (#206, phase 2 — "provenance-auto").** A navigation declared
  **without** a custom expand delegate (a bare `HasMany`/`HasOptional`/`HasRequired`) is now
  **SQL-JOIN-expandable automatically**: on the EF Core-backed `GetQueryable` path, `$expand`'ing
  it folds the navigation into the collection query's member-init projection, so a **single JOIN'd
  query** loads the page and its related rows (collapsing the per-entity/batch delegate's *N×P*
  sequential calls to one query) — no delegate to write, no N+1. Both collection and single-valued
  references are supported, and it composes with `$select` pushdown in one query. **Eligibility is
  decided purely by whether an expand delegate exists** — no global flag, no per-navigation
  opt-in: declaring a delegate (`getAll`/`get`/`batchGetAll`/`batchGet`) opts a navigation **out**
  of pushdown (the delegate always owns expansion, so it can filter/order/authorize and is never
  bypassed); a bare declaration opts it **in**. On by default
  (`EntitySetDefaults.ExpandPushdownEnabled` / per-profile `ExpandPushdownEnabled`), with silent
  Debug-logged fallback — the delegate-less navigation stays EDM-only for that request, never a
  `500` — whenever pushdown is ineligible: a non-EF provider, a `$levels` or **nested** `$expand`
  (multi-level), a cyclic navigation (guarded at startup against base/interface back-references),
  or a projection/translation/serialization failure. Mental model: *write a delegate only when
  expansion needs real logic; a plain relationship gets SQL-JOIN expansion for free.*
- **Nested `$expand` options on a pushed navigation (#206, phase 2).** A pushed (delegate-less)
  `$expand` now honors the expanded collection's nested options. `$filter`, `$orderby`, and
  `$top`/`$skip` are pushed to SQL as a **filtered / ordered / paged `Include`** — translated by
  Microsoft's own OData `FilterBinder`/`OrderByBinder`, never a bespoke translator — so a single
  JOIN'd query loads exactly the requested related rows (no per-parent N+1). `$count` emits an
  inline `Nav@odata.count` (the full filtered count, paging applied after counting per §11.2.4.2)
  and `$select` projects the expanded elements. Output follows the configured naming policy (PascalCase
  by default) — no `SelectExpandWrapper` ever reaches the serializer. `$search`/`$compute`/`$apply`
  on the expand item is deferred (the navigation stays EDM-only for that request); see
  `docs/query-options.md`.
- **Multi-level `$expand` and `$levels` pushdown (#206).** A nested `$expand` is now pushed
  **recursively**: `?$expand=Books($expand=Chapters($expand=Pages))` folds every level into one
  JOIN'd query (EF Core `Include`→`ThenInclude`), applying each level's own nested
  `$filter`/`$orderby`/`$top`/`$skip`/`$count`/`$select`. A branch is pushed only when it is
  **delegate-less at every level** — the instant a level's navigation carries a delegate (or is
  cyclic / a non-projectable type) the whole branch is deferred off pushdown, so a **delegate-backed
  navigation is never EF-included at any depth** and its delegate is never bypassed (the
  delegate-safety invariant holds recursively). `$levels=N` / `$levels=max` recursively expand a
  **self-referential** navigation (a tree/hierarchy) as a bounded, cycle-free projection, capped at
  `MaxExpansionDepth` (`$levels=max` resolves to exactly that value). Output follows the configured
  naming policy (PascalCase by default) at every depth, and any level that fails to translate degrades
  gracefully to EDM-only (never a `500`).
- **`MaxExpansionDepth` advertised in `$metadata` (#206).** Each entity set now carries an
  `Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels` vocabulary annotation equal to its resolved
  `MaxExpansionDepth`, so clients can discover the server's `$expand`/`$levels` ceiling from the CSDL.
- **Per-operation authorization reflected in OpenAPI/NSwag docs (#219, opt-in).** Opt-in operation
  transformers in the OpenApi and NSwag companion packages reflect OhData's per-operation authorization
  (#199) into the generated document: each secured operation gets an operation-level security
  requirement referencing a security scheme the app already defined, plus documented `401` and `403`
  responses. Detection uses standard ASP.NET Core endpoint metadata (`IAuthorizeData` present,
  `IAllowAnonymous` absent), so it covers both the legacy profile-wide auth and the per-route model.
  OhData never defines the security scheme — the author supplies the scheme id when registering the
  transformer/processor — keeping it off by default. An `AllowAnonymous` route wins over
  `RequireAuthorization`, and an app-defined `401` is not clobbered.
- **Auth-requirements documentation filter (#220, opt-in).** An opt-in OpenAPI/NSwag operation filter
  that appends a human-readable authorization-requirements section (required roles, claim types/values,
  and named policy names) to each operation's description, drawn from OhData's structured per-operation
  auth data (#199). Off by default. Claim **values** are a mild info-disclosure surface, so they are
  emitted only at the `Full` disclosure level; the default `Kinds` level surfaces requirement kinds and
  their non-secret identifiers (claim types, role names, policy names) but not values. Register at most
  one filter instance (the append is idempotent per instance). Core exposes the resolved requirements as
  `OhDataOperationAuthMetadata` endpoint metadata plus a shared `OhDataAuthRequirementsText` renderer, so
  both companions produce identical text.
- **New runnable sample: `samples/OhData.Sample.EfCoreSqlite` (#209).** A clone-and-run EF Core SQLite
  sample demonstrating SQL pushdown (`$filter`/`$orderby`/`$select` translating to a single query), a
  DTO-projection entity set (`ProductSummaries` over a Products-join-Categories projection so wire-model
  filters/sorts push through the projection into one JOIN), and a many-to-many relationship with a
  suppressed join table (`Product` ↔ `Tag` via EF skip navigations, the join entity never reaching the
  wire). The README shows the real captured SQL for each.

### Changed

- **POST/PUT/PATCH responses now OMIT un-expanded navigation properties (#240).** POST `201` echoes
  and PUT/PATCH return-representation bodies previously serialized un-expanded navigations as explicit
  `null`/`[]`, while read responses (#176/#179) omit them — so `POST /X` echoed `"Category": null` where
  `GET /X(id)` omitted the member. The write-path response builders now run the same
  un-expanded-navigation omission as reads, so a write response and a read of the same entity have
  identical shape. **This is a response-shape change:** clients that relied on the empty `null`/`[]`
  navigation placeholders in write responses will no longer receive those keys — request the navigation
  with `$expand` to include it. A deep-insert (`AllowDeepInsert = true`) POST is exempt: it still echoes
  its created graph inline per §11.4.2.2, the one place a POST legitimately returns nested navigations.
- **`EntitySetDefaults.MaxExpansionDepth` default is now `3` (was `12`) (#206).** With multi-level and
  `$levels` pushdown, the depth limit is a meaningful request ceiling (it caps `$levels=max` and
  rejects deeper `$expand`/`$levels` with `400`), so the default is a conservative `3`. Raise it per
  profile or via `WithDefaults` for deeper graph/hierarchy queries.

- **`$expand` pushdown is now decoupled from `$select` pushdown (#206).** An `$expand` push no
  longer column-prunes the parent projection when `SelectPushdownEnabled` is `false` — the two
  capabilities are independent, so a profile can disable `$select` pushdown while keeping `$expand`
  JOIN pushdown (and vice versa).

### Fixed

- **Server paging now emits a stabilizing `ORDER BY` before `Skip`/`Take` (#241).** On the
  `GetQueryable` path, server paging previously applied `Take`/`LIMIT` with no `ORDER BY` when the
  client omitted `$orderby`, raising EF warning 10102 and leaving page order across `@odata.nextLink`
  formally undefined (rows could repeat or be skipped between pages on providers without a guaranteed
  scan order). The path now guarantees a deterministic total order before any `Skip`/`Take`: a present
  `$orderby` gets the entity key appended as a final tiebreaker (stable even when sorting on a non-unique
  column); an absent `$orderby` over an unordered source orders by the entity key ascending (OData Part 1
  §11.2.6.2); and a profile that pre-orders its own `IQueryable` is left untouched (its intended order is
  never silently overridden). The stabilizing order is injected only when a row-limiting operator
  actually runs. The Priority-1 path (which materializes the profile's own `ApplyTo` result before the
  framework's cap) is unchanged.
- **Nested `$top` inside `$expand` no longer returns `400` (#206).** Navigation-target types now
  clear Microsoft's model-bound `MaxTop = 0` default (`SetMaxTop(null)`), which previously rejected
  `$expand=Children($top=N)` with "The limit of '0' for Top query has been exceeded." OhData still
  governs `$top` itself (root: `source.MaxTop` clamp; nested: applied by the expand-pushdown path).
- **`$select` projection pushdown (#206, phase 1).** On the `GetQueryable` path, an eligible
  `$select` now composes a member-init projection onto the profile's queryable, so LINQ
  providers emit a **column-pruned `SELECT`** instead of reading every column. Wire output is
  byte-identical with or without pushdown (the configured naming-policy JSON pipeline — PascalCase by
  default — runs unchanged); the projected set is the selected properties plus the entity key and any
  `UseETag` properties. On by default (`EntitySetDefaults.SelectPushdownEnabled` /
  per-profile `SelectPushdownEnabled`), with silent Debug-logged fallback to the full fetch
  for ineligible requests (no parameterless constructor, setterless projected member,
  computed `UseETag` selector) and an opt-out for `IQueryable` providers that cannot
  translate member-init. (`$expand` pushdown is phase 2, above.)

## [1.4.0] - 2026-07-19

Production hardening (milestone 1.4.0): safe-by-default limits across the read paths,
per-operation and resource-based authorization, observability, and profile-driven property
exclusion.

### Added

- **Property exclusion via `Ignore()` (#226).** `EntitySetProfile.Ignore(x => x.Property)` excludes
  model properties from the whole OData surface — `$metadata`, query options, property routes,
  response bodies, and request binding (POST/PUT via serializer options, PATCH via an explicit
  delta-builder filter) — without touching the CLR type. Wire suppression uses one derived
  per-registration `JsonSerializerOptions` with a `JsonTypeInfoResolver` modifier (A/B-benchmarked
  on the issue; zero cost when unused, *faster* than baseline when used). Startup validation
  rejects ignoring the key, ignore/navigation conflicts (either declaration order), and
  same-model-type profiles with mismatched ignore sets.
- **Per-operation authorization (#199).** A profile can now authorize each operation category
  independently via `ConfigureAuthorization(auth => …)`, replacing the all-or-nothing profile-wide
  model for sets that need it. Categories are `Read`, `Create`, `Update`, `Delete`, and `Invoke`
  (bound operations), with `Writes()`/`All()` conveniences and `Invoke("Name", …)` for a single bound
  operation. Each category takes a nested lambda that **mirrors `AuthorizationPolicyBuilder`** —
  `RequireAuthenticatedUser()`, `RequireRole(...)`, `RequireClaim(...)`, `RequirePolicy(...)` accumulate
  and combine with AND — plus the exclusive `AllowAnonymous()`. Requirements are stored as plain data
  (policy/role/claim *names*), so profiles stay free of ASP.NET Core types; the factory applies them
  **per route** (`RequireAuthorization`/`AllowAnonymous`), so a global
  `MapOhData().RequireAuthorization()` composes as before and an unspecified category inherits it
  (anonymous when there is none). `$metadata`, the service document, and unbound operations remain
  group-level-only. The legacy `RequireAuthorization()`/`RequireRoles()` model is unchanged and still
  the default; combining it with `ConfigureAuthorization(...)` on one profile throws at startup.
  **Resource-based (instance-level) authorization** is also supported via `.RequireResource()`: OhData
  loads the `{key}` entity and evaluates the framework's `OperationAuthorizationRequirement` (exposed as
  `OhDataOperations.Read/Create/Update/Delete/Invoke`) against it, so you write a standard
  `AuthorizationHandler<OperationAuthorizationRequirement, TModel>` for owner/tenant checks (or
  `.RequireResource("PolicyName")` to run a named policy with the entity as the resource). It composes
  with the coarse requirements (AND), applies uniformly across key-based routes (entity, property, nav,
  `$ref`), fails **closed** (a requirement no handler satisfies → `403`), and requires a `GetById`
  handler on resource-checked Read/Update/Delete (enforced at startup). See `docs/authorization.md`.
- **Observability: distributed-tracing spans and metrics (#200).** OhData now emits telemetry via
  the BCL `System.Diagnostics` primitives — an `ActivitySource` and a `Meter`, both named `OhData` —
  with **no `OpenTelemetry.*` package dependency** taken by the library; consumers opt in from their
  own OpenTelemetry pipeline (`.AddSource("OhData")` / `.AddMeter("OhData")`), and the instrumentation
  is near-free when nothing is listening. One span per request (child of the ASP.NET Core request
  activity), named `{method} {route}` and tagged with `odata.entity_set`, `http.route`,
  `odata.operation` (a coarse method/shape label), `http.request.method`, and `http.response.status_code`
  (span status set to `Error` on `5xx`). Two metrics on the `OhData` meter:
  `ohdata.server.request.duration` (histogram, seconds) and `ohdata.server.active_requests`
  (up/down counter), tagged by entity set / operation / status. The `http.*` server tags aren't
  duplicated (ASP.NET Core already emits them). See `docs/observability.md`. A per-response
  result-size histogram is a planned follow-up.
- **Tunable query-complexity guards; the `$expand`-depth guard is now enforced (#202).** The
  settings-level expansion-depth validator was hardcoded to `0` (disabled), so a `$expand` nesting
  deeper than the framework could satisfy was silently truncated rather than rejected. Four limits are
  now configurable — globally via `WithDefaults`, or per entity set on the profile (profile overrides
  global): `MaxExpansionDepth` (default **12**, the framework's internal nested-expand cap — now
  **enforced**: a `$expand` nesting deeper than the limit returns `400` instead of a silently-truncated
  result), plus `MaxFilterNodeCount` (default `10000`), `MaxOrderByNodeCount` (default `1000`), and
  `MaxAnyAllExpressionDepth` (default `1000`). The node-count defaults are unchanged from what was
  already applied — they were previously hardcoded and are now lowerable to harden against expensive
  `$filter`/`$orderby` expressions. Enforced uniformly on all three collection read paths
  (`GetQueryable`, `GetAll`, Priority-1) via the shared property-allowlist validation. **Behavior
  change:** a request nesting `$expand` deeper than `MaxExpansionDepth` (default 12) now returns `400`
  where it previously returned a partial (truncated) `200`; no realistic request nested that deep,
  since the framework never expanded past its internal cap. Values above 12 are bounded by that
  internal cap; the intended use is to *lower* the limit.
- **Configurable request-body-size limit for write operations (#203).** A new
  `MaxRequestBodyBytes` (global via `WithDefaults`, or per entity set on the profile — the profile
  value overrides the global default) rejects an oversized write body (`POST`/`PUT`/`PATCH` and their
  navigation/`$ref`/property/action variants) with `413 Payload Too Large` and the OData error
  envelope, **before** the body is deserialized. Enforcement is twofold: an oversized `Content-Length`
  is rejected up front by a group-level filter, and the per-request Kestrel `MaxRequestBodySize` is
  set so a chunked / no-`Content-Length` body is bounded during read (Kestrel's resulting
  `BadHttpRequestException` is mapped to the same `413`). The limit is attached per entity set as
  route-group endpoint metadata and enforced once in the group filter — no per-handler wiring.
  **Default is `null`** (no OhData-level limit; the host's Kestrel default, ~30 MB, still applies), so
  this is purely additive — opt in by setting a value. See `docs/deep-insert.md`.
- **Structural-property routes are omitted from generated API docs by default (#221).** The property
  routes — `GET /{Set}({key})/{Property}`, its `/$value` variant, and the `PUT`/`PATCH`/`DELETE`
  property writes (including the immutable-key stubs) — number up to four per property, per entity
  set, and would otherwise dominate a Swagger/OpenAPI document. They are now excluded from
  ApiExplorer (`ExcludeFromDescription`) by default, which covers all three doc stacks
  (Microsoft.AspNetCore.OpenApi, Swashbuckle, NSwag) at once. A new `PropertyRouteDocsEnabled`
  (global via `WithDefaults`, or per entity set on the profile — the profile value overrides the
  global default, **default `false`**) opts them back into the docs. **Documentation-only:** the
  routes remain fully functional at runtime regardless of this flag; only their visibility in the
  generated document changes. **Docs-output change:** consumers who relied on property routes
  appearing in the generated OpenAPI document must set `PropertyRouteDocsEnabled = true` to keep
  them. See `docs/property-access.md`.

### Changed

- **The `GetAll` (simple/`IEnumerable`) read path now caps an omitted `$top` to `MaxTop` (#201).**
  Previously, omitting `$top` on a `GetAll` route returned the **entire** backing collection, however
  large — a deliberate decision at the time, because `GetAll` had no `@odata.nextLink` continuation
  story. #195 established an offset-`$skip` continuation for a re-enumerable source, and `GetAll` is
  re-enumerable, so that blocker is gone. An omitted `$top` is now capped to `MaxTop` (or a smaller
  `Prefer: maxpagesize`, clamped and echoed via `Preference-Applied`) with a `$skip` `@odata.nextLink`
  for the remainder — making all three collection read paths (`GetQueryable`, `GetAll`, Priority-1)
  uniformly safe-by-default. **This is a response-shape change** for `GetAll` routes whose source
  exceeds `MaxTop` (default `1000`): such a request now returns a bounded page plus `@odata.nextLink`
  instead of the full set. Sources **under** `MaxTop` are unaffected (the page isn't full, so no
  `nextLink` is emitted). **To opt out** and return the full set in one response, set `MaxTop = null`
  on the profile. `@odata.count` continues to reflect the pre-paging total.

### Fixed

- **The Priority-1 (`ODataEntitySetProfile` / `GetODataQueryable`) read path now enforces `MaxTop` (#195).**
  This path delegates query application to the profile's own `ApplyTo`, and the framework previously
  materialized whatever came back with `queryable.ToArray()` — so a client that omitted `$top` (or sent
  `$top` larger than `MaxTop`) could force the server to return the entire backing collection. The
  headline `MaxTop = 1000` default was silently inert here; it was only advertised in OpenAPI metadata.
  Now, consistent with the `GetQueryable` path: an oversized `$top` is rejected with `400`
  (`InvalidQueryOption`); an omitted `$top` is capped to `MaxTop` (or a smaller `Prefer: maxpagesize`,
  which is clamped so it can never lift the ceiling and is echoed via `Preference-Applied`); and a
  continuation `@odata.nextLink` is emitted when a full page is returned. The continuation link uses
  `$skip` rather than the opaque `$skiptoken` the `GetQueryable` path emits, because a Priority-1
  profile re-applies the incoming `ODataQueryOptions` via `ApplyTo`, which honors `$skip` natively but
  has no handler for `$skiptoken`. A profile that sets `ODataQueryResult.NextLink` itself is trusted to
  be paging on its own terms and the framework does not cap or override it. `@odata.count` remains the
  profile's responsibility (set `ODataQueryResult.TotalCount` for an accurate pre-paging total).
- **The main collection GET routes now reject unimplemented system query options (#196).** `$apply`,
  `$compute`, `$index`, and `$deltatoken` were parsed and then **silently ignored** on the main
  collection route, so `GET /Widgets?$apply=...` returned `200` with the option quietly dropped — while
  the navigation-collection route already rejected the same options with `400`. Ignoring a known query
  option violates OData Minimal-conformance item 7 ("the service MUST parse the option or reject the
  request"). These four options now return `400 UnsupportedQueryOption` uniformly across all three
  collection read paths (`GetAll`, `GetQueryable`, and Priority-1 `GetODataQueryable`), via the shared
  capability gate. Implemented and capability-gated options are unaffected (`$filter`/`$orderby`/
  `$select`/`$expand`/`$count`/`$top`/`$skip`/`$search`/`$skiptoken`).
- **Allowlist expression overloads now enforce their documented direct-member contract (#227).**
  `FilterProperties`/`OrderByProperties`/`SelectProperties`/`ExpandProperties` documented that only
  direct property access (`x => x.Name`) is supported, but nested member access slipped through:
  `SelectProperties(x => x.Name.Length)` silently allowlisted `"Length"` and
  `FilterProperties(x => x.Category.Name)` silently allowlisted `"Name"`. Both now throw
  `ArgumentException` at profile construction, per the documented contract. **Behavior change:** a
  profile that was (mis)relying on the lax behavior will now fail at startup — rewrite the selector
  to a direct property of the model.
- **Companion packages omit `Ignore()`d properties from generated schemas (#228).** #226 removed
  ignored properties from responses, request binding, `$metadata`, and query options — but the
  OpenAPI companion packages generate schemas from CLR types, so generated documents still listed
  them. Each companion now consults the registrations' ignored-property map (CLR model type →
  ignored CLR names, reached via `InternalsVisibleTo` so the core package keeps carrying no
  doc-stack dependency) and removes those members from the type's schema, respecting the
  serializer naming policy: `OhDataOpenApiSchemaTransformer` (`IOpenApiSchemaTransformer`,
  Microsoft.AspNetCore.OpenApi), `OhDataNSwagSchemaProcessor` (NJsonSchema `ISchemaProcessor`,
  NSwag), and `OhDataSwaggerSchemaFilter` (`ISchemaFilter`, Swashbuckle). Opt-in per stack —
  register the schema hook alongside the existing operation-level one; see the updated snippets in
  `docs/openapi.md`, `docs/nswag.md`, and `docs/versioning.md`.

## [1.3.0] - 2026-07-17

Spec-correctness and OpenAPI docs-fidelity across the read and documentation paths. Every change is
additive or a bug fix — no breaking API changes. Highlights: un-expanded navigation properties are
now omitted on every read path (OData JSON Format v4.01 §4.5.1/§11.2.4.2); nested `$expand`/`$select`
clauses are executed to arbitrary depth; `$metadata` is served as UTF-8 with a prolog, bytes, and
charset that all agree; `Accept` negotiation follows RFC 7231 §5.3.2 media ranges and q-values; and
the generated OpenAPI document now matches the live server (write-route request bodies, function
query parameters, typed responses, and `$top`/`$skip` on the simple `GetAll` read path).

**One response-shape change to be aware of:** clients that relied on the empty `[]`/`null`
placeholders OhData previously emitted for un-requested navigation properties will no longer receive
those keys — request the navigation with `$expand` to include it (see the #176 entry under Fixed).

### Added

- **`$top`/`$skip` on the `GetAll` (simple/`IEnumerable`) collection read path.** Previously
  rejected wholesale with `400 UnsupportedQueryOption` alongside `$filter`/`$orderby` - now applied
  as a post-materialization `Skip()`/`Take()`, the same class of operation as the already-live
  `$select`/`$expand`/`$count` on this path. `MaxTop` caps an explicit `$top` exactly like
  `GetQueryable` does (`400 InvalidQueryOption` when exceeded); an *omitted* `$top` is deliberately
  **not** implicitly capped to `MaxTop` the way `GetQueryable`'s is, since `GetAll` emits no
  `@odata.nextLink`/`$skiptoken` continuation and truncating silently would drop data with no way
  to retrieve the rest - see `docs/query-options.md` for the full rationale. `@odata.count`
  continues to reflect the pre-paging total. `$filter`/`$orderby` remain rejected.
- **Request-body documentation for write routes**, without attaching runtime `Accepts`/
  `IAcceptsMetadata` (which would short-circuit this framework's manual JSON-content-type/body
  handling and replace the OData error envelope with an empty 415 - see the comment near
  `OhDataEndpointFactory`'s PATCH route). A new `OhDataApiDescriptionProvider`
  (`IApiDescriptionProvider`, registered once idempotently inside `AddOhData`) reads a plain
  `OhDataRequestBodyMetadata` marker attached to each write route (entity POST/PUT/PATCH, nav-POST,
  property PUT/PATCH, `$ref` POST/PUT, bound/unbound actions) and adds the corresponding body
  parameter/schema to the route's `ApiDescription`. Every OpenAPI document generator built on
  ApiExplorer - Microsoft.AspNetCore.OpenApi, NSwag, and Swashbuckle - picks this up automatically;
  no per-package configuration needed. New public documentation-only types:
  `ODataPropertyWriteRequest<T>` (`{"value": ...}`) and `ODataRefWriteRequest`
  (`{"@odata.id": "..."}`). Ships in the core `EnGen.OhData.AspNetCore` package.
  Swashbuckle's `SwaggerGenerator` dereferences `ApiParameterDescription.ModelMetadata`
  unconditionally when building a request body's schema and throws a `NullReferenceException` if
  it's null (unlike Microsoft.AspNetCore.OpenApi/NSwag, which tolerate null and fall back to
  `.Type`); `OhDataApiDescriptionProvider` supplies a real `ModelMetadata` via the framework's own
  dependency-free `EmptyModelMetadataProvider` to avoid this.
- **Typed responses on read routes.** Bare, schema-less `.Produces(200)` calls are replaced with
  honest schemas across the board: collection GET (on `GetQueryable`, `GetAll`, and Priority-1) and
  collection-navigation GET now document `ODataCollectionResponse<T>` (a new public DTO mirroring
  the real `{"@odata.context", "@odata.count", "@odata.nextLink", "value"}` envelope - used for
  documentation only, never for actual serialization); structural-property GET documents
  `ODataPropertyResponse<T>`; navigation/entity `$ref` GET documents `ODataRefResponse`/
  `ODataRefCollectionResponse`; bound/unbound function and action results document the operation's
  actual declared return type (unwrapped from `Task<T>`/`ValueTask<T>`) instead of a bare `200`;
  `$count` and `$value` routes now declare their real `text/plain`/`application/octet-stream`
  content types instead of defaulting to `application/json`.
- **Read-path summaries** on collection GET routes via `WithSummary()`/`WithDescription()`, so
  generated docs make clear which read path backs an endpoint: `GetQueryable` → "List {Set}
  (queryable)" naming the live query options; `GetAll` → "List {Set} (simple read path)" noting
  that `$top`/`$skip`/`$select`/`$expand`/`$count` are applied server-side post-materialization
  while `$filter`/`$orderby` are not supported. Microsoft.AspNetCore.OpenApi reads these natively;
  the NSwag and Swashbuckle companion packages apply the same endpoint metadata explicitly, since
  neither doc stack surfaces it by default.
- New `OhData.AspNetCore.Swashbuckle.Tests` project (12 tests), matching the existing
  `OhData.AspNetCore.OpenApi.Tests`/`OhData.AspNetCore.NSwag.Tests` structure, so the Swashbuckle
  companion package now has direct test coverage against a real generated `swagger.json` (it
  previously had none).

### Fixed

- **Accept negotiation now follows RFC 7231 §5.3.2 media ranges and q-values (#182).** The
  group-level 406 filter matched the `Accept` header by substring (`accept.Contains("application/json")`
  / `"text/plain"` / `"*/*"`), which mishandled three cases: a media range such as `application/*`
  wrongly 406'd a JSON route (it matches `application/json`), `text/*` on `/$count` or
  `/{property}/$value` wrongly 406'd (it matches `text/plain`), and `application/json;q=0` -
  meaning "not acceptable" - wrongly returned `200`. The header is now parsed into media ranges via
  `MediaTypeHeaderValue.TryParseList`, with each candidate type resolved against its most specific
  matching range (exact `type/subtype` > `type/*` > `*/*`) and its q-value honored (absent q ⇒ 1.0,
  q=0 ⇒ that range is unacceptable); a request is acceptable when at least one range with q>0 matches
  a type the route can produce. Per-route producible sets are unchanged (`application/json`
  everywhere; plus `text/plain` on `/$count`, plus `text/plain`/`application/octet-stream` on
  `/$value`; `$metadata` stays exempt). An absent/empty `Accept` header still means "no constraint"
  → `200`; a present-but-unparseable header is treated as not-acceptable (`406`).
- **Nested `$expand` / `$select` clauses are now executed (#183).** A request such as
  `GET /Movies(1)?$expand=Studio($expand=Movies)` previously returned the expanded studio with an
  empty `"movies": []` - the second-level clause was parsed but never invoked against a handler, so
  no data was loaded (and nested `$select` inside `$expand` was likewise ignored). Stage-3 expand
  injection only ever iterated the *top-level* `ExpandedNavigationSelectItem`s. Expansion is now
  recursive (OData JSON Format v4.01 §11.2.4.2): after injecting a navigation's related entities,
  the framework resolves the navigation *target*'s own entity set from the EDM, loads its nested
  `$expand`'d navigations one level deeper, and repeats for arbitrary depth
  (`$expand=A($expand=B($expand=C))`). Each level honours its own nested `$select` projection, and a
  nested navigation that is *not* expanded is still omitted (no regression of #176/#179). Batching is
  preserved per level: a navigation exposing a `BatchHandler` is invoked once for the whole flattened
  set of entities at that level (rather than once per parent), with the per-entity `Handler` used as
  the fallback - so a fully batch-registered graph stays batched at every depth, while a per-entity
  graph is loaded per related entity (N+1 within that one navigation, unchanged from the top-level
  behaviour). A recursion guard (`MaxNestedExpandDepth = 12`) bounds pathological/adversarial nesting.
  To let requests reach that depth, the model-bound `$expand` depth written at EDM-build time is
  raised from Microsoft's default of 2 to the same guard value; the settings-level
  `MaxExpansionDepth` check remains disabled. Fixes both collection GET and single-entity `GetById`
  (which ride the same pipeline). Also corrects the `OmitUnexpandedNavigations` doc comment, which
  described nested-clause expansion that did not previously happen. Applies to `GetAll`,
  `GetQueryable`, and the Priority-1 `ODataQueryOptions` collection paths.
- **Bound/unbound function query parameters are now documented in OpenAPI (#181).** A function
  (`BindFunction`, e.g. `TopRated(int count = 10)`) reads its parameters from the query string, but
  its handler binds no minimal-API parameters, so ApiExplorer saw none of them and every generated
  document listed `parameters: []` for the operation even though `?count=2` demonstrably worked.
  Actions already got request-body documentation via `OhDataRequestBodyMetadata`; functions got
  nothing. Fixed symmetrically: a new plain `OhDataQueryParametersMetadata` marker (carrying each
  parameter's name, CLR type, and required/optional flag) is attached to every bound/unbound
  function route, and `OhDataApiDescriptionProvider` turns it into one query `ApiParameterDescription`
  per parameter (with a real `ModelMetadata`, `BindingSource.Query`, and `IsRequired` driven by
  whether the delegate parameter has a C# default). A trailing `CancellationToken` is excluded, and
  an entity-level function's leading key is skipped (it is already documented as a path parameter).
  All three doc stacks (Microsoft.AspNetCore.OpenApi, Swashbuckle, NSwag) render it automatically;
  no per-package configuration needed. New public documentation-only types: `OhDataQueryParameter`
  and `OhDataQueryParametersMetadata`, in the core `EnGen.OhData.AspNetCore` package.
- **Un-expanded navigation properties are no longer emitted on read responses (#176).** OhData
  serialised the full CLR entity graph, so a navigation that was not requested via `$expand` still
  surfaced in the payload - a collection nav as `"cast": []`, a single-valued nav as
  `"studio": null` - and an *expanded* entity even carried its own un-expanded navigations
  (e.g. `?$expand=Studio` returned `"studio":{...,"movies":[]}`). OData JSON Format v4.01 §4.5.1 /
  §11.2.4.2 require a non-expanded navigation to be OMITTED entirely, never rendered inline. A new
  EDM-model-driven pass runs after expansion on `GetById` and collection GET (`GetAll`/`GetQueryable`
  /Priority-1) and removes every navigation member not expanded at its own level, recursing into the
  expanded ones so a related entity never leaks its own navigations. Expanded navigations remain
  present and populated. **Response-shape change:** clients that relied on the empty `[]`/`null`
  navigation placeholders will no longer receive those keys - request the navigation with `$expand`
  to include it. Deep-insert `POST` responses are unaffected (they still echo the created graph per
  §11.4.2.2).
- **Un-expanded navigation omission now also covers nav-route and bound-operation reads (#179).**
  The #176 fix wired the omission into only the top-level reads (collection GET, `GetById`); three
  other serialization paths still emitted the full CLR graph, so an entity's shape depended on which
  route returned it. Now all read paths run the same EDM-driven pass: the single-valued navigation
  GET (`GET /Set(key)/{nav}`) strips the target entity type's own navigations; the navigation-collection
  GET (`GET /Set(key)/{nav}`) strips each item's navigations using the nav element type; and bound
  function/action results that return the entity set's own type (both the collection and single-entity
  branches of `WrapBoundOpResult`) strip navigations and — matching the normal collection/`GetById`
  paths — inject `@odata.etag` when `UseETag` is configured (previously dropped). Same spec basis as
  #176 (OData JSON Format v4.01 §4.5.1 / §11.2.4.2); expanded navigations remain present and populated.
- `NoMaxTop_TopDescriptionHasNoCap` (NSwag doc-generation tests): a `GetAll` profile that left
  `MaxTop` at its `EntitySetDefaults`-provided default (`1000`) was previously documented as having
  no `$top` cap at all, because the `GetAll` route's `OhDataQueryOptionsMetadata` hardcoded
  `MaxTop: null` regardless of the profile's actual value (a pre-existing docs/behavior mismatch
  this program fixes as a side effect of implementing `$top` on `GetAll`).
- **`406 Not Acceptable` on `/$count` and `/{property}/$value` when a client sends
  `Accept: text/plain`.** The group-level Accept-negotiation filter only permitted
  `application/json`/`*/*` (with a `$metadata`/`application/xml` exemption), but those two segments
  actually return `text/plain` (and `$value` returns `application/octet-stream` for `byte[]`).
  A client asking for the media type those routes *advertise in the OpenAPI document* — e.g.
  Swagger UI hitting `/{Set}/$count` — was therefore rejected. This was latent until the typed
  `$count`/`$value` response content types were corrected in the same Unreleased batch (before that,
  the routes mislabeled themselves as `application/json`, so UIs sent `Accept: application/json` and
  slipped through). The filter now exempts `/$count` (`text/plain`) and `/$value` (`text/plain`,
  `application/octet-stream`), mirroring the existing `$metadata` exemption. The narrow exemption is
  preserved: a genuinely unsupported type (e.g. `text/xml`) on those routes still returns `406`.
- **`$metadata` declared `encoding="utf-16"` while being served as UTF-8 (#180).** The CSDL was
  written through a `StringWriter` (whose `Encoding` is the CLR string's native UTF-16), so
  `XmlWriter` stamped `encoding="utf-16"` into the prolog, but the document went out as UTF-8 bytes
  under a charset-less `application/xml` header. A strict XML consumer - notably OData codegen
  clients that read `$metadata` - would decode the UTF-8 bytes as UTF-16 and fail. The CSDL is now
  written as UTF-8 (prolog reads `encoding="utf-8"`) and served as `application/xml; charset=utf-8`,
  so prolog, bytes, and header agree.
- **OpenAPI/omission edge-case polish (#184).** Four independent, low-severity fixes found in the
  pre-1.3.0 release-gate pass:
  - **`[JsonPropertyName]`-renamed navigations are now honored by omission and `$expand`.** The
    un-expanded-navigation omission pass and the `$expand` key-injection both derived a navigation's
    serialized JSON key from `PropertyNamingPolicy.ConvertName(navProp.Name)`, ignoring a per-property
    `[System.Text.Json.Serialization.JsonPropertyName("…")]` rename. A renamed navigation therefore
    leaked inline (omission looked for the policy-cased key and missed it), and `$expand` wrote a
    second, differently-cased key. The serialized key is now resolved from the CLR property's
    `[JsonPropertyName]` when present, falling back to the naming-policy name (so a symmetric
    `JsonNamingPolicy` such as snake_case still round-trips). Same spec basis as #176/#179 (OData JSON
    Format v4.01 §4.5.1 / §11.2.4.2).
  - **Key-property write stubs now declare the `{key}` OpenAPI path parameter.** The immutable-key
    `PUT`/`PATCH`/`DELETE /{Set}({key})/{KeyProperty}` stubs return a clean `400` but took no `key`
    lambda parameter, so their generated operation omitted the `{key}` path-parameter declaration its
    sibling `GET` carries — producing an OpenAPI document with an undeclared template variable. The
    stubs now take `(string key)`; the `400` behavior is unchanged.
  - **Action request-body schemas now expose their named parameters instead of an empty `{}`.**
    Bound/unbound action bodies were documented with `OhDataRequestBodyMetadata.BodyType =
    typeof(object)`, yielding a typeless schema whose parameters were conveyed only by the prose
    description. The body type is now a per-action synthesized POCO whose public properties mirror the
    action's parameters (each pinned with `[JsonPropertyName]` to the exact parameter name), so
    Microsoft.AspNetCore.OpenApi, Swashbuckle, and NSwag all render the real body shape (e.g.
    `{"rating": <number>}`). `CancellationToken` is excluded, and for entity-level actions the leading
    key is excluded. The prose description is retained alongside the schema.
  - **`$select=<nav>` (un-expanded) context URL — behavior kept, now documented and tested.**
    `GET Set(key)?$select=cast` (a navigation, not `$expand`'d) returns a content-less entity (only
    `@odata.*` annotations) whose `@odata.context` still lists `(cast)`. This is spec-defensible and
    is kept deliberately: selecting an un-expanded navigation selects its navigation *link*, which the
    default `odata.metadata=minimal` omits when convention-computable (OData JSON §4.5.9 / §11.2.4.1),
    while the context URL MUST echo the client's select list (§10.8). Dropping the `(cast)` projection
    (the rejected alternative) would emit `#Set/$entity`, falsely claiming the full entity was
    returned — strictly more misleading. Documented in code with the spec basis and covered by a test.

---

## [1.2.0] - 2026-07-17

### Added

- Project logo and package icon (`assets/icon.svg` + 128px `assets/icon.png`): a database cylinder
  shaped as a speech bubble saying "Oh", with the h doubling as the exclamation mark. Embedded in
  every package as `PackageIcon` via `Directory.Build.props`; shown in the README header; the
  publish quality gate's `IconMustBeSet` exclusion is removed (all meziantou rules now run).
- New `EnGen.OhData.AspNetCore.OpenApi` companion package: `OhDataOpenApiOperationTransformer`
  (an `IOpenApiOperationTransformer` for the built-in `Microsoft.AspNetCore.OpenApi` pipeline,
  net10.0) documents the OData query parameters on OhData endpoints, mirroring the Swashbuckle
  filter's gating exactly. Register via
  `AddOpenApi(o => o.AddOperationTransformer<OhDataOpenApiOperationTransformer>())`. Ships with
  its own test suite and `docs/openapi.md`. Note: the package deliberately floors a direct
  `Microsoft.OpenApi [2.7.5, 3)` dependency so consumers resolve above GHSA-v5pm-xwqc-g5wc
  (upstream `Microsoft.AspNetCore.OpenApi` still floors at the vulnerable 2.0.0).
- New `EnGen.OhData.AspNetCore.NSwag` companion package: `OhDataNSwagOperationProcessor`
  (an NSwag `IOperationProcessor`, net8.0/net10.0) with the same documentation behavior. Register
  via `AddOpenApiDocument(s => s.OperationProcessors.Add(new OhDataNSwagOperationProcessor()))`.
  Ships with its own test suite and `docs/nswag.md`.
- Both new test suites (25 tests) run in CI and in the publish gate; all five packages are packed
  and published by the release workflow (10 release assets).

### Changed

- `Microsoft.AspNetCore.OData` dependency floor raised from `[9.4.*, 10)` to `[9.5.*, 10)`
  (full 1,100-test suite verified against 9.5.0).
- Package validation now diffs `EnGen.OhData.AspNetCore`, `EnGen.OhData.Client`, and
  `EnGen.OhData.AspNetCore.Swashbuckle` against the published 1.1.0 API surface
  (`PackageValidationBaselineVersion=1.1.0`), so unintended breaking changes fail the build.

---

## [1.1.0] - 2026-07-17

### Added

- New `EnGen.OhData.AspNetCore.Swashbuckle` companion package containing
  `OhDataSwaggerOperationFilter` (same `OhData.AspNetCore` namespace and class name — migrating is
  a pure package-reference addition, no code changes). The filter documents the OData query
  parameters on collection endpoints in Swagger, exactly as before.

### Breaking

- `OhDataSwaggerOperationFilter` moved out of `EnGen.OhData.AspNetCore` into the new
  `EnGen.OhData.AspNetCore.Swashbuckle` package, removing the core package's
  `Swashbuckle.AspNetCore.SwaggerGen` (and transitive `Microsoft.OpenApi`) dependency. The core
  server package now works cleanly alongside `Microsoft.AspNetCore.OpenApi`, NSwag, any Swashbuckle
  major, or no OpenAPI stack at all. If you registered the filter, add the companion package —
  nothing else changes. Note: 1.0.0 was published and delisted the same day over this; 1.1.0 is the
  effective first release, which is why this break ships in a minor version.

---

## [1.0.0] - 2026-07-16

First public release. Includes the initial framework feature set (drafted as an unpublished
0.1.0 that never shipped) plus the full conformance, hardening, and performance train, plus a
post-release-prep audit fix wave (below) found before the tag was actually cut.

### Breaking

- **Query-option capability flags and property allowlists are now enforced at runtime**
  (release audit B1; OData 4.0 Minimal conformance item 7 — "parse the option or reject it").
  Previously `FilterEnabled`/`OrderByEnabled`/`SelectEnabled`/`ExpandEnabled`/`CountEnabled` and
  `FilterProperties`/`OrderByProperties`/`SelectProperties`/`ExpandProperties` only wrote EDM/
  Swagger advertisement metadata — on the `GetQueryable` and Priority-1 (`GetODataQueryable`)
  collection paths every option was applied regardless, so a "disabled" `$filter` still filtered
  and a non-allowlisted property could still be probed via `$filter`/`$orderby` (a data side
  channel for excluded columns). Now:
  - Collection GET (`GetQueryable`, Priority-1, and `GetAll` for its live `$select`/`$expand`/
    `$count` subset): a query option whose capability flag is disabled returns
    `400 Bad Request` (`UnsupportedQueryOption`, message names the option and the flag).
    Flags all default to `false` — **clients that previously sent (silently honored) options
    against profiles that never opted in will now receive 400**; set the corresponding flag to
    `true` to restore the old behavior, which is now advertised truthfully.
  - Property allowlists are enforced via the EDM's model-bound
    `NotFilterable`/`NotSortable`/`NotSelectable`/`NotExpandable` annotations
    (`FilterQueryOption`/`OrderByQueryOption`/`SelectExpandQueryOption.Validate`): an option
    referencing a non-allowlisted property returns `400` (`InvalidQueryOption`).
  - Standalone `GET /{Set}/$count`: `$filter` is gated by `FilterEnabled` (its route metadata
    already advertised exactly that) and the `FilterProperties` allowlist.
  - Navigation collection routes: previously-ignored system query options (`$filter`,
    `$expand`, `$search`, `$apply`, `$compute`, `$skiptoken`, `$deltatoken`) return `400`
    (`UnsupportedQueryOption`) instead of silently returning the full, unfiltered collection
    (release audit S1). `$select`/`$orderby`/`$skip`/`$top`/`$count` keep working as before.
  - `GET /{Set}({key})`: `$select` and `$expand` are gated by `SelectEnabled`/`ExpandEnabled`
    (the route's metadata always advertised them), and **`$expand` is now implemented on the
    single-entity route** (release audit S2) — previously it was silently ignored. Expansion
    reuses the collection pipeline (same serialization, `$select` interaction, and batch-handler
    support), emitting the single-entity context (`#Set/$entity`, or the projected
    `#Set(props)/$entity` form with `$select`).
  - `GetAll` route metadata now reports the profile's actual
    `SelectEnabled`/`ExpandEnabled`/`CountEnabled` flags instead of hardcoded `false`
    (those options are live on that path).
  Docs updated to match (`docs/query-options.md`, `docs/navigation-routing.md`,
  `docs/spec-compliance.md`).

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

- **Regression: nav-path `$filter`/`$orderby` (e.g. `Tags/any(t: t/Name eq 'X')`,
  `Category/Name`) incorrectly rejected with `400`**, introduced by the B1 property-allowlist
  enforcement above. `ValidatePropertyAllowlists` ran `Validate()` unconditionally, but the
  model-bound `Filterable`/`Sortable` annotations were only ever written for the profile's own
  root entity type — navigation-target types (e.g. `Tag`, `OrderLine`) carried no annotation at
  all, and Microsoft's model-bound validator treats an unannotated type's properties as
  `NotFilterable`/`NotSortable` by default once *any* validation runs, regardless of the root's
  own `FilterEnabled`/`OrderByEnabled` flags. Separately, when a root allowlist *was*
  configured, the validator also required the navigation property itself (e.g. `Tags`) to be
  present in the allowlist, so `FilterProperties(x => x.Name)` blocked `Tags/any(...)` even
  though `Tags` was never meant to be gated by a *structural*-property allowlist. Fixed with two
  changes: (1) `OhDataBuilder` now marks every navigation-target type the model discovers —
  every structural type that isn't a root profile's own entity type — as fully
  filterable/sortable/selectable/expandable/countable, since these types have no allowlist
  surface of their own in 1.0; (2) `EntitySetProfile` now unions this entity's own navigation
  property names into a configured `FilterProperties`/`OrderByProperties` allowlist before
  handing it to the model builder, so a configured allowlist only ever restricts *structural*
  properties, never navigation traversal. The root-type security property is unchanged: a
  non-allowlisted *structural* property (e.g. `Id` when only `Name` is allowlisted) still
  returns `400`.
- Empty, malformed, or non-object JSON bodies on four route families no longer return a raw,
  envelope-less `500` (release audit B2). Entity-bound actions, collection-bound actions,
  unbound actions, and `$ref` POST/PUT all read their request body by hand (needed for correct
  `Content-Type`/malformed-body error formatting) but were missing the guards already applied to
  POST/PUT/PATCH. `$ref` POST/PUT in particular had no guard at all — even an empty body 500'd.
  All four now: reject a non-JSON `Content-Type` with `415`; reject malformed JSON and non-object
  JSON (array/string/number/bool/null) with `400`; both as the standard OData error envelope.
  Actions with zero parameters are unaffected — they never read the body.
- Unhandled exceptions thrown by any handler — as opposed to an `ODataError` a handler
  deliberately returns — no longer produce an empty, envelope-less `500` (release audit S7). A
  new group-level endpoint filter, registered alongside the existing `OData-Version`/
  `OData-MaxVersion` filters, catches any exception that reaches it and returns the standard
  error envelope (`code: "InternalServerError"`, a generic message — never the exception's own
  message or stack trace) while logging the real exception for operators to diagnose. Does not
  affect routes that return an `ODataError` result (the normal case for every other 4xx/5xx in
  this framework) or startup-time validation exceptions (those happen once, in `MapOhData()`,
  before any request is served).
- **Startup validation for unbound-operation route collisions** (release audit S5). An unbound
  function/action (`AddFunction`/`AddAction`) sharing a name with another unbound operation of the
  same kind, or with an entity set that registers the same `(route, HTTP method)` pair (a
  collection `GET` for a function, `POST` for an action), previously registered without error and
  only surfaced as an `AmbiguousMatchException` `500` the first time the route was hit — the
  entire collection route was dead with zero startup diagnostics. `MapOhData()` (specifically,
  resolving the `OhDataRegistration`) now throws `InvalidOperationException` naming the colliding
  operation/entity-set and the shared route, matching the existing bound-operation collision-guard
  idiom. Comparisons are case-insensitive, matching ASP.NET Core's default route-template matching.
- **Startup validation for entity-bound operation signatures** (release audit S6).
  `BindEntityFunction`/`BindEntityAction` now validate, at bind time, that the handler delegate
  accepts the entity key as its first parameter (typed `TKey`, besides an optional trailing
  `CancellationToken`). Previously a zero-parameter handler registered fine and 500'd at request
  time with an uncaught `IndexOutOfRangeException` (the framework places the parsed route key into
  `args[0]` unconditionally); a handler whose first parameter wasn't `TKey` likewise registered
  fine and failed only at request time via a `DynamicInvoke` mismatch. Both now throw
  `InvalidOperationException` at startup, naming the operation, its declaring entity set, and the
  expected signature. Corrected a stale doc comment on `BoundOperationDefinition.Parameters` that
  claimed the leading key parameter was excluded for entity-level operations — it isn't; only a
  trailing `CancellationToken` is stripped.
- **String-keyed entity-id URLs are now canonical OData key syntax** (release audit S4). Entity-id
  URLs built from a CLR key value — `POST` `201` `Location`/`Content-Location`, `OData-EntityId`,
  and `@odata.id` (on `POST`, and now also rebuilt from the parsed key on `GetById`/`PUT`/`PATCH`,
  `$ref`, and nav-`POST` responses) — previously formatted string keys with plain
  `string.Format(..., "{0}", key)`: no surrounding single quotes and no URL-encoding. A string key
  containing a space, single quote, or unicode character produced an invalid or wrong URL (e.g.
  `/Things(abc)` instead of `/Things('abc')`); embedded single quotes weren't doubled. A new
  shared `ODataEntityKeyUrlFormatter` (mirroring the client's `ODataKeyFormatter`) now quotes and
  percent-encodes string keys, doubling embedded single quotes consistently with
  `ODataKeyParser`'s unescaping, so every entity-id URL the server emits round-trips back through
  key parsing. `int`/`Guid`/other non-string keys are unaffected — formatting is unchanged.
- **Client `ODataKeyFormatter` no longer truncates fractional seconds on `DateTime`/
  `DateTimeOffset` keys** (flagged during the PR #140 filter-translation fix as a leftover; release
  audit S10/B3). Previously formatted with a fixed whole-seconds (`"...ssZ"`/`"...sszzz"`) pattern,
  silently dropping any sub-second precision — a key formatted from an entity's actual
  (sub-second-precision) key value no longer matched the entity it was formatted from, producing a
  spurious `404`. Now delegates to the same `ODataDateTimeLiteralFormatter` `FilterTranslator` uses
  for `$filter` literals (full precision, trimmed of trailing zeros, `DateTimeKind`
  normalization/`Z` suffix per PR #140's `FormatDateTime` semantics), so a key literal and a
  `$filter` literal for the same value now format identically.
- **Client: `DateTime` with `Kind=Local`/`Unspecified` in `$filter` no longer emits an
  offset-less literal the server rejects with 400** (release audit B3). `FilterTranslator`
  previously appended a `Z` suffix only for `DateTimeKind.Utc`; `Local`/`Unspecified` values (e.g.
  ordinary `DateTime.Now` comparisons) produced a bare literal
  (`2026-07-15T08:53:09.5190818`) that the Microsoft URI parser — and therefore any OData 4.0
  server, including OhData — rejects (Part 2 §5.1.1.9 requires an explicit `Z`/offset on every
  `Edm.DateTimeOffset` literal). Both kinds now always emit a `Z` suffix: `Local` values are
  converted to their UTC instant first (`ToUniversalTime()`); `Unspecified` values are treated as
  already UTC and emitted as-is with `Z` (matching the convention most ORMs/serializers use for
  "no timezone info" values, e.g. System.Text.Json's own `DateTime` round-tripping). `Utc` values
  are unaffected. A new shared `ODataDateTimeLiteralFormatter` also preserves full sub-second
  precision (trimmed of trailing zeros) instead of the previous whole-seconds truncation; see the
  `ODataKeyFormatter` entry below. See `docs/client.md#literal-type-support` for the exact
  per-`DateTimeKind` semantics.
- **Client: referencing the outer lambda parameter inside `Any`/`All` no longer silently
  translates to `null`** (release audit B4). `x => x.Tags.Any(t => t.Name == x.Name)` previously
  produced `Tags/any(t: t/Name eq null)` — the sub-translator couldn't resolve the outer
  parameter and fell through to a `null` literal, silently returning the wrong rows instead of
  failing loudly. Outer-parameter references inside `Any`/`All` lambdas are now resolved against
  the enclosing scope and translated to the correct nested-property path
  (`Tags/any(t: t/Name eq Name)`); a reference that still can't be resolved throws
  `NotSupportedException` instead of degrading to `null`.
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
- README test counts corrected to the actual suite sizes (release audit B5); `docs/etags.md`,
  `docs/bound-operations.md`, and `docs/property-access.md` corrected for the conditional-request
  and error-envelope fixes above; `docs/authorization.md` reconciled on the `$metadata`/service-doc
  anonymous-access story and documents the unbound-operation auth story; `docs/query-options.md`
  and `docs/client.md` updated for the capability-enforcement and client-translation fixes above

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

[Unreleased]: https://github.com/en-gen/OhData/compare/v1.7.0...develop
[1.7.0]: https://github.com/en-gen/OhData/releases/tag/v1.7.0
[1.6.0]: https://github.com/en-gen/OhData/releases/tag/v1.6.0
[1.5.0]: https://github.com/en-gen/OhData/releases/tag/v1.5.0
[1.4.0]: https://github.com/en-gen/OhData/releases/tag/v1.4.0
[1.3.0]: https://github.com/en-gen/OhData/releases/tag/v1.3.0
[1.2.0]: https://github.com/en-gen/OhData/releases/tag/v1.2.0
[1.1.0]: https://github.com/en-gen/OhData/releases/tag/v1.1.0
[1.0.0]: https://github.com/en-gen/OhData/releases/tag/v1.0.0
