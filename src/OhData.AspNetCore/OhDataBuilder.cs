using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OData.ModelBuilder;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Fluent builder used inside <c>AddOhData(ohdata => { ... })</c> to register entity set profiles
/// and configure framework options.
/// </summary>
public sealed class OhDataBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _profileTypes = new();
    private string _prefix = "/odata";
    private readonly string _name;

    internal OhDataBuilder(IServiceCollection services, string name = OhDataDefaults.DefaultRegistrationName)
    {
        _services = services;
        _name = name;
    }

    /// <summary>
    /// Sets the route prefix for all OData endpoints. Defaults to <c>/odata</c>.
    /// </summary>
    public OhDataBuilder WithPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix must not be empty.", nameof(prefix));
        _prefix = prefix.TrimEnd('/');
        return this;
    }

    /// <summary>
    /// Registers an entity set profile. The profile is resolved from DI (singleton),
    /// so constructor injection is supported.
    /// </summary>
    public OhDataBuilder AddProfile<TProfile>() where TProfile : class, IEntitySetProfile
    {
        _services.AddSingleton<TProfile>();
        _profileTypes.Add(typeof(TProfile));
        return this;
    }

    internal void Register()
    {
        var capturedTypes = _profileTypes.ToList();
        var capturedPrefix = _prefix;
        var capturedName = _name;

        _services.AddKeyedSingleton<OhDataRegistration>(capturedName, (sp, _) =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("OhData");
            var modelBuilder = new ODataConventionModelBuilder();
            var context = new OhDataContext(capturedTypes);
            var defaults = sp.GetService<EntitySetDefaults>() ?? new EntitySetDefaults();
            var profiles = new List<IEntitySetEndpointSource>();

            foreach (var type in capturedTypes)
            {
                var instance = sp.GetRequiredService(type);

                if (instance is IVisitModelBuilder vmb)
                {
                    logger?.LogDebug("OhData: building EDM for {Profile}", type.Name);
                    vmb.VisitModelBuilder(modelBuilder, context, defaults);
                }

                if (instance is IEntitySetEndpointSource source)
                    profiles.Add(source);
                else
                    logger?.LogWarning(
                        "OhData: {Type} does not implement IEntitySetEndpointSource and will be skipped",
                        type.Name);
            }

            var duplicates = profiles
                .GroupBy(s => s.EntitySetName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
                throw new InvalidOperationException(
                    $"OhData: duplicate entity set name(s) registered: {string.Join(", ", duplicates)}. " +
                    "Each entity set name must be unique within an OhData registration.");

            var edmModel = modelBuilder.GetEdmModel();
            var options = sp.GetService<IOptions<OhDataOptions>>()?.Value ?? new OhDataOptions();

            logger?.LogInformation(
                "OhData: initialized {Count} entity set(s) [{Names}] at prefix '{Prefix}'",
                profiles.Count,
                string.Join(", ", profiles.Select(p => p.EntitySetName)),
                capturedPrefix);

            var reg = new OhDataRegistration(capturedPrefix, edmModel, profiles, options);
            // Also register in the collection for named access
            sp.GetRequiredService<OhDataRegistrationCollection>().Add(capturedName, reg);
            return reg;
        });

        // Backwards compat: the default registration is also accessible as an unkeyed singleton
        if (capturedName == OhDataDefaults.DefaultRegistrationName)
        {
            _services.AddSingleton<OhDataRegistration>(sp =>
                sp.GetRequiredKeyedService<OhDataRegistration>(OhDataDefaults.DefaultRegistrationName));
        }
    }
}
