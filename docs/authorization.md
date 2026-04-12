# Authorization

OhData integrates with standard ASP.NET Core authentication and authorization — there is no OhData-specific auth system. The framework applies ASP.NET Core's own `RequireAuthorization` to the registered endpoints based on what you declare in the profile.

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

        // One or more roles — user must have at least one (OR semantics)
        RequireRoles("Admin", "SuperAdmin");

        GetAll = (ct) => ...;
    }
}
```

`RequireAuthorization(policy)` and `RequireRoles(roles)` may each be called once per profile and combine with AND semantics (both must pass). Calling either method twice throws `InvalidOperationException` at startup.

## Scope

Authorization applies to **all operations** on the entity set — GET, POST, PUT, PATCH, DELETE, navigation routes, and bound operations all get the same requirement. Per-operation granularity is not supported.

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
