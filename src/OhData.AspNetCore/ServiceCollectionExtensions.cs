using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OhData.AspNetCore;

/// <summary>
/// Extension methods on <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// for registering OhData services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers OhData services using the default registration name. Call <c>app.MapOhData()</c>
    /// after <c>app.Build()</c> to wire up the endpoints.
    /// </summary>
    public static IServiceCollection AddOhData(
        this IServiceCollection services,
        Action<OhDataBuilder> configure)
        => services.AddOhData(OhDataDefaults.DefaultRegistrationName, configure);

    /// <summary>
    /// Registers a named OhData instance. Call <c>app.MapOhData(name)</c> to wire up its endpoints.
    /// Multiple named registrations may coexist — each gets its own EDM model and route prefix.
    /// </summary>
    public static IServiceCollection AddOhData(
        this IServiceCollection services,
        string name,
        Action<OhDataBuilder> configure)
    {
        // Detect duplicate registration names eagerly (at AddOhData call time) rather than at
        // startup, because the DI container silently replaces keyed singletons with the same key.
        if (services.Any(d => d.IsKeyedService
                && d.ServiceKey is string k
                && StringComparer.OrdinalIgnoreCase.Equals(k, name)
                && d.ServiceType == typeof(OhDataRegistration)))
        {
            throw new InvalidOperationException(
                $"OhData: a registration named '{name}' is already registered. " +
                "Call AddOhData with a different name, or remove the duplicate call.");
        }

        services.TryAddSingleton<OhDataRegistrationCollection>();

        // Leg 2 (docs-fidelity): registered once, idempotently, regardless of how many named
        // OhData registrations the host app adds — TryAddEnumerable de-dupes by implementation
        // type, so a second/third AddOhData call is a no-op here. See OhDataApiDescriptionProvider
        // for why this lives in the ApiExplorer pipeline rather than as endpoint metadata alone.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IApiDescriptionProvider, OhDataApiDescriptionProvider>());

        var builder = new OhDataBuilder(services, name);
        configure(builder);
        builder.Register();
        return services;
    }
}
