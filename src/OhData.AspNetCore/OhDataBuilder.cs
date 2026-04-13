using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    // Tracks profile types registered across all OhData registrations on this IServiceCollection.
    // Stored as a singleton marker so it is shared between all OhDataBuilder instances.
    private sealed class GlobalProfileRegistry
    {
        internal readonly HashSet<Type> RegisteredTypes = new();
    }

    internal OhDataBuilder(IServiceCollection services, string name = OhDataDefaults.DefaultRegistrationName)
    {
        _services = services;
        _name = name;

        // Ensure the cross-registration tracker is registered in DI exactly once,
        // as an instance singleton so it is available before the container is built.
        if (!_services.Any(s => s.ServiceType == typeof(GlobalProfileRegistry)))
            _services.AddSingleton(new GlobalProfileRegistry());
    }

    /// <summary>
    /// Sets the route prefix for all OData endpoints. Defaults to <c>/odata</c>.
    /// </summary>
    public OhDataBuilder WithPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix must not be empty.", nameof(prefix));
        string normalized = '/' + prefix.Trim().Trim('/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^/[A-Za-z0-9._~!$&'()*+,;=:@/\-]*$"))
        {
            throw new ArgumentException(
                $"Prefix '{normalized}' contains characters that are not valid in a URL path segment.", nameof(prefix));
        }

        _prefix = normalized;
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

        // Detect the same profile type being registered in a different OhData registration.
        // Retrieve the tracker directly from the registered descriptors to avoid requiring a built container.
        var registryDescriptor = _services.FirstOrDefault(s => s.ServiceType == typeof(GlobalProfileRegistry));
        if (registryDescriptor?.ImplementationInstance is GlobalProfileRegistry registry)
        {
            if (!registry.RegisteredTypes.Add(typeof(TProfile)))
            {
                throw new InvalidOperationException(
                    $"Profile type '{typeof(TProfile).Name}' has already been registered in a different " +
                    "OhData registration. A profile type cannot be shared across registrations.");
            }
        }

        _services.AddSingleton<TProfile>();
        _profileTypes.Add(typeof(TProfile));
        return this;
    }

    /// <summary>
    /// Scans the specified assemblies for <see cref="EntitySetProfile{TKey,TModel}"/> subclasses
    /// and registers each discovered profile as if it had been passed to
    /// <see cref="AddProfile{TProfile}"/> individually.
    /// </summary>
    /// <param name="configure">
    /// Callback that receives a <see cref="ProfileScanner"/> and specifies which assemblies
    /// to scan, e.g. <c>s =&gt; s.InAssemblyOf&lt;Program&gt;()</c>.
    /// </param>
    /// <example>
    /// <code>
    /// services.AddOhData(builder =&gt; builder
    ///     .AddProfilesFrom(s =&gt; s
    ///         .InAssemblyOf&lt;Program&gt;()
    ///         .In(typeof(ExternalProfile).Assembly)));
    /// </code>
    /// </example>
    public OhDataBuilder AddProfilesFrom(Action<ProfileScanner> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var scanner = new ProfileScanner(_profileTypes);
        configure(scanner);
        foreach (var type in scanner.Scan())
            AddProfileType(type);
        return this;
    }

    /// <summary>
    /// Scans the assembly that contains <typeparamref name="T"/> for
    /// <see cref="EntitySetProfile{TKey,TModel}"/> subclasses and registers each one.
    /// Equivalent to <c>AddProfilesFrom(s =&gt; s.InAssemblyOf&lt;T&gt;())</c>.
    /// </summary>
    /// <typeparam name="T">Any type whose containing assembly should be scanned.</typeparam>
    public OhDataBuilder AddProfilesFromAssemblyOf<T>() =>
        AddProfilesFrom(s => s.InAssemblyOf<T>());

    /// <summary>
    /// Scans the specified assemblies for <see cref="EntitySetProfile{TKey,TModel}"/> subclasses
    /// and registers each one.
    /// Equivalent to <c>AddProfilesFrom(s =&gt; s.In(assemblies))</c>.
    /// </summary>
    /// <param name="assemblies">One or more assemblies to scan.</param>
    public OhDataBuilder AddProfilesFromAssembly(params Assembly[] assemblies) =>
        AddProfilesFrom(s => s.In(assemblies));

    private void AddProfileType(Type type)
    {
        if (_profileTypes.Contains(type)) return;
        // If another OhData registration already registered this profile type as a singleton,
        // skip re-registering it in DI but still track it for this builder's route mapping.
        if (!_services.Any(s => s.ServiceType == type))
            _services.AddSingleton(type);
        _profileTypes.Add(type);
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
