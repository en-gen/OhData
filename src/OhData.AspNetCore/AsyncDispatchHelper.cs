using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Abstractions;

internal static class AsyncDispatchHelper
{
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
