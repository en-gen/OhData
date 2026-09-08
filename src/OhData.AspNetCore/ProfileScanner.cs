using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OhData;

/// <summary>
/// Configures assembly scanning for profile subclasses. Obtained via
/// <see cref="OhDataBuilder.AddProfilesFrom"/>.
/// </summary>
/// <remarks>
/// A scan discovers all concrete, non-abstract subclasses of ONE profile kind in the specified
/// assemblies and registers each as if it had been passed to the matching Add method individually;
/// types already registered are skipped. The kind is the caller's: <c>AddProfilesFrom</c> scans
/// <see cref="EntitySetProfile{TKey,TModel}"/>, and the mapper package's
/// <c>AddDeltaProfilesFrom</c> scans delta profiles, which it has owned since #665.
/// </remarks>
public sealed class ProfileScanner
{
    private readonly HashSet<Assembly> _assemblies = new();
    private readonly IReadOnlyList<Type> _alreadyRegistered;

    internal ProfileScanner(IReadOnlyList<Type> alreadyRegistered)
        : this(alreadyRegistered, IsEntitySetProfile)
    {
    }

    internal ProfileScanner(IReadOnlyList<Type> alreadyRegistered, Func<Type, bool> isProfileKind)
    {
        _alreadyRegistered = alreadyRegistered;
        _isProfileKind = isProfileKind;
    }

    /// <summary>
    /// Includes the assembly that contains <typeparamref name="T"/> in the scan.
    /// </summary>
    /// <typeparam name="T">Any type whose assembly should be scanned.</typeparam>
    public ProfileScanner InAssemblyOf<T>() => In(typeof(T).Assembly);

    /// <summary>
    /// Includes the specified assembly in the scan.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    public ProfileScanner In(Assembly assembly)
    {
        if (assembly is null) throw new ArgumentNullException(nameof(assembly));
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Includes the specified assemblies in the scan.
    /// </summary>
    /// <param name="assemblies">One or more assemblies to scan.</param>
    public ProfileScanner In(params Assembly[] assemblies)
    {
        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException("At least one assembly must be provided.", nameof(assemblies));
        foreach (var assembly in assemblies)
            _assemblies.Add(assembly);
        return this;
    }

    internal IEnumerable<Type> Scan() =>
        _assemblies
            .Where(a => !a.IsDynamic)
            .SelectMany(a => a.GetTypes())
            .Where(t =>
                t.IsClass &&
                !t.IsAbstract &&
                // #488 item 5(a): an open generic is a template, not a profile. It was discovered,
                // registered in DI, and then killed MapOhData() with a raw
                // "MemberAccessException: Cannot create an instance of ..." naming no remedy --
                // and no way existed to exclude it from the scan. Skipping is what every DI
                // scanner does, and a CLOSED generic profile is still discovered normally. One
                // predicate serves both profile kinds, so the entity-set path (which the issue
                // notes shares the gap) is covered by the same line.
                !t.ContainsGenericParameters &&
                _isProfileKind(t) &&
                !_alreadyRegistered.Contains(t));

    // Which kind this scan is looking for. Entity-set profiles by default; the mapper package
    // passes its own predicate for delta profiles, which it owns since #665.
    private readonly Func<Type, bool> _isProfileKind;

    private static bool IsEntitySetProfile(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(EntitySetProfile<,>))
                return true;
        }

        return false;
    }

}
