# Authorization

OhData integrates with standard ASP.NET Core authentication and authorization - there is no OhData-specific auth system. The framework applies ASP.NET Core's own `RequireAuthorization` to the registered endpoints based on what you declare in the profile.

## Middleware setup

Configure auth middleware in `Program.cs` before `MapOhData()`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => { /* configure token validation */ });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireClaim("role", "admin"));
});

// ...

app.UseAuthentication();
app.UseAuthorization();

app.MapOhData();
```

## Declaring requirements on a profile

Inside the profile constructor, call one of:

```csharp
public class ProductProfile : EntitySetProfile<int, Product>
{
    public ProductProfile() : base(x => x.Id)
    {
        // Any authenticated user (valid identity, any role or claim)
        RequireAuthorization();

        // Named ASP.NET Core authorization policy
        RequireAuthorization("AdminOnly");

        // One or more roles - user must have at least one (OR semantics)
        RequireRoles("Admin", "SuperAdmin");

        GetAll = (ct) => ...;
    }
}
```

`RequireAuthorization(policy)` and `RequireRoles(roles)` may each be called once per profile and combine with AND semantics (both must pass). Calling either method twice throws `InvalidOperationException` at startup.

## Scope

`RequireAuthorization()`/`RequireRoles()` apply to **all operations** on the entity set - GET, POST, PUT, PATCH, DELETE, navigation routes, and bound operations all get the same requirement. This is the simplest model and remains the default.

For per-operation granularity (reads open, writes gated; deletes admin-only; etc.), use `ConfigureAuthorization(...)` instead - see the next section.

## Per-operation authorization

`ConfigureAuthorization(auth => …)` authorizes each operation **category** independently:

```csharp
public class OrderProfile : EntitySetProfile<int, Order>
{
    public OrderProfile() : base(x => x.Id)
    {
        ConfigureAuthorization(auth => auth
            .Read(r   => r.AllowAnonymous())                     // catalog reads are public
            .Create(c => c.RequirePolicy("Editors"))
            .Update(u => u.RequireRole("Editors")                // requirements combine with AND,
                          .RequireClaim("dept", "sales"))        //   like AuthorizationPolicyBuilder
            .Delete(d => d.RequireRole("Admin"))
            .Invoke("Approve", i => i.RequirePolicy("Approvers")) // one named bound operation
            .Invoke(i => i.RequireAuthenticatedUser()));         // all other bound operations

        GetAll = ct => ...;
        // ...
    }
}
```

**Categories** (an `OhDataOperation` maps every route to exactly one):

| Category | Routes |
|---|---|
| `Read` | collection/by-id/navigation/property GETs, `$count`, `$value`, `$ref` GET |
| `Create` | `POST` to the collection; `POST` to a collection navigation |
| `Update` | `PUT`/`PATCH` on an entity, property, or navigation; adding/setting a link (`POST`/`PUT` `$ref`); **and** the mutations that leave the row intact — clearing a property (`DELETE …/{Property}`) and removing a link (`DELETE …/$ref`) |
| `Delete` | `DELETE` that removes a whole entity |
| `Invoke` | bound function/action invocation |

Selectors: `.Read(...)`, `.Create(...)`, `.Update(...)`, `.Delete(...)`, `.Writes(...)` (= create+update+delete), `.All(...)` (every category), `.Invoke(...)` (all bound ops), and `.Invoke("Name", ...)` (one named bound operation, which takes precedence over a generic `.Invoke(...)`). Later category rules win on overlap.

**`Invoke("Name", ...)` matches the operation name case-insensitively, and an unmatched name is refused at startup.** Both halves are [#525](https://github.com/en-gen/OhData/issues/525). The name is matched the way the route that serves the operation is matched, so `.Invoke("stamp", ...)` governs a `Stamp` function; before the fix that comparison was ordinal, so a miscased rule silently matched nothing and the operation fell back to the generic `.Invoke(...)` rule — or, with no generic rule, to **no requirement at all**. Because a *misspelled* name evaporates the same way and no comparer can rescue it, `app.MapOhData()` now throws `InvalidOperationException` when a named `Invoke` rule does not resolve to a bound operation the profile declares, naming the rule and listing the declared operations. There is no valid configuration in which a rule targets an operation that does not exist.

**Exactly one `Invoke(name, …)` rule per bound operation, or startup throws.**
[#546](https://github.com/en-gen/OhData/issues/546). Named rules are resolved last-write-wins, so
once #525 made the match case-insensitive, two rules differing only in case collapsed onto each
other and **the order they were declared in decided whether the operation was protected**:

```csharp
.Invoke("Stamp", i => i.RequireRole("admin")).Invoke("stamp", i => i.AllowAnonymous())
// anonymous GET …/Stamp  ->  200        (protected before #525)

.Invoke("stamp", i => i.AllowAnonymous()).Invoke("Stamp", i => i.RequireRole("admin"))
// anonymous GET …/Stamp  ->  401
```

`MapOhData()` now throws `InvalidOperationException` when two named `Invoke` rules on one profile
resolve to the same bound operation, naming both spellings and the operation — **including two
rules spelled identically**, since the mechanism (the earlier rule silently discarded) and the
consequence are the same. The grouping uses the same comparer the resolution does. **Generic
`Invoke(…)` rules are unaffected**: last-write-wins is the design there, and `.All(…)` followed by
`.Invoke(…)` is a documented refinement idiom. Remedy: keep exactly one `Invoke` rule per
operation.

**Per-category requirements** mirror `AuthorizationPolicyBuilder` and combine with **AND**:

| Method | Meaning |
|---|---|
| `.RequireAuthenticatedUser()` | any authenticated identity |
| `.RequireRole("A", "B")` | at least one of the roles (OR within; AND across requirements) |
| `.RequireClaim("type", "v1", "v2")` | a claim of `type`, optionally restricted to the given values |
| `.RequirePolicy("Name")` | a named ASP.NET Core policy (registered via `AddAuthorization`) |
| `.AllowAnonymous()` | explicitly anonymous — **exclusive**, cannot be combined with any `Require*` |

### Defaults and composition with group-level auth

- A category with **no rule** emits nothing, so it **inherits** any group-level
  `MapOhData().RequireAuthorization()` - and is anonymous when there is none. This matches
  ASP.NET Core's "anonymous unless you say otherwise" posture and keeps global auth composable.
  **It also fails open**, which is why `app.MapOhData()` now logs a `Warning` naming any category
  that ends up anonymous while the same profile requires authorization elsewhere - see
  ["The composition"](#the-composition-securing-everything-you-can-name-still-leaves-holes-you-did-not)
  below. The migration that trips it is
  `RequireAuthorization()` -> `ConfigureAuthorization(a => a.Read(...).Writes(...))`: it reads as a
  refinement and is a **widening**, because nothing names `Invoke` and every bound operation on the
  set drops to anonymous.
- An explicit `.AllowAnonymous()` **overrides** a group-level requirement for that category (it is the
  standard `AllowAnonymousAttribute`). This is deliberate and it is how you punch a public hole in an
  otherwise-gated surface - but read the composition section below before relying on group-level auth
  as a floor, because it is not one.

  > **The same call means something different on an unbound operation, and that is deliberate (#572).**
  > `AddFunction(op, a => a.AllowAnonymous())` does **not** emit `AllowAnonymousAttribute`; there it
  > means *"I am not adding a requirement"*, never *"I am removing yours"*, so a host-applied
  > `app.MapOhData().RequireAuthorization()` still covers it. One interface,
  > `ICategoryAuthorizationBuilder`, is used in both places, so the spelling is identical and the
  > behaviour is opposite. Both startup warnings now say which one you are getting. If you only mean
  > *"this category needs no requirement of its own"*, name the requirement you intended instead -
  > on a category this call is the stronger statement. See
  > [unbound operations](#unbound-functions-and-actions-carry-their-own-requirement) for the other half.
- `$metadata` and the service document are **not** entity-set-scoped, so `ConfigureAuthorization` does
  not reach them; protect them with group-level auth (see below), same as the legacy model. Unbound
  functions and actions are not entity-set-scoped either, but they now have their **own**
  per-operation surface - see
  ["Unbound functions and actions"](#unbound-functions-and-actions-carry-their-own-requirement).

### One model per profile

`ConfigureAuthorization(...)` and the legacy `RequireAuthorization()`/`RequireRoles()` are mutually
exclusive on a single profile - calling both throws `InvalidOperationException` at startup. Choose one.

## Resource-based (instance-level) authorization

The requirements above are coarse - they answer "can this *kind* of user touch this operation." To
answer "can this user touch *this row*" (owner checks, tenant isolation), add `.RequireResource()` to a
category. OhData loads the `{key}` entity and evaluates ASP.NET Core's native resource-based
authorization against it - you write a standard handler:

```csharp
// profile:
ConfigureAuthorization(auth => auth
    .Read(r   => r.RequireResource())
    .Update(u => u.RequireRole("Editors").RequireResource()));   // must be an Editor AND own the row

// your handler (switch on the operation name):
public sealed class OrderAuthorizationHandler
    : AuthorizationHandler<OperationAuthorizationRequirement, Order>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, Order order)
    {
        if (req.Name == OhDataOperations.Read.Name) ctx.Succeed(req);
        if (req.Name == OhDataOperations.Update.Name && order.OwnerId == ctx.User.FindFirst("sub")?.Value)
            ctx.Succeed(req);
        return Task.CompletedTask;
    }
}
// Program.cs:  services.AddScoped<IAuthorizationHandler, OrderAuthorizationHandler>();
```

`OhData.AspNetCore.OhDataOperations` exposes the five framework `OperationAuthorizationRequirement`
instances (`Read`/`Create`/`Update`/`Delete`/`Invoke`); the handler's resource *type* selects the
entity set, and `requirement.Name` selects the operation. Use `.RequireResource("PolicyName")` instead
to evaluate a **named policy** (carrying your own custom `IAuthorizationRequirement`s, with data) with
the entity as the resource.

Key points:

- **The resource is the `{key}` entity.** For property/navigation/`$ref` routes the resource is the
  parent entity in the path, so every one of *this profile's* routes is covered by *this profile's*
  rule - none of them escapes it. **Create** is the exception: `POST` to a collection is checked
  against the *incoming* (pre-persist) entity from the body (there's no stored row yet); `POST` to a
  navigation is checked against the parent.
  <br>Read that literally: it says the parent entity is checked, not the *related* entity. A
  navigation route is authorized as an operation on the parent, so if the navigation's target type is
  *also* exposed as its own, more strictly protected entity set, **that set's rule is not applied
  here** - see [Authorization is per-profile and does not compose across a
  navigation](#authorization-is-per-profile-and-does-not-compose-across-a-navigation) below.
- **Collection reads are not resource-checked** (there's no single instance) - filter those in the
  query itself. `$metadata`/service-document/unbound operations are never resource-checked.
- **It composes with the coarse requirements (AND)** - both must pass. `.RequireResource()` alone is
  not an endpoint gate, so an anonymous request *reaches the handler* and is denied `403` (which enables
  "read if public **or** owner"); pair it with `.RequireAuthenticatedUser()` if you want anonymous
  requests to get `401` instead.
- **Fail-closed.** If no matching handler is registered, the requirement is never satisfied →
  everything returns `403`. Opting in without a handler denies; it never silently allows.
- **Cost & requirements.** A resource-checked route performs one `GetById` load (in the auth filter) to
  fetch the entity for the check, so `.RequireResource()` on any route carrying a `{key}` segment
  **requires a `GetById` handler** (enforced at startup). That is Read/Update/Delete, and also
  `Create` when the profile registers a navigation-`post` route (`POST /{Set}({key})/{Nav}`) and
  `Invoke` when it registers an entity-bound function or action — those two used to pass startup and
  then fail every request with a 500 (#486). The two collection-level members of those categories
  need no `GetById` and are unaffected: the collection `POST` evaluates its `Create` requirement
  against the deserialized model directly, and a collection-bound operation's route has no key to
  load by. Not compatible with `AllowUpsert` create-on-`PUT` (a missing entity returns `404` before
  the handler runs).

### Fallback

If two entity sets need entirely separate surfaces (not just different requirements per verb), you can
still split them across two profiles with different entity set names that delegate to the same
underlying service.

## Response behaviour

When auth is required and the request has no valid identity, ASP.NET Core returns `401 Unauthorized`. When a valid identity lacks the required role or policy claim, it returns `403 Forbidden`. OhData does not intercept or modify these responses.

## Global auth (all entity sets)

Apply auth to all routes at once using the `RouteGroupBuilder` returned by `MapOhData()`:

```csharp
// Every OhData route requires an authenticated user
app.MapOhData().RequireAuthorization();

// Named registrations:
app.MapOhData("v1").RequireAuthorization("V1Policy");
```

Per-profile `RequireAuthorization()` applies in addition to any group-level requirement.

**This includes the service document, `$metadata`, and unbound functions/actions** (see the next
two sections) - they are mapped on the exact same top-level `RouteGroupBuilder` that `MapOhData()`
returns, so a group-level `.RequireAuthorization()`/`.RequireRoles(...)` call protects them too,
same as every entity-set route. Group-level auth is the mechanism to reach for if your service's
schema itself needs to be behind auth (see below).

**It is not a floor, though.** A group-level requirement covers every route that does not say
otherwise, and a profile can say otherwise: a category-level `.AllowAnonymous()` emits the standard
`AllowAnonymousAttribute`, which ASP.NET Core's authorization middleware honours over any
`IAuthorizeData` the group contributed. So "wrap the whole group" secures everything **except what a
profile has deliberately opened**. See the next section.

## Authorization is per-profile and does not compose across a navigation

**A navigation is authorized by the profile that *declares* it, never by the profile that owns its
target entity set.** If `Customer` has a navigation to `Ticket`, and `Tickets` is separately
registered as its own entity set with `RequireRoles("support")`, then a caller who may read
`Customers` can read those same ticket rows through the navigation - the `support` requirement is
never evaluated on that path.

This governs the whole navigation family, and `$expand` with it:

| route | authorized by |
|---|---|
| `GET /Customers({key})/Tickets` and its `/$count` | the `Customers` profile |
| `GET/POST/PUT/DELETE /Customers({key})/Tickets/$ref` | the `Customers` profile |
| `POST /Customers({key})/Tickets` (create a related entity) | the `Customers` profile |
| `GET /Customers?$expand=Tickets`, `GET /Customers({key})?$expand=Tickets` | the `Customers` profile |
| `GET /Tickets`, `GET /Tickets({key})`, … | the `Tickets` profile |

Note that the writes are on that list too. A `$ref` `POST`/`PUT`/`DELETE` and a navigation `POST`
run under the declaring profile's rule exactly as the reads do, so they can create and re-link rows
in an entity set whose own profile would have refused the same caller.

### The declaration is the opt-in

There is no per-navigation authorization switch, and none is planned: **writing the
`HasMany`/`HasOptional`/`HasRequired` declaration is itself the decision to expose that data through
this entity set.** A bare `HasMany(x => x.Tickets)` with no handler and no route is enough - it
registers nothing, but it puts the navigation in the EDM, and `$expand` serves it. (An *undeclared*
navigation that the OData convention builder discovered on its own is different: OhData never loads,
routes or writes one, so it is not reachable at all. `MapOhData()` warns about those separately.)

### This matches `Microsoft.AspNetCore.OData`

It is not an OhData-specific gap. `Microsoft.AspNetCore.OData` contains no authorization code at
all, and structurally it cannot behave otherwise: its navigation action (`GetOrders`) is routed onto
the **parent's** controller, so an `[Authorize]` attribute on `OrdersController` is never consulted
for `/Customers({key})/Orders`, and `$expand` is a pure projection with no second dispatch to
authorize. Per-endpoint authorization under `$expand` is the norm across OData servers, not a
divergence.

### The startup warning

`MapOhData()` emits one `Warning` per declared navigation whose target entity set's profile requires
something the declaring profile does not - naming the declaring set, the navigation, the target set,
and the exact requirements that will not be applied. It compares per operation category, so a target
guarded only on writes does not warn about a navigation that exposes only reads, and it stays silent
when the two profiles are equally strict, when the target is *less* strict, and when no target
profile is registered at all.

It is a warning and never a failure. Enforcing the target's rule would break the ordinary scoped
-navigation pattern (a customer-scoped `Orders` navigation beside a separately registered,
admin-gated `Orders` set is correct code, not a bug), and it is not even well-defined when two
entity sets share one EDM type.

### The remedy

Two options, and which one is right depends on whether reaching that data from the parent is
intended:

1. **Configure the requirement on the declaring profile.** If the related data is sensitive wherever
   it is reached from, put the same `RequireAuthorization`/`RequireRoles`/`ConfigureAuthorization`
   call on the profile that declares the navigation. Per-operation rules let you match the target's
   shape rather than over-protecting the parent - e.g. leave the parent's own reads open and require
   the role on `Writes` if the target only guards writes.
2. **Split the surface.** Remove the navigation declaration (or `Ignore()` it) so the protected rows
   are not reachable from the unprotected parent at all, and let clients read them through the
   protected entity set. If only *some* related rows should be reachable, give the navigation an
   explicit handler and scope the query inside it - a delegate-backed navigation is your own code,
   so it can apply whatever row-level rule the parent context implies.

If reaching the data from the parent *is* intended - the scoped-navigation case - nothing needs to
change; the warning is telling you a decision was made, not that it was made wrongly.

## `$metadata` and the service document are anonymous by default - unless group-level auth is used

`GET /{prefix}` (the service document) and `GET /{prefix}/$metadata` are mapped directly on the
top-level route group, *before* any per-profile authorization groups are nested under it. Two
consequences follow, and both are true at the same time (they are not in tension - they answer two
different questions):

- **Per-profile auth never reaches them.** `RequireAuthorization()`/`RequireRoles()` declared
  inside a profile constructor only applies to that profile's own nested route group, not to the
  shared top-level group both documents live on. So if you only ever configure auth per-profile -
  the common case - the service document and `$metadata` stay reachable without authentication
  *even when every registered profile requires auth*. **This is intentional, by design:** OData
  tooling (client code generators, API explorers, `$metadata`-driven clients) expects to discover
  the shape of a service anonymously before authenticating against individual entity sets, and
  neither document exposes entity data - only the schema (entity sets, types, properties,
  operations) `$metadata` was designed to advertise.
- **Group-level auth does reach them**, because it's applied to the same `RouteGroupBuilder` these
  two routes are mapped on (see "Global auth" above - `app.MapOhData().RequireAuthorization()`
  returns `401`/`403` for `$metadata` and the service document exactly like any other route in the
  group). If your service's schema itself is sensitive, this is the workaround: put
  `.RequireAuthorization(...)` on the `MapOhData()` call itself rather than (or in addition to)
  per-profile. There is currently no way to protect `$metadata`/the service document while leaving
  the rest of the surface open to anonymous per-profile-only configuration - it's an all-or-nothing
  choice between "group auth also covers schema discovery" and "schema discovery is always open."

## Unbound functions and actions carry their own requirement

`AddFunction`/`AddAction` (registered on `OhDataBuilder`, not inside a profile - see
[bound-operations.md](bound-operations.md#unbound-functions-and-actions)) are mapped on the same
top-level route group as `$metadata` and the service document, for the same reason: they aren't
tied to any single entity set, so there's no profile-level auth group for them to sit inside.

Pass an `authorize` lambda to give one its own requirement. It takes the same
`ICategoryAuthorizationBuilder` the `ConfigureAuthorization` categories take:

```csharp
builder.Services.AddOhData(o => o
    .AddAction(ResetAll,  a => a.RequireRole("admin"))
    .AddFunction(Report,  a => a.RequirePolicy("Reporting"))
    .AddFunction(Ping,    a => a.AllowAnonymous()));   // deliberately public, and says so
```

- **`RequireResource()` is refused** (`ArgumentException` at registration). Resource-based
  authorization evaluates the requirement against the entity loaded from a `{key}` segment, and an
  unbound operation has neither a key nor an entity set - the rule could only ever be a silent no-op.
- **`AllowAnonymous()` states intent; it does not remove anyone else's requirement.** Unlike the
  category-level `.AllowAnonymous()` on a profile, it does **not** emit `AllowAnonymousAttribute`, so
  it cannot tunnel the operation out from under a group-level requirement the host applied. It
  silences the startup warning below and nothing else.
- **Per-profile `RequireAuthorization()`/`RequireRoles()` still cannot protect an unbound operation.**
  Even if every entity set in the registration requires auth, `GET /{prefix}/{UnboundFunction}` and
  `POST /{prefix}/{UnboundAction}` are anonymous unless you say otherwise, either here or at group
  level. That configuration is now named at startup - see the next section.
- **Group-level auth also protects them**, via the same mechanism as `$metadata` above: applying
  `.RequireAuthorization()`/`.RequireRoles(...)` to the `RouteGroupBuilder` `MapOhData()` returns
  covers every unbound function/action in that registration along with everything else on the group.

## The composition: securing everything you can name still leaves holes you did not

Three behaviours documented above are individually correct and compose into a quiet fail-open. Each
one on its own reads as a reasonable default; together they mean that **securing every profile you
can name still leaves anonymous routes you did not name**, and until 1.7.0 nothing surfaced that.

| # | Behaviour | Status |
|---|---|---|
| 1 | An unbound function/action is not entity-set-scoped, so no per-profile requirement reaches it | Fixed: it can carry its own requirement, and an unstated one is warned about |
| 2 | A `ConfigureAuthorization` category with no rule emits no requirement | Warned about |
| 3 | A category-level `.AllowAnonymous()` overrides a host-applied group requirement | **Unchanged and unwarned - by design** |

### The startup warning

`app.MapOhData()` logs one `Warning` per anonymous subject - per unbound operation, and per
(entity set, category) - when **all** of the following hold:

- the route ends up with no authorization requirement at all,
- nothing stated that it should be anonymous (no `.AllowAnonymous()` on the category, none on the
  unbound operation, none on the group), and
- the registration requires authorization **somewhere else**, so it is a service with a hole rather
  than a service that is simply public.

The message names the subject, the configuration that produced it, the requirement to add, and the
explicit `AllowAnonymous()` spelling that states the opposite intent and stops the warning. A
registration with no authorization anywhere is never reported, and neither is one where the host
applied `app.MapOhData().RequireAuthorization()` - the check runs at endpoint-build time precisely so
that the mitigation this document recommends really does silence it.

### Why seam 3 is not warned about, and not changed

A category-level `.AllowAnonymous()` really does defeat `app.MapOhData().RequireAuthorization()`. So
group-level auth is a **backstop for routes nobody has an opinion about**, not a floor under the
whole surface.

This is ASP.NET Core's own behaviour, not OhData's: `.AllowAnonymous()` emits the standard
`AllowAnonymousAttribute`, and the authorization middleware short-circuits on any endpoint carrying
`IAllowAnonymous` regardless of the `IAuthorizeData` its group contributed, and regardless of the
order the two were applied in. The framework control in `Issue487AuthSeamTests` demonstrates it with
no OhData in the picture - a plain `MapGroup` with `RequireAuthorization()` applied *after* its
routes serves the `.AllowAnonymous()` one with `200` while its sibling answers `401`. Diverging from
that would surprise every developer who knows ASP.NET Core, and would make the "public catalog reads
inside an otherwise-gated service" design un-expressible.

It is not warned about either, because `.AllowAnonymous()` **is** the way to express that design.
Warning on it would fire on correct configuration with no way to silence it, which is worse than no
warning at all.

**So if you rely on group-level auth as a global backstop, audit the `.AllowAnonymous()` calls in
your profiles.** They are the holes, they are deliberate, and they are the only ones the startup
warning will not tell you about.
