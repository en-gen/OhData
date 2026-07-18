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
- An explicit `.AllowAnonymous()` **overrides** a group-level requirement for that category (it is the
  standard `AllowAnonymousAttribute`).
- `$metadata`, the service document, and unbound functions/actions are **not** entity-set-scoped, so
  `ConfigureAuthorization` does not reach them; protect them with group-level auth (see below), same as
  the legacy model.

### One model per profile

`ConfigureAuthorization(...)` and the legacy `RequireAuthorization()`/`RequireRoles()` are mutually
exclusive on a single profile - calling both throws `InvalidOperationException` at startup. Choose one.

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

## Unbound functions and actions have no per-operation auth - only group-level auth reaches them

`AddFunction`/`AddAction` (registered on `OhDataBuilder`, not inside a profile - see
[bound-operations.md](bound-operations.md#unbound-functions-and-actions)) are mapped on the same
top-level route group as `$metadata` and the service document, for the same reason: they aren't
tied to any single entity set, so there's no profile-level auth group for them to sit inside.
Consequently:

- **Per-profile `RequireAuthorization()`/`RequireRoles()` cannot protect an unbound operation** -
  even if every entity set in the registration requires auth, `GET /{prefix}/{UnboundFunction}`
  and `POST /{prefix}/{UnboundAction}` remain anonymous. There is no per-operation opt-in for
  unbound functions/actions today (an API-shape gap, not a bug - if you need an authenticated
  unbound operation with other operations left open, model it as a bound operation on a
  dedicated, auth-required entity set instead).
- **Group-level auth does protect them**, via the same mechanism as `$metadata` above: applying
  `.RequireAuthorization()`/`.RequireRoles(...)` to the `RouteGroupBuilder` `MapOhData()` returns
  covers every unbound function/action in that registration along with everything else on the
  group.
