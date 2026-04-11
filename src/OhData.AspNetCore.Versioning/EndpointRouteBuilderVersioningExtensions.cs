using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace OhData.AspNetCore.Versioning;

public static class EndpointRouteBuilderVersioningExtensions
{
    public static RouteGroupBuilder MapOhDataVersion(this IEndpointRouteBuilder app, string versionName)
        => app.MapOhData(versionName);
}
