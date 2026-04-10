using Microsoft.Extensions.DependencyInjection;

namespace OhData.AspNetCore.Versioning;

public static class ServiceCollectionVersioningExtensions
{
    /// <summary>
    /// Registers a named OhData version with path-segment prefix.
    /// Equivalent to: services.AddOhData(name, o => o.WithPrefix(prefix).Configure(configure))
    /// </summary>
    public static IServiceCollection AddOhDataVersion(
        this IServiceCollection services,
        string versionName,
        string prefix,
        Action<OhDataBuilder> configure)
        => services.AddOhData(versionName, o => { o.WithPrefix(prefix); configure(o); });
}
