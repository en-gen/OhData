using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// Configures assembly scanning for <see cref="EntitySetProfile{TKey,TModel}"/> subclasses.
/// Obtained via <see cref="OhDataBuilder.AddProfilesFrom"/>.
/// </summary>
/// <remarks>
/// Scanning discovers all concrete, non-abstract <see cref="EntitySetProfile{TKey,TModel}"/>
/// subclasses in the specified assemblies and registers them as if each had been passed to
/// <see cref="OhDataBuilder.AddProfile{TProfile}"/> individually. Types already registered
/// in the current builder are skipped.
/// </remarks>
public sealed class ProfileScanner
{
    private readonly HashSet<Assembly> _assemblies = new();
    private readonly IReadOnlyList<Type> _alreadyRegistered;

    internal ProfileScanner(IReadOnlyList<Type> alreadyRegistered)
    {
        _alreadyRegistered = alreadyRegistered;
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
                IsEntitySetProfile(t) &&
                !_alreadyRegistered.Contains(t));

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
