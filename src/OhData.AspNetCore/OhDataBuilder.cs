using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly List<UnboundOperationDefinition> _unboundOps = new();
    private string _prefix = "/odata";
    private readonly string _name;
    private readonly EntitySetDefaults _defaults = new();

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
        _prefix = '/' + prefix.Trim().Trim('/');
        return this;
    }

    /// <summary>
    /// Configures global defaults applied to all entity sets in this registration
    /// (e.g. <c>MaxTop</c>, <c>IdempotentDelete</c>). Per-profile settings override these.
    /// </summary>
    public OhDataBuilder WithDefaults(Action<EntitySetDefaults> configure)
    {
        configure(_defaults);
        return this;
    }

    /// <summary>
    /// Registers an entity set profile. The profile is resolved from DI (singleton),
    /// so constructor injection is supported.
    /// </summary>
    public OhDataBuilder AddProfile<TProfile>() where TProfile : class, IEntitySetProfile
    {
        if (_profileTypes.Contains(typeof(TProfile)))
        {
            throw new InvalidOperationException(
                $"OhData: profile type '{typeof(TProfile).Name}' is already registered. Remove the duplicate AddProfile call.");
        }

        _services.AddSingleton<TProfile>();
        _profileTypes.Add(typeof(TProfile));
        return this;
    }

    /// <summary>
    /// Registers an unbound function at the service root: <c>GET /prefix/{name}</c>.
    /// Parameters are read from query-string values; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The function delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">Optional explicit route name. Use when passing lambdas to override the compiler-generated name.</param>
    public OhDataBuilder AddFunction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: false);
        if (name is not null) op = op with { Name = name };
        _unboundOps.Add(op);
        return this;
    }

    /// <summary>
    /// Registers an unbound action at the service root: <c>POST /prefix/{name}</c>.
    /// Parameters are read from the JSON request body; <see cref="System.Threading.CancellationToken"/>
    /// is detected and injected automatically if present.
    /// </summary>
    /// <param name="handler">The action delegate. The method name is used as the route segment unless <paramref name="name"/> is specified.</param>
    /// <param name="name">Optional explicit route name. Use when passing lambdas to override the compiler-generated name.</param>
    public OhDataBuilder AddAction(Delegate handler, string? name = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var op = UnboundOperationDefinition.From(handler, isAction: true);
        if (name is not null) op = op with { Name = name };
        _unboundOps.Add(op);
        return this;
    }

    internal void Register()
    {
        var capturedTypes = _profileTypes.ToList();
        var capturedUnbound = _unboundOps.ToList();
        string capturedPrefix = _prefix;
        string capturedName = _name;
        var capturedDefaults = _defaults;

        _services.AddKeyedSingleton<OhDataRegistration>(capturedName, (sp, _) =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("OhData");
            var modelBuilder = new ODataConventionModelBuilder();
            var defaults = capturedDefaults;
            var profiles = new List<IEntitySetEndpointSource>();

            foreach (var type in capturedTypes)
            {
                object instance = sp.GetRequiredService(type);

                if (instance is IVisitModelBuilder vmb)
                {
                    logger?.LogDebug("OhData: building EDM for {Profile}", type.Name);
                    try
                    {
                        vmb.VisitModelBuilder(modelBuilder, defaults);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "OhData: failed to build EDM for profile {Profile}", type.Name);
                        throw new InvalidOperationException(
                            $"OhData: failed to build EDM for profile '{type.Name}'. See inner exception for details.", ex);
                    }
                }

                if (instance is IEntitySetEndpointSource source)
                {
                    profiles.Add(source);
                }
                else
                {
                    logger?.LogWarning(
                        "OhData: {Type} does not implement IEntitySetEndpointSource and will be skipped",
                        type.Name);
                }
            }

            var duplicates = profiles
                .GroupBy(s => s.EntitySetName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"OhData: duplicate entity set name(s) registered: {string.Join(", ", duplicates)}. " +
                    "Each entity set name must be unique within an OhData registration.");
            }

            // Gap 7: register unbound functions/actions in the EDM as FunctionImport/ActionImport
            // Must be done BEFORE GetEdmModel() so they appear in $metadata.
            foreach (var op in capturedUnbound)
            {
                if (!op.IsAction)
                {
                    var fn = modelBuilder.Function(op.Name);
                    RegisterUnboundOpReturnType(fn, op);
                    foreach (var param in op.Parameters)
                    {
                        var p = fn.Parameter(param.ParameterType, param.Name!);
                        if (param.IsOptional) p.Optional();
                    }
                }
                else
                {
                    var act = modelBuilder.Action(op.Name);
                    RegisterUnboundOpReturnType(act, op);
                    foreach (var param in op.Parameters)
                    {
                        var p = act.Parameter(param.ParameterType, param.Name!);
                        if (param.IsOptional) p.Optional();
                    }
                }
            }

            var edmModel = modelBuilder.GetEdmModel();
            logger?.LogInformation(
                "OhData: initialized {Count} entity set(s) [{Names}] at prefix '{Prefix}'",
                profiles.Count,
                string.Join(", ", profiles.Select(p => p.EntitySetName)),
                capturedPrefix);

            var reg = new OhDataRegistration(capturedPrefix, edmModel, profiles, capturedUnbound);
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

    private static void RegisterUnboundOpReturnType(FunctionConfiguration fn, UnboundOperationDefinition op)
    {
        if (op.ReturnType is null) return;
        var refl = op.ReturnsCollection
            ? typeof(FunctionConfiguration).GetMethod(nameof(FunctionConfiguration.ReturnsCollection), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType)
            : typeof(FunctionConfiguration).GetMethod(nameof(FunctionConfiguration.Returns), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType);
        refl.Invoke(fn, null);
    }

    private static void RegisterUnboundOpReturnType(ActionConfiguration act, UnboundOperationDefinition op)
    {
        if (op.ReturnType is null) return;
        var refl = op.ReturnsCollection
            ? typeof(ActionConfiguration).GetMethod(nameof(ActionConfiguration.ReturnsCollection), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType)
            : typeof(ActionConfiguration).GetMethod(nameof(ActionConfiguration.Returns), System.Array.Empty<Type>())!
                  .MakeGenericMethod(op.ReturnType);
        refl.Invoke(act, null);
    }
}
