using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OhData;

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
        var (hasCt, visibleParams) = AsyncDispatchHelper.SplitCancellationToken(method.GetParameters());

        var rawReturn = method.ReturnType;
        bool isVoidReturn = AsyncDispatchHelper.IsVoidAsyncReturn(rawReturn);

        Type? returnType = null;
        bool returnsCollection = false;
        if (!isVoidReturn)
        {
            Type unwrapped = AsyncDispatchHelper.UnwrapAsyncReturn(rawReturn);

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

        PropertyInfo? resultProp = AsyncDispatchHelper.GetAsyncResultAccessor(rawReturn);

        return new UnboundOperationDefinition
        {
            Name = method.Name,
            IsAction = isAction,
            Parameters = visibleParams,
            ReturnType = returnType,
            ReturnsCollection = returnsCollection,
            Invoke = AsyncDispatchHelper.BuildInvoker(del, hasCt, isVoidReturn, resultProp)
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
