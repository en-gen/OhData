using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Abstractions;

/// <summary>
/// Describes an unbound function or action that lives at the service root level
/// (i.e. not bound to an entity set). Registered via <c>OhDataBuilder.AddFunction</c> /
/// <c>OhDataBuilder.AddAction</c>.
/// </summary>
internal sealed record UnboundOperationDefinition
{
    public required string Name { get; init; }
    public required bool IsAction { get; init; }
    public required ParameterInfo[] Parameters { get; init; }

    // Return type for EDM registration (null = void/Task).
    public Type? ReturnType { get; init; }
    public bool ReturnsCollection { get; init; }

    public required Func<object?[], CancellationToken, Task<object?>> Invoke { get; init; }

    internal static UnboundOperationDefinition From(Delegate del, bool isAction)
    {
        var method = del.Method;
        var allParams = method.GetParameters();
        bool hasCt = allParams.Length > 0
            && allParams[^1].ParameterType == typeof(CancellationToken);
        var visibleParams = hasCt ? allParams[..^1] : allParams;

        var rawReturn = method.ReturnType;
        bool isVoidReturn = rawReturn == typeof(void)
            || rawReturn == typeof(Task)
            || rawReturn == typeof(ValueTask);

        Type? returnType = null;
        bool returnsCollection = false;
        if (!isVoidReturn)
        {
            Type unwrapped = rawReturn.IsGenericType &&
                             (rawReturn.GetGenericTypeDefinition() == typeof(Task<>) ||
                              rawReturn.GetGenericTypeDefinition() == typeof(ValueTask<>))
                ? rawReturn.GetGenericArguments()[0]
                : rawReturn;

            var collElement = GetCollectionElementType(unwrapped);
            if (collElement is not null)
            {
                returnType = collElement;
                returnsCollection = true;
            }
            else
            {
                returnType = unwrapped;
            }
        }

        PropertyInfo? resultProp = null;
        if (!isVoidReturn && rawReturn.IsGenericType)
        {
            var genDef = rawReturn.GetGenericTypeDefinition();
            if (genDef == typeof(Task<>))
                resultProp = rawReturn.GetProperty("Result");
            else if (genDef == typeof(ValueTask<>))
                resultProp = typeof(Task<>).MakeGenericType(rawReturn.GetGenericArguments()[0]).GetProperty("Result");
        }

        return new UnboundOperationDefinition
        {
            Name = method.Name,
            IsAction = isAction,
            Parameters = visibleParams,
            ReturnType = returnType,
            ReturnsCollection = returnsCollection,
            Invoke = async (args, ct) =>
            {
                object?[] fullArgs = hasCt ? [.. args, (object)ct] : args;
                object? raw;
                try { raw = del.DynamicInvoke(fullArgs); }
                catch (TargetInvocationException tie) when (tie.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw;
                }
                if (raw is null) return null;
                if (raw is ValueTask vt) { await vt.ConfigureAwait(false); return null; }
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

    private static Type? GetCollectionElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }
}
