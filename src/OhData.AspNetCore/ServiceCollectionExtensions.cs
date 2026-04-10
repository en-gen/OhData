using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;

namespace OhData.AspNetCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers OhData services. Call <c>app.MapOhData()</c> after <c>app.Build()</c>
    /// to wire up the endpoints.
    /// </summary>
    public static IServiceCollection AddOhData(
        this IServiceCollection services,
        Action<OhDataBuilder> configure,
        Action<OhDataOptions>? configureOptions = null)
    {
        services.AddOptions<OhDataOptions>();

        if (configureOptions is not null)
            services.Configure<OhDataOptions>(configureOptions);

        var builder = new OhDataBuilder(services);
        configure(builder);
        builder.Register();
        return services;
    }
}
