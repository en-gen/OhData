using System;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Abstractions;

internal sealed record NavigationRouteDefinition
{
    public string PropertyName { get; init; } = "";
    public bool IsCollection { get; init; }
    public Func<object, CancellationToken, Task<object?>> Handler { get; init; } = null!;

    // Gap 6: $ref delegates — null = endpoint not registered
    // AddRef: (parentKey, relatedId, ct) — relatedId is the "@odata.id" string from the request body
    public Func<object, object, CancellationToken, Task>? AddRef { get; init; }
    // RemoveRef: (parentKey, relatedId, ct)
    public Func<object, object, CancellationToken, Task>? RemoveRef { get; init; }
}
