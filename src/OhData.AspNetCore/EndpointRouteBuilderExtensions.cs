using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OhData;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods on <see cref="IEndpointRouteBuilder"/> for mapping OhData endpoints.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps all OData endpoints derived from registered entity set profiles,
    /// including the service document (<c>{prefix}</c>), <c>$metadata</c>, and per-entity-set
    /// GET / GET({key}) / POST / PUT({key}) / PATCH({key}) / DELETE({key}) routes.
    /// Returns the <see cref="RouteGroupBuilder"/> so callers can chain additional policies
    /// (e.g. <c>.RequireAuthorization()</c>).
    /// </summary>
    public static RouteGroupBuilder MapOhData(this IEndpointRouteBuilder routes)
        => routes.MapOhData(OhDataDefaults.DefaultRegistrationName);

    /// <summary>
    /// Maps all OData endpoints for the named OhData registration.
    /// </summary>
    public static RouteGroupBuilder MapOhData(this IEndpointRouteBuilder routes, string name)
    {
        var registration = routes.ServiceProvider.GetRequiredKeyedService<OhDataRegistration>(name);

        // Construct anything that validates its configuration on construction, so the failure lands
        // here rather than on the first request. Nothing registers one unless a companion package
        // does, so this is a no-op on a core-only app.
        foreach (var validated in routes.ServiceProvider.GetServices<IOhDataStartupValidated>())
        {
            _ = validated;
        }

        return OhDataEndpointFactory.MapAll(routes, registration);
    }
}
