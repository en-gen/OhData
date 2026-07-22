using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OhData;

internal static class AsyncDispatchHelper
{
    /// <summary>
    /// Splits a method's parameters into (has trailing CancellationToken, the parameters visible to
    /// callers with that token removed). The framework supplies the token itself at invocation time.
    /// </summary>
    internal static (bool hasCancellationToken, ParameterInfo[] visibleParameters) SplitCancellationToken(
        ParameterInfo[] allParameters)
    {
        bool hasCt = allParameters.Length > 0
            && allParameters[^1].ParameterType == typeof(CancellationToken);
        return (hasCt, hasCt ? allParameters[..^1] : allParameters);
    }

    /// <summary>
    /// True when the return type carries no result value (<c>void</c>/<c>Task</c>/<c>ValueTask</c>) —
    /// such an operation always yields 204 No Content.
    /// </summary>
    internal static bool IsVoidAsyncReturn(Type returnType) =>
        returnType == typeof(void)
        || returnType == typeof(Task)
        || returnType == typeof(ValueTask);

    /// <summary>Unwraps <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> to <c>T</c>; returns the type unchanged otherwise.</summary>
    internal static Type UnwrapAsyncReturn(Type returnType) =>
        returnType.IsGenericType
        && (returnType.GetGenericTypeDefinition() == typeof(Task<>)
            || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? returnType.GetGenericArguments()[0]
            : returnType;

    /// <summary>
    /// Resolves the <c>Task&lt;T&gt;.Result</c> accessor <see cref="BuildInvoker"/> reads the awaited
    /// value from, caching it at registration time instead of reflecting per invocation. Returns null
    /// for non-generic (void/Task/ValueTask) returns, which have nothing to read.
    /// </summary>
    internal static PropertyInfo? GetAsyncResultAccessor(Type returnType)
    {
        if (!returnType.IsGenericType) return null;
        var genDef = returnType.GetGenericTypeDefinition();
        if (genDef == typeof(Task<>))
            return returnType.GetProperty("Result");
        if (genDef == typeof(ValueTask<>))
            return typeof(Task<>).MakeGenericType(returnType.GetGenericArguments()[0]).GetProperty("Result");
        return null;
    }

    /// <summary>
    /// Builds an async invoker for a delegate that may return void, Task, Task&lt;T&gt;,
    /// ValueTask, or ValueTask&lt;T&gt;. Handles CancellationToken injection.
    /// </summary>
    internal static Func<object?[], CancellationToken, Task<object?>> BuildInvoker(
        Delegate del,
        bool hasCt,
        bool isVoidReturn,
        PropertyInfo? resultProp)
    {
        return async (args, ct) =>
        {
            object?[] fullArgs = hasCt ? [.. args, (object)ct] : args;
            object? raw;
            try { raw = del.DynamicInvoke(fullArgs); }
            catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
            if (raw is null) return null;

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
        };
    }
}
