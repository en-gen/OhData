using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace OhData;

/// <summary>
/// Registers <see cref="DeltaProfile"/> types with an OhData registration.
/// </summary>
/// <remarks>
/// <para>
/// Extension methods on <see cref="OhDataBuilder"/> rather than members of it, because delta
/// mapping ships in <c>EnGen.OhData.AspNetCore.Mapper</c> as of 2.0.0 (#665) and the core cannot
/// reference it. Declared in the <c>OhData</c> namespace so they are in scope wherever
/// <c>AddOhData</c> is — an adopter's <c>using</c> statements do not change, only the package
/// reference.
/// </para>
/// <para>
/// <b>Scanning is now explicit.</b> <c>AddProfilesFrom</c> discovers entity-set profiles only; a
/// delta profile is discovered by <see cref="AddDeltaProfilesFrom"/>. The core scanner cannot name
/// a type it does not reference, and asking it to match one by string would be a worse answer than
/// one call per kind.
/// </para>
/// </remarks>
public static class DeltaProfileRegistration
{
    /// <summary>
    /// Registers a <see cref="DeltaProfile"/>. Its mappings are compiled and validated once at
    /// startup and exposed through the injected <see cref="IDeltaFactory"/>.
    /// </summary>
    /// <typeparam name="TProfile">The delta profile type.</typeparam>
    /// <param name="builder">The OhData registration to add it to.</param>
    public static OhDataBuilder AddDeltaProfile<TProfile>(this OhDataBuilder builder)
        where TProfile : DeltaProfile
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        Register(builder, typeof(TProfile), explicitCall: true);
        return builder;
    }

    /// <summary>
    /// Scans the specified assemblies for <see cref="DeltaProfile"/> subclasses and registers each
    /// one, as if it had been passed to <see cref="AddDeltaProfile{TProfile}"/> individually.
    /// </summary>
    /// <param name="builder">The OhData registration to add them to.</param>
    /// <param name="configure">
    /// Receives a scanner and specifies which assemblies to scan, e.g.
    /// <c>s =&gt; s.InAssemblyOf&lt;Program&gt;()</c>.
    /// </param>
    public static OhDataBuilder AddDeltaProfilesFrom(
        this OhDataBuilder builder, Action<ProfileScanner> configure)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        DeltaProfileRegistry registry = EnsureServices(builder);

        // Deduped against what this registry already holds, which is the delta equivalent of the
        // core scanner's _profileTypes -- so re-scanning an assembly is a no-op rather than a throw.
        var scanner = new ProfileScanner(registry.Types.ToList(), IsDeltaProfile);
        configure(scanner);

        foreach (Type type in scanner.Scan()) Register(builder, type, explicitCall: false);
        return builder;
    }

    /// <summary>
    /// Scans the assembly containing <typeparamref name="T"/> for <see cref="DeltaProfile"/>
    /// subclasses and registers each one.
    /// </summary>
    /// <typeparam name="T">Any type whose containing assembly should be scanned.</typeparam>
    /// <param name="builder">The OhData registration to add them to.</param>
    public static OhDataBuilder AddDeltaProfilesFromAssemblyOf<T>(this OhDataBuilder builder) =>
        builder.AddDeltaProfilesFrom(s => s.InAssemblyOf<T>());

    /// <summary>
    /// Scans the specified assemblies for <see cref="DeltaProfile"/> subclasses and registers each
    /// one.
    /// </summary>
    /// <param name="builder">The OhData registration to add them to.</param>
    /// <param name="assemblies">One or more assemblies to scan.</param>
    public static OhDataBuilder AddDeltaProfilesFromAssembly(
        this OhDataBuilder builder, params Assembly[] assemblies) =>
        builder.AddDeltaProfilesFrom(s => s.In(assemblies));

    private static bool IsDeltaProfile(Type type) => typeof(DeltaProfile).IsAssignableFrom(type);

    /// <summary>
    /// Routes a type into the shared cross-registration registry and DI.
    /// </summary>
    /// <remarks>
    /// Delta profiles are not tied to a single OhData registration — the <see cref="IDeltaFactory"/>
    /// is one global singleton — so uniqueness is tracked in the shared registry.
    /// </remarks>
    private static void Register(OhDataBuilder builder, Type type, bool explicitCall)
    {
        DeltaProfileRegistry registry = EnsureServices(builder);

        if (registry.Types.Contains(type))
        {
            // #488 item 5(c): the duplicate-call message may only be used when a duplicate call is
            // what actually happened. A scan that already discovered the type followed by ONE
            // explicit call is not a duplicate -- and the reverse order was always a silent no-op,
            // so throwing here made the outcome depend on declaration order.
            if (explicitCall && registry.ExplicitlyRegistered.Contains(type))
            {
                throw new InvalidOperationException(
                    $"OhData: delta profile type '{type.Name}' is already registered. " +
                    "Remove the duplicate AddDeltaProfile call.");
            }

            if (explicitCall) registry.ExplicitlyRegistered.Add(type);
            return;
        }

        if (!builder.Services.Any(s => s.ServiceType == type)) builder.Services.AddScoped(type);

        registry.Types.Add(type);
        if (explicitCall) registry.ExplicitlyRegistered.Add(type);
    }

    /// <summary>
    /// Registers the delta-mapping infrastructure, idempotently.
    /// </summary>
    /// <remarks>
    /// The registry accumulates profile types across every OhData registration (an instance
    /// singleton, mutable before the container is built); the single <see cref="IDeltaFactory"/>
    /// reads it once, lazily, and compiles and validates every mapping. That compilation is forced
    /// at <c>MapOhData</c> through <c>IOhDataStartupValidated</c>, so an unmapped or incompatible
    /// mapping fails at startup rather than on the first request.
    /// </remarks>
    private static DeltaProfileRegistry EnsureServices(OhDataBuilder builder)
    {
        IServiceCollection services = builder.Services;

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(DeltaProfileRegistry));
        if (descriptor?.ImplementationInstance is DeltaProfileRegistry existing) return existing;

        var registry = new DeltaProfileRegistry();
        services.AddSingleton(registry);
        services.AddSingleton<IDeltaFactory>(sp =>
            DeltaFactory.Build(sp, sp.GetRequiredService<DeltaProfileRegistry>()));
        services.AddSingleton<IOhDataStartupValidated>(
            sp => new DeltaStartupValidation(sp.GetRequiredService<IDeltaFactory>()));

        return registry;
    }

    /// <summary>
    /// Forces <see cref="IDeltaFactory"/> construction — and therefore mapping compilation and
    /// validation — when <c>MapOhData</c> resolves the startup-validated services.
    /// </summary>
    private sealed class DeltaStartupValidation : IOhDataStartupValidated
    {
        public DeltaStartupValidation(IDeltaFactory factory) => Factory = factory;

        public IDeltaFactory Factory { get; }
    }
}
