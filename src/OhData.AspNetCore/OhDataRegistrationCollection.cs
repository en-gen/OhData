namespace OhData.AspNetCore;

/// <summary>Holds all named OhData registrations. Registered as a singleton.</summary>
public sealed class OhDataRegistrationCollection
{
    private readonly Dictionary<string, OhDataRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    internal void Add(string name, OhDataRegistration registration) =>
        _registrations[name] = registration;

    public OhDataRegistration Get(string name)
    {
        if (_registrations.TryGetValue(name, out var reg)) return reg;
        throw new InvalidOperationException(
            $"No OhData registration found with name '{name}'. " +
            $"Did you call AddOhData(\"{name}\", ...)?");
    }

    public bool TryGet(string name, out OhDataRegistration? registration) =>
        _registrations.TryGetValue(name, out registration);

    public OhDataRegistration Default => Get(OhDataDefaults.DefaultRegistrationName);
}
