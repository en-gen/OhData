using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OhData;

/// <summary>Holds all named OhData registrations. Registered as a singleton.</summary>
public sealed class OhDataRegistrationCollection
{
    private readonly ConcurrentDictionary<string, OhDataRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    internal void Add(string name, OhDataRegistration registration)
    {
        // Silently no-op when the name already exists (TryAdd semantics).
        // The primary duplicate guard is in ServiceCollectionExtensions; this is defense-in-depth.
        _registrations.TryAdd(name, registration);
    }

    /// <summary>
    /// Returns the named registration. Throws <see cref="InvalidOperationException"/>
    /// if no registration with <paramref name="name"/> exists.
    /// </summary>
    /// <param name="name">The registration name passed to <c>AddOhData(name, ...)</c>.</param>
    public OhDataRegistration Get(string name)
    {
        if (_registrations.TryGetValue(name, out var reg)) return reg;
        throw new InvalidOperationException(
            $"No OhData registration found with name '{name}'. " +
            $"Did you call AddOhData(\"{name}\", ...)?");
    }

    /// <summary>
    /// Attempts to retrieve the named registration. Returns <c>false</c> if not found.
    /// </summary>
    /// <param name="name">The registration name passed to <c>AddOhData(name, ...)</c>.</param>
    /// <param name="registration">The resolved registration, or <c>null</c> if not found.</param>
    public bool TryGet(string name, out OhDataRegistration? registration) =>
        _registrations.TryGetValue(name, out registration);

    /// <summary>
    /// Returns the unnamed default registration (the one added via <c>AddOhData()</c> without
    /// an explicit name). Throws if no default registration exists.
    /// </summary>
    public OhDataRegistration Default => Get(OhDataDefaults.DefaultRegistrationName);

    /// <summary>
    /// All registrations resolved so far. A registration appears here once its keyed singleton
    /// has been built — <c>app.MapOhData(name)</c> forces that at startup, so by the time any
    /// request (including an OpenAPI document request) is served, every mapped registration is
    /// present. Used by <see cref="IgnoredPropertyDocsMap"/> (#228).
    /// </summary>
    internal IEnumerable<OhDataRegistration> All => _registrations.Values;
}
