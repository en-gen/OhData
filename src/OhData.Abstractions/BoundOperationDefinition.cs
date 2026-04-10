using System.Reflection;
using System.Threading.Tasks;

namespace OhData.Abstractions;

internal sealed record BoundOperationDefinition
{
    public required string Name { get; init; }
    public required bool IsAction { get; init; }
    public required ParameterInfo[] Parameters { get; init; }

    // Invokes the underlying delegate; CancellationToken is automatically appended if the
    // original method accepts it as its last parameter.
    // Note: DynamicInvoke is ~100x slower than a direct typed invocation in microbenchmarks,
    // but the absolute cost (~1-5us) is negligible relative to HTTP endpoint overhead (~1-50ms).
    // A pre-compiled strongly-typed invoker via expression trees would eliminate this, but adds
    // significant startup complexity for minimal per-request benefit.
    public required Func<object?[], CancellationToken, Task<object?>> Invoke { get; init; }

    internal static BoundOperationDefinition From(Delegate del, bool isAction)
    {
        var method = del.Method;
        var allParams = method.GetParameters();
        var hasCt = allParams.Length > 0
            && allParams[^1].ParameterType == typeof(CancellationToken);
        var visibleParams = hasCt ? allParams[..^1] : allParams;

        var returnType = method.ReturnType;
        var isVoidReturn = returnType == typeof(void)
            || returnType == typeof(Task)
            || returnType == typeof(ValueTask);

        // Cache the Result property accessor for Task<T>/ValueTask<T> at registration time
        // rather than using reflection per invocation.
        PropertyInfo? resultProp = null;
        if (!isVoidReturn && returnType.IsGenericType)
        {
            var genDef = returnType.GetGenericTypeDefinition();
            if (genDef == typeof(Task<>))
                resultProp = returnType.GetProperty("Result");
            else if (genDef == typeof(ValueTask<>))
                resultProp = typeof(Task<>).MakeGenericType(returnType.GetGenericArguments()[0]).GetProperty("Result");
        }

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
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw; // unreachable
                }
                if (raw is null) return null;

                // Convert ValueTask/ValueTask<T> to Task/Task<T> for uniform handling
                if (raw is ValueTask vt)
                {
                    await vt.ConfigureAwait(false);
                    return null;
                }
                if (raw.GetType() is { IsGenericType: true } rawType
                    && rawType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTaskMethod = rawType.GetMethod("AsTask")!;
                    raw = asTaskMethod.Invoke(raw, null)!;
                }

                if (raw is Task task)
                {
                    await task.ConfigureAwait(false);
                    if (isVoidReturn) return null;
                    return resultProp?.GetValue(task);
                }
                return raw;
            }
        };
    }
}
