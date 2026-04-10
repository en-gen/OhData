namespace OhData.Abstractions;

public sealed class OhDataContext
{
    internal OhDataContext(IReadOnlyList<Type> registeredModelTypes)
    {
        RegisteredModelTypes = registeredModelTypes;
    }

    internal IReadOnlyList<Type> RegisteredModelTypes { get; }
}
