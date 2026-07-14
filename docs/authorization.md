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

Authorization applies to **all operations** on the entity set - GET, POST, PUT, PATCH, DELETE, navigation routes, and bound operations all get the same requirement. Per-operation granularity is not supported.

If you need some operations open and others protected, split them across two profiles with different entity set names that delegate to the same underlying service.

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

## `$metadata` and the service document are always anonymous

`GET /{prefix}` (the service document) and `GET /{prefix}/$metadata` are mapped before per-profile
authorization is applied, so they remain reachable without authentication even when every
registered profile requires auth. **This is intentional, by design** - it is not a gap to be
closed:

- OData tooling (client code generators, API explorers, `$metadata`-driven clients) expects to be
  able to fetch the service document and metadata document anonymously to discover the shape of
  the service before authenticating against individual entity sets.
- Neither document exposes entity data - only the schema (entity sets, types, properties,
  operations) that `$metadata` was designed to advertise.

If your service's schema itself is sensitive and you need `$metadata`/the service document
protected, that is not currently supported as a per-registration opt-in. This is a possible future
feature; there is no workaround today beyond fronting the endpoints with your own middleware or
reverse-proxy rule outside of OhData's routing.
