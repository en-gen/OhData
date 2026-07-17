using System;
using Microsoft.Extensions.DependencyInjection;

namespace OhData.AspNetCore.Versioning;

/// <summary>
/// Extension methods on <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// for registering versioned OhData services.
/// </summary>
public static class ServiceCollectionVersioningExtensions
{
    /// <summary>
    /// Registers a named OhData version with a path-segment prefix. Equivalent to calling
    /// <c>services.AddOhData(versionName, o => o.WithPrefix(prefix); configure(o))</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="versionName">Registration key for this version, e.g. <c>"v1"</c>.</param>
    /// <param name="prefix">URL prefix for all entity set routes, e.g. <c>"/api/v1"</c>.</param>
    /// <param name="configure">Callback to configure profiles and other options for this version.</param>
    public static IServiceCollection AddOhDataVersion(
        this IServiceCollection services,
        string versionName,
        string prefix,
        Action<OhDataBuilder> configure)
        => services.AddOhData(versionName, o => { o.WithPrefix(prefix); configure(o); });
}
