using System.Reflection;

namespace OhData.Abstractions;

internal sealed record BoundOperationDefinition
{
    public required string Name { get; init; }
    public required bool IsAction { get; init; }
    public required ParameterInfo[] Parameters { get; init; }

    // Invokes the underlying delegate; CancellationToken is automatically appended if the
    // original method accepts it as its last parameter.
    public required Func<object?[], CancellationToken, Task<object?>> Invoke { get; init; }

    internal static BoundOperationDefinition From(Delegate del, bool isAction)
    {
        var method = del.Method;
        var allParams = method.GetParameters();
        var hasCt = allParams.Length > 0
            && allParams[^1].ParameterType == typeof(CancellationToken);
        var visibleParams = hasCt ? allParams[..^1] : allParams;

        var isVoidTask = method.ReturnType == typeof(Task) || method.ReturnType == typeof(void);

        return new BoundOperationDefinition
        {
            Name = method.Name,
            IsAction = isAction,
            Parameters = visibleParams,
            Invoke = async (args, ct) =>
            {
                var fullArgs = hasCt
                    ? [.. args, (object)ct]
                    : args;
                object? raw;
                try { raw = del.DynamicInvoke(fullArgs); }
                catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw; // unreachable
                }
                if (raw is null) return null;
                if (raw is Task task)
                {
                    await task.ConfigureAwait(false);
                    if (isVoidTask) return null;
                    // Task<T>: extract Result via reflection
                    var resultProp = task.GetType().GetProperty("Result");
                    return resultProp?.GetValue(task);
                }
                return raw;
            }
        };
    }
}
