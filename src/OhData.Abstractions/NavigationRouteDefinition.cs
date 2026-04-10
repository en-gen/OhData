namespace OhData.Abstractions;

internal sealed class NavigationRouteDefinition
{
    public string PropertyName { get; init; } = "";
    public bool   IsCollection { get; init; }
    public Func<object, CancellationToken, Task<object?>> Handler { get; init; } = null!;
}
