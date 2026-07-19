# Design prototype — #199 Per-operation authorization (B + C)

> Status: **design/RFC** — not yet implemented. Combines two layers: **C** an in-profile
> fluent, operation-level gate, and **B** an optional resource-based (instance-level) check.
> This document is the "option 1" prototype: the full route→operation table and the
> `OperationAuthRule` / requirement contracts, for review before any code lands.

## 1. Why the current model isn't enough

Today authorization is a single per-profile `AuthorizationConfig(bool Required, string? Policy,
IReadOnlyList<string>? Roles)` (in [`IEntitySetEndpointSource`](../../src/OhData.AspNetCore/IEntitySetEndpointSource.cs)),
declared via `RequireAuthorization()` / `RequireAuthorization(policy)` / `RequireRoles(...)`, and the
factory applies it once to an `entityAuthGroup` that wraps **every** route for the set
([`OhDataEndpointFactory.MapEntitySet`](../../src/OhData.AspNetCore/OhDataEndpointFactory.cs) ~L1762).
It is all-operations, all-or-nothing. The documented escape hatch is "split reads and writes into two
profiles pointing at the same service" — which is real friction.

Two *different* questions get conflated when people ask for "per-operation auth":

- **Operation-level** — "writes require the `Editors` policy; deletes require `Admin`." Coarse,
  role/policy-based, doesn't need to know *which* row. → **Layer C**.
- **Instance-level** — "you may edit an order **only if you own it**." Depends on the specific
  entity + the current user + the operation together. Role gates can't express it. → **Layer B**.

This design ships both, as independent-but-composable layers. A set can use only C (the common case),
only B (rare), or both.

## 2. Design constraints (unchanged rules)

1. **Profiles stay ASP.NET-Core-agnostic.** The profile stores auth as *plain data* — policy
   **names** and role **names** only, never `IAuthorizationRequirement`, `AuthorizeAttribute`, or
   delegates that close over ASP.NET types. The factory is the only thing that touches
   `RequireAuthorization` / `IAuthorizationService`. (Same rule the current `AuthorizationConfig`
   honors.)
2. **Backward compatible.** Existing `RequireAuthorization()` / `RequireRoles()` keep working
   byte-identically — they lower to `Authorize(a => a.All()....)`. If a profile declares no
   per-operation rules, the factory takes the exact current single-group path.
3. **No new runtime cost when unused.** Layer C compiles to per-route `RequireAuthorization`
   metadata (zero per-request overhead beyond what ASP.NET Core already does). Layer B costs one
   `AuthorizeAsync` call — and, for writes, one entity load — only on sets that opt in.
4. **`MapGroup` slash rule.** Per-operation auth cannot use nested route groups (all entity-set
   routes share the `""`-template group by necessity — see CLAUDE.md). Instead the factory applies
   the resolved requirement to each route's own `RouteHandlerBuilder` at map time. It already knows
   each route's operation category because it maps every route explicitly.

## 3. Operation taxonomy → auth category

The factory maps each route explicitly, so it assigns an auth **category** per route at registration
(richer than the runtime `ClassifyOperation` telemetry labels, which can't tell a bound function from
a collection GET). Five categories, expressed as a `[Flags]` enum:

```csharp
[Flags]
public enum OhDataOperation
{
    None   = 0,
    Read   = 1 << 0,
    Create = 1 << 1,
    Update = 1 << 2,
    Delete = 1 << 3,
    Invoke = 1 << 4,                       // bound functions + actions
    Write  = Create | Update | Delete,
    All    = Read | Write | Invoke,
}
```

Full route table (mirrors the route list in CLAUDE.md):

| Route | Method | Category | Note |
|---|---|---|---|
| `/{Set}` | GET | **Read** | collection |
| `/{Set}/$count` | GET | **Read** | |
| `/{Set}({key})` | GET | **Read** | by-id |
| `/{Set}({key})/{nav}` | GET | **Read** | nav read |
| `/{Set}({key})/{nav}/$count` | GET | **Read** | |
| `/{Set}({key})/{nav}/$ref` | GET | **Read** | read links |
| `/{Set}({key})/{Property}` | GET | **Read** | property read |
| `/{Set}({key})/{Property}/$value` | GET | **Read** | raw value |
| `/{Set}` | POST | **Create** | |
| `/{Set}({key})/{nav}` | POST | **Create** | create related entity |
| `/{Set}({key})` | PUT / PATCH | **Update** | |
| `/{Set}({key})/{Property}` | PUT / PATCH | **Update** | |
| `/{Set}({key})/{nav}` | PUT / PATCH | **Update** | |
| `/{Set}({key})/{nav}/$ref` | POST / PUT | **Update** | add / set link (entity survives) |
| `/{Set}({key})` | DELETE | **Delete** | remove the row |
| `/{Set}({key})/{Property}` | DELETE | **Update** | *clear a field — the entity survives* |
| `/{Set}({key})/{nav}/$ref` | DELETE | **Update** | *remove a link — the entity survives* |
| `/{Set}({key})/{nav}` | DELETE | **Delete** | delete related entity |
| `/{Set}/{Function}` | GET | **Invoke** | collection-bound function |
| `/{Set}({key})/{Function}` | GET | **Invoke** | entity-bound function |
| `/{Set}/{Action}` | POST | **Invoke** | collection-bound action |
| `/{Set}({key})/{Action}` | POST | **Invoke** | entity-bound action |

> **Design decision (recommended default):** categorize by *what is mutated*, not by HTTP verb.
> Clearing a property value (`DELETE /{Set}({key})/{Property}`) and removing a link
> (`DELETE .../$ref`) leave the row intact, so they fall under **Update**, not **Delete** —
> "delete" is reserved for removing a whole entity. This means "can edit" implies "can clear a
> field," and "can delete" is the narrower, more dangerous grant. Flag this for confirmation
> (§8, Q3); it's the one place a reasonable person might expect verb-based mapping instead.

**Out of scope for per-operation auth** (unchanged, group-level only): `$metadata`, the service
document, and unbound functions/actions. They aren't entity-set-scoped, so there's no profile to hang
a category on. `app.MapOhData().RequireAuthorization(...)` still covers them exactly as documented in
[authorization.md](../authorization.md#metadata-and-the-service-document-are-anonymous-by-default---unless-group-level-auth-is-used).

## 4. Layer C — in-profile fluent operation-level gate

### 4.1 Author-facing API

```csharp
public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile() : base(x => x.Id)
    {
        ConfigureAuthorization(auth => auth
            .Read(r   => r.AllowAnonymous())                          // catalog reads are public
            .Create(c => c.RequirePolicy("Editors"))
            .Update(u => u.RequireRole("Editors")                     // ANDed requirements, like
                          .RequireClaim("dept", "sales")             //   AuthorizationPolicyBuilder
                          .RequireResource())                        //   + Layer-B instance check
            .Delete(d => d.RequireRole("Admin"))
            .Invoke("Approve", i => i.RequirePolicy("Approvers"))     // one named bound op
            .Invoke(i => i.RequireAuthenticatedUser()));             // all other bound ops

        GetAll  = ct => ...;
        // ...
    }
}
```

**Outer** — `ConfigureAuthorization(Action<IAuthorizationRuleBuilder>)`: a single-use builder
(§4 decision, was §8-Q4). Category selectors, each taking a **nested per-category lambda**:

| Selector | Covers |
|---|---|
| `.Read(r => …)` | `OhDataOperation.Read` |
| `.Create(c => …)` / `.Update(u => …)` / `.Delete(d => …)` | the single category |
| `.Writes(w => …)` | `Create \| Update \| Delete` |
| `.Invoke(i => …)` | all bound operations (fallback) |
| `.Invoke("Name", i => …)` | one named bound function/action |
| `.All(a => …)` | everything (what legacy `RequireAuthorization()` lowers to) |

**Inner** — the per-category scope **mirrors `AuthorizationPolicyBuilder`**: requirements
*accumulate* and combine with **AND** (must satisfy all), exactly like the real policy builder. We
store the shape as plain data (§4.2) and replay it onto the real builder in the factory (§4.3):

| Inner method | Mirrors `AuthorizationPolicyBuilder` | How the factory applies it |
|---|---|---|
| `.RequireAuthenticatedUser()` | `RequireAuthenticatedUser()` | inline policy |
| `.RequireRole("A", "B")` | `RequireRole(...)` — at least one (OR within, AND across) | inline policy |
| `.RequireClaim("type", "v1", "v2")` | `RequireClaim(...)` | inline policy |
| `.RequirePolicy("Name")` | (named policy reference) | `route.RequireAuthorization("Name")` |
| `.RequireResource()` | — (Layer-B extension, §5) | `AuthorizeAsync(user, entity, req)` |
| `.AllowAnonymous()` | (terminal) | `route.AllowAnonymous()` |

`.AllowAnonymous()` is exclusive — combining it with any `Require*` in the same category throws at
build time. We deliberately **do not** mirror `RequireAssertion(Func<…>)`: it's a delegate over
ASP.NET types, so it can't be plain data — imperative, instance-aware checks go through Layer B
(§5) instead. Later category rules win on overlap; any category with no rule defaults to
**anonymous** (today's behavior — nothing is protected unless you say so).

### 4.2 Data model the profile stores (no ASP.NET types)

Each inner method appends a plain-data requirement record; the category rule holds the accumulated
list (mirroring how `AuthorizationPolicyBuilder` accumulates requirements):

```csharp
public enum AuthRequirementKind { AuthenticatedUser, Role, Claim, Policy, Resource }

public sealed record AuthRequirement(
    AuthRequirementKind Kind,
    string? Name = null,                       // policy name, or claim type
    IReadOnlyList<string>? Values = null);     // roles, or accepted claim values

public sealed record OperationAuthRule(
    OhDataOperation Operations,
    bool AllowAnonymous,                        // exclusive: true ⇒ Requirements is empty
    IReadOnlyList<AuthRequirement> Requirements,
    string? BoundOperationName = null);         // non-null only for .Invoke("Name", …)

// New member on IEntitySetEndpointSource:
IReadOnlyList<OperationAuthRule>? OperationAuthorization { get; }   // null ⇒ use legacy single-group path
```

`ConfigureAuthorization(Action<IAuthorizationRuleBuilder>)` on `EntitySetProfile` accumulates these
records — pure data, **no `Microsoft.AspNetCore.*` reference in the profile**. Calling both
`ConfigureAuthorization(...)` and the legacy `RequireAuthorization()`/`RequireRoles()` on one profile
throws at startup (pick one model).

### 4.3 How the factory applies it

For each route it maps, the factory resolves the effective rule for that route's category (honoring
bound-op-name specificity) and replays the plain-data requirements onto the **real**
`AuthorizationPolicyBuilder` — this is the one place `Microsoft.AspNetCore.Authorization` is touched:

```csharp
static void ApplyOperationAuth(RouteHandlerBuilder route, OperationAuthRule? rule)
{
    if (rule is null || rule.AllowAnonymous) { route.AllowAnonymous(); return; }

    // Named policies apply as separate RequireAuthorization("name") calls (they stack → AND).
    foreach (var req in rule.Requirements.Where(r => r.Kind == AuthRequirementKind.Policy))
        route.RequireAuthorization(req.Name!);

    // Inline requirements (role/claim/authenticated) replay onto one AuthorizationPolicyBuilder.
    var inline = rule.Requirements.Where(r => r.Kind is not AuthRequirementKind.Policy
                                                   and not AuthRequirementKind.Resource).ToList();
    if (inline.Count > 0)
    {
        route.RequireAuthorization(pb =>
        {
            foreach (var req in inline)
            {
                switch (req.Kind)
                {
                    case AuthRequirementKind.AuthenticatedUser: pb.RequireAuthenticatedUser(); break;
                    case AuthRequirementKind.Role:  pb.RequireRole(req.Values!.ToArray()); break;
                    case AuthRequirementKind.Claim: pb.RequireClaim(req.Name!, req.Values ?? Array.Empty<string>()); break;
                }
            }
        });
    }
    // AuthRequirementKind.Resource is Layer B — handled inside the route handler (§5), not here.
}
```

No new route group; the requirements ride the existing per-route builder. Response behavior is
unchanged (`401` no identity, `403` wrong role/claim/policy — ASP.NET Core owns these). Multiple
`RequireAuthorization` calls on one endpoint stack with AND semantics, so a named policy plus inline
role/claim requirements all must pass.

## 5. Layer B — resource-based instance-level check (opt-in)

Layer C can't say "edit *your own* orders." Layer B delegates that to ASP.NET Core's native
**resource-based authorization**, using the framework's own `OperationAuthorizationRequirement` (in
`Microsoft.AspNetCore.Authorization.Infrastructure`) — the exact type the official CRUD
resource-auth sample uses. OhData loads the row and calls `IAuthorizationService.AuthorizeAsync(user,
entity, requirement)`; the consumer writes a standard `AuthorizationHandler<
OperationAuthorizationRequirement, TModel>` and switches on `requirement.Name`.

**The profile never names `OperationAuthorizationRequirement`.** It says `.RequireResource()` (plain
data); OhData maps the category → the matching requirement internally. The framework type appears only
in OhData's factory (already ASP.NET-coupled) and in the consumer's own handler — the profile-agnostic
rule holds.

### 5.1 Opting in

```csharp
ConfigureAuthorization(auth => auth
    .Read(r => r.RequireAuthenticatedUser())
    .Writes(w => w.RequireResource()));    // ← run instance-level auth for Create/Update/Delete
```

Two tiers:

- **`.RequireResource()`** — OhData invokes `AuthorizeAsync(user, entity, OhDataOperations.<verb>)`
  with the framework requirement; you write one handler and switch on `req.Name`.
- **`.RequireResource("PolicyName")`** — OhData invokes `AuthorizeAsync(user, entity, "PolicyName")`;
  your named policy carries any custom `IAuthorizationRequirement`(s), with data — one handler each.

Either appends an `AuthRequirement(AuthRequirementKind.Resource, Name: policyOrNull)` to the rule and
composes with the coarse inline requirements in the same lambda — e.g.
`.Update(u => u.RequireRole("Editors").RequireResource())` means "must be an Editor **and** pass the
per-row check."

### 5.2 What OhData ships (the framework `OperationAuthorizationRequirement` pattern)

```csharp
namespace OhData.AspNetCore.Authorization;

// Framework-provided requirement type; OhData just names the five operations, mirroring the
// canonical ASP.NET Core resource-based-auth `Operations` sample.
public static class OhDataOperations
{
    public static readonly OperationAuthorizationRequirement Read   = new() { Name = nameof(Read) };
    public static readonly OperationAuthorizationRequirement Create = new() { Name = nameof(Create) };
    public static readonly OperationAuthorizationRequirement Update = new() { Name = nameof(Update) };
    public static readonly OperationAuthorizationRequirement Delete = new() { Name = nameof(Delete) };
    public static readonly OperationAuthorizationRequirement Invoke = new() { Name = nameof(Invoke) };
}
```

The consumer's handler is copy-paste from the official docs, scoped to their model:

```csharp
public sealed class OrderAuthorizationHandler
    : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, Order order)
    {
        var userId = ctx.User.FindFirst("sub")?.Value;
        if (req.Name == OhDataOperations.Read.Name) ctx.Succeed(req);
        if (req.Name == OhDataOperations.Update.Name && order.OwnerId == userId) ctx.Succeed(req);
        if (req.Name == OhDataOperations.Delete.Name && ctx.User.IsInRole("Admin")) ctx.Succeed(req);
        return Task.CompletedTask;             // no Succeed ⇒ 403
    }
}
// Program.cs:  services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
```

The **resource type discriminates the entity set** (`AuthorizationHandler<…, Order>` only fires for
`Order`), so `OperationAuthorizationRequirement` needs only its `Name` — no entity-set field.
*Edge case:* two profiles over the same CLR model share the handler; to split them, use
`.RequireResource("PolicyName")` with distinct policies.

### 5.3 Where the factory invokes it, and the entity-load question

| Category | Resource passed to `AuthorizeAsync` | Requirement | Entity load cost |
|---|---|---|---|
| **Read** (by-id, nav, property) | the entity already being returned | `OhDataOperations.Read` | none — the read loads it anyway |
| **Update** (PUT/PATCH, property, link) | the **existing** entity | `OhDataOperations.Update` | **one extra `GetById`** before mutating |
| **Delete** | the **existing** entity | `OhDataOperations.Delete` | **one extra `GetById`** before deleting |
| **Create** | the **incoming** deserialized entity (pre-persist) | `OhDataOperations.Create` | none |
| **Invoke** (entity-bound) | the bound entity (loaded from `{key}`) | `OhDataOperations.Invoke` | one `GetById` |
| **Invoke** (collection-bound) | *no instance* — resource auth N/A, use Layer C only | — | — |

Instance auth on **writes requires `GetById` to exist** on the profile (to load the existing row).
If a profile opts a write category into `.RequireResource()` without a `GetById` handler, that's a
**startup `InvalidOperationException`** (fail fast, same style as the existing collision guards in
`MapOhData`). Collection reads (`GET /{Set}`) are excluded from B — you can't resource-check a set you
haven't materialized; filter those in the query itself.

Failure → `403 Forbidden` with the OData error envelope. Success → the handler proceeds exactly as
today.

### 5.4 No handler registered → fail closed

If a category opts into `.RequireResource()` but the app registers **no**
`AuthorizationHandler<OperationAuthorizationRequirement, TModel>`, ASP.NET Core's
`AuthorizeAsync` returns *not succeeded* (a requirement is satisfied only if some handler calls
`context.Succeed`; zero handlers ⇒ nothing succeeds). OhData therefore returns **403** — the secure
default. Opting into a check and providing none **denies**; it never silently allows.

To keep that from being a silent lockout, OhData emits a **best-effort startup warning** ("entity set
`{Set}` opted into `.RequireResource()` but no `AuthorizationHandler<OperationAuthorizationRequirement,
{TModel}>` appears registered — resource-checked operations will return 403"). It is a **warning, not
a throw**, because handler registration can't be detected with certainty (open-generic handlers, bare
`IAuthorizationHandler` registrations, decorators, scoped lifetimes all evade a reliable check) — a
false-positive hard failure would block a correctly-configured app. Contrast §5.3's `GetById` guard,
which *is* a hard throw because OhData knows that condition for certain. The client always sees a clean
`403`; the "looks like no handler" detail stays in the server log — never leak internal config.

The warning (and the whole fail-closed path) applies **only to categories that carry an
`AuthRequirementKind.Resource` requirement**. A category configured `.AllowAnonymous()` — or left at
the default no-rule (anonymous) state, or gated only coarsely by role/claim — never calls
`AuthorizeAsync`, needs no handler, and never triggers the warning. Anonymous means open.

### 5.5 Anonymous requests hitting a resource-checked category → 403, not 401

`.RequireResource()` is **not** an endpoint gate — the factory skips `Resource` in
`ApplyOperationAuth` (§4.3), so it's evaluated inside the handler, after the auth middleware. A
category gated *only* by `.RequireResource()` therefore leaves the endpoint open: an **unauthenticated
request reaches the handler** with an anonymous principal, the handler runs, and a non-`Succeed`
outcome is a **`403`** (not a `401` challenge — resource-based auth returns allow/deny, it doesn't
challenge).

This is deliberate — it's what enables "**read if the row is public _or_ you're the owner**," where an
anonymous user legitimately passes for public rows. OhData does **not** auto-require an authenticated
user on `.RequireResource()`, because that would break the public-or-owner pattern. If instead you want
anonymous requests bounced to log in (`401`) rather than forbidden (`403`), pair the resource check
with a coarse endpoint gate:

```csharp
.Update(u => u.RequireAuthenticatedUser()   // endpoint gate → anonymous = 401, before the handler
              .RequireResource());          // handler → authenticated-but-not-owner = 403
```

## 6. How B and C compose

They answer different questions and both must pass:

```
request
  │
  ├─ Layer C  (endpoint RequireAuthorization)  → 401 / 403  "can this kind of user touch this operation at all?"
  │
  └─ handler runs, loads entity if needed
        └─ Layer B  (AuthorizeAsync w/ entity) → 403          "can THIS user touch THIS row?"
```

- **C only** (the 90% case): role/policy gates per verb. One-liner per category, zero runtime cost.
- **B only**: rare — open to all authenticated users at the endpoint, filtered per-row in a handler.
- **C + B**: coarse gate *and* per-row rule (e.g. `.Update().RequirePolicy("Editors")` **and**
  `.RequireResource()` → must be an Editor *and* own the row).

## 7. Backward compatibility

- `AuthorizationConfig` and the single-`entityAuthGroup` path stay. They're used **iff**
  `OperationAuthorization` is `null` (no `Authorize(...)` call). No behavior change for any existing
  profile.
- Legacy `RequireAuthorization()`/`RequireRoles()` are kept as thin sugar; internally they may either
  keep the old path or lower to `.All()` — either way the emitted requirement is identical. (Recommend
  keeping the old path untouched to minimize churn, and treating `Authorize(...)` as the strictly-new
  surface.)
- Docs: [authorization.md](../authorization.md) §"Scope" ("Per-operation granularity is not
  supported" / "split into two profiles") gets rewritten; the two-profile workaround becomes a
  fallback note.

### API shape — SETTLED (this iteration)

- **Outer:** single-use `ConfigureAuthorization(auth => …)` builder (not the DI-colliding
  `AddAuthorization` name, not the escape-prone bare-return builder).
- **Inner:** nested per-category lambdas (`.Update(u => …)`), each mirroring
  `AuthorizationPolicyBuilder` — requirements **accumulate and AND** (`RequireRole` / `RequireClaim`
  / `RequireAuthenticatedUser` / `RequirePolicy`), plus `.RequireResource()` (Layer B) and the
  exclusive `.AllowAnonymous()`. `RequireAssertion` intentionally omitted → use Layer B.

## 8. Decisions

1. **Read default** — ✅ **RESOLVED: anonymous by default.** Same posture as ASP.NET Core; nothing is
   protected unless a rule says so. Non-breaking.
2. **`$metadata`/service-doc/unbound ops** — ✅ **RESOLVED: always anonymous / group-level-only.**
   `$metadata` and the service document must stay anonymous (client tooling expects to discover the
   schema without auth); unbound ops reachable only via group-level `MapOhData().RequireAuthorization()`.
3. **Property-clear & link-remove categorization** — ✅ **RESOLVED: `Update`.** `DELETE …/{Property}`
   and `DELETE …/$ref` mutate a surviving row, so they need the *Update* grant; `Delete` is reserved
   for removing a whole entity.
4. **Create resource for Layer B** — the resource passed to `AuthorizeAsync` on POST is the entity
   **deserialized from the request body, before persistence** (there is no stored row yet). Lets the
   handler validate the new row's contents (e.g. "only create rows you'll own"). *Awaiting confirm;
   recommend: pass the incoming entity — costs no extra load and is the only resource available.*
5. **`.RequireResource()` naming** — ✅ **RESOLVED: keep `.RequireResource()`** (matches ASP.NET Core's
   "resource-based authorization" term).

### Follow-ups (separate issues, not #199) — reflect auth in the API-explorer docs

Because Layer C applies real `RequireAuthorization(...)` **per route**, the authorization requirement
is standard ASP.NET Core endpoint metadata, so an OpenAPI generator can surface it **per operation**.
Out of scope for #199 (server-side enforcement); enabled by its per-route metadata. Filed for the
**1.5.0** milestone against the OpenApi + NSwag companion packages:

- **#219 (baseline)** — reflect the per-operation *security requirement* + `401`/`403` responses,
  referencing whatever security **scheme** the app already defined. OhData never defines the scheme
  (the app's identity setup). Layer B is *not* expressible in OpenAPI beyond "may return 403."
- **#220 (rich, opt-in)** — an operation filter that documents the *specific* roles/claims/policies a
  route requires, drawn from OhData's `AuthRequirement` data (a differentiator — named-policy filters
  can only show an opaque policy name). Must be opt-in: claim **values** in public docs are a mild
  info-disclosure surface.

## 9. Implementation plan (once §8 is settled)

| # | Change | Files |
|---|---|---|
| 1 | `OhDataOperation` enum, `AuthRequirementKind`, `AuthRequirement`, `OperationAuthRule`, and the `OhDataOperations` static class (framework `OperationAuthorizationRequirement`s) | new `Authorization/` types in `OhData.AspNetCore` |
| 2 | `IAuthorizationRuleBuilder` (outer) + per-category `ICategoryAuthorizationBuilder` (inner, `AuthorizationPolicyBuilder`-mirroring) + `ConfigureAuthorization(...)` on `EntitySetProfile`; accumulate rules; guard against mixing with legacy; single-use enforcement | `EntitySetProfile.cs` |
| 3 | `OperationAuthorization` member | `IEntitySetEndpointSource.cs`, `EntitySetProfile.cs` |
| 4 | `ApplyOperationAuth(route, rule)` + resolve-rule-per-category; call at each `Map*` site; skip the single-group path when rules present | `OhDataEndpointFactory.cs` |
| 5 | Layer B: `AuthorizeAsync` invocation in the by-id/write/invoke handlers behind `AuthRequirementKind.Resource`; startup guard requiring `GetById` | `OhDataEndpointFactory.cs` |
| 6 | Startup validation: `Authorize` + legacy both set → throw; resource-write without `GetById` → throw | `OhDataEndpointFactory.cs` (`MapOhData` validation pass) |
| 7 | Docs rewrite | `docs/authorization.md`, `CHANGELOG.md` |

**Test plan** (new `PerOperationAuthTests.cs`): a matrix asserting each category's route returns
`401/403/200` under anonymous / wrong-role / right-role identities; `.AllowAnonymous()` override;
legacy-still-works regression; `Authorize`+legacy throws; resource-write-without-`GetById` throws;
Layer B owner-check (owner `200`, non-owner `403`) for Read/Update/Delete/Create; C+B combined.
Perf: confirm C adds no per-request allocation beyond `RequireAuthorization` metadata; B adds exactly
one `AuthorizeAsync` (+ one `GetById` on writes) — include the k6 + BenchmarkDotNet deltas in the PR.

## 10. Scope split for the 1.4.0 milestone

If B lands slower than C, ship **C first** (self-contained, closes the headline "per-operation auth"
gap) and land **B** as a follow-up — the `OperationAuthRule`/`AuthRequirementKind.Resource` shape is designed so
B is additive (no rework of C). Both are targeted at #199; the PR(s) reference `Fixes #199`.
