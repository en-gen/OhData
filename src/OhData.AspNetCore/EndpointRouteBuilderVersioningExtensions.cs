using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using OhData;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Extension methods on <see cref="IEndpointRouteBuilder"/> for mapping versioned OhData endpoints.
/// </summary>
public static class EndpointRouteBuilderVersioningExtensions
{
    /// <summary>
    /// Maps all OData endpoints for the named version registration.
    /// Equivalent to <c>app.MapOhData(versionName)</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <param name="versionName">
    /// The version name used when registering with <c>AddOhDataVersion</c>,
    /// e.g. <c>"v1"</c>.
    /// </param>
    public static RouteGroupBuilder MapOhDataVersion(this IEndpointRouteBuilder app, string versionName)
        => app.MapOhData(versionName);
}
