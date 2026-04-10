# Authorization

OhData integrates with standard ASP.NET Core authentication and authorization. There is no OhData-specific auth system — the framework applies ASP.NET Core's own `RequireAuthorization` to the registered endpoints.

## Setup

Auth middleware must be configured before `MapOhData()`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => { ... });
builder.Services.AddAuthorization();

// ...

app.UseAuthentication();
app.UseAuthorization();
app.MapOhData();
```

## Declaring requirements on a profile

Inside the profile constructor, call one of:

```csharp
// Any authenticated user (valid identity, any role/claim)
RequireAuthorization();

// Named ASP.NET Core policy (defined via AddAuthorization(options => options.AddPolicy(...)))
RequireAuthorization("AdminPolicy");

// One or more roles (comma-joined, standard ASP.NET Core role check)
RequireRoles("Admin", "SuperAdmin");
```

Only one requirement can be active per profile. Calling multiple methods overwrites the previous.

## Scope of application

Authorization applies to **all operations** on the entity set. There is no per-operation granularity in the current version — if you need some operations to be open and others protected, use two separate profiles with different entity set names and delegate to the same underlying service.

## How it works internally

`EntitySetProfile` stores an `AuthorizationConfig` record (in `OhData.Abstractions`, no ASP.NET Core dependency):

```csharp
public sealed class AuthorizationConfig
{
    public bool Required { get; init; }
    public string? Policy { get; init; }
    public string[]? Roles { get; init; }
}
```

At endpoint registration, `OhDataEndpointFactory` calls `.RequireAuthorization(...)` on each `RouteHandlerBuilder`, which is the same call you'd make manually on any minimal API endpoint.

## Unauthenticated behaviour

When auth is required and the request has no valid identity, ASP.NET Core returns `401 Unauthorized`. When a valid identity lacks the required role or policy claim, it returns `403 Forbidden`. OhData does not intercept or alter this behaviour.
