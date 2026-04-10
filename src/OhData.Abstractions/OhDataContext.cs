namespace OhData.Abstractions;

public sealed class OhDataContext
{
    internal OhDataContext(IReadOnlyList<Type> registeredProfileTypes)
    {
        RegisteredProfileTypes = registeredProfileTypes;
    }

    internal IReadOnlyList<Type> RegisteredProfileTypes { get; }
}
