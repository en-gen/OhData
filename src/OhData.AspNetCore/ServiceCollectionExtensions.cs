using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OhData.Abstractions;

namespace OhData.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers OhData services using the default registration name. Call <c>app.MapOhData()</c>
    /// after <c>app.Build()</c> to wire up the endpoints.
    /// </summary>
    public static IServiceCollection AddOhData(
        this IServiceCollection services,
        Action<OhDataBuilder> configure,
        Action<OhDataOptions>? configureOptions = null)
        => services.AddOhData(OhDataDefaults.DefaultRegistrationName, configure, configureOptions);

    /// <summary>
    /// Registers a named OhData instance. Call <c>app.MapOhData(name)</c> to wire up its endpoints.
    /// Multiple named registrations may coexist — each gets its own EDM model and route prefix.
    /// </summary>
    public static IServiceCollection AddOhData(
        this IServiceCollection services,
        string name,
        Action<OhDataBuilder> configure,
        Action<OhDataOptions>? configureOptions = null)
    {
        services.TryAddSingleton<OhDataRegistrationCollection>();
        services.AddOptions<OhDataOptions>();

        if (configureOptions is not null)
            services.Configure<OhDataOptions>(configureOptions);

        var builder = new OhDataBuilder(services, name);
        configure(builder);
        builder.Register();
        return services;
    }
}
