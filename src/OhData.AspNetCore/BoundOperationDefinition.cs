using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Abstractions;

/// <summary>
/// Describes a bound function or action registered via
/// <c>BindFunction</c>, <c>BindAction</c>, <c>BindEntityFunction</c>, or <c>BindEntityAction</c>
/// on an <see cref="EntitySetProfile{TKey,TModel}"/>. Built once at startup and cached.
/// </summary>
internal sealed record BoundOperationDefinition
{
    /// <summary>The OData operation name (the delegate method name).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>true</c> for actions (OData §11.5.4, HTTP POST); <c>false</c> for functions
    /// (OData §11.5.3, HTTP GET).
    /// </summary>
    public required bool IsAction { get; init; }

    /// <summary>
    /// The visible OData parameters (all delegate parameters except <see cref="CancellationToken"/>
    /// and, for entity-level operations, the leading key parameter).
    /// </summary>
    public required ParameterInfo[] Parameters { get; init; }

    /// <summary>
    /// <c>true</c> when this operation is bound to a single entity
    /// (<c>GET/POST /{EntitySet}({key})/{Name}</c>); <c>false</c> when bound to the entity set
    /// collection (<c>GET/POST /{EntitySet}/{Name}</c>).
    /// </summary>
    public bool IsEntityLevel { get; init; } = false;

    /// <summary>
    /// Invokes the underlying delegate, automatically appending a <see cref="CancellationToken"/>
    /// when the original method accepts one.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Delegate.DynamicInvoke"/> internally. In microbenchmarks this is ~100× slower
    /// than a direct typed call, but the absolute cost (~1–5 µs) is negligible relative to HTTP
    /// endpoint overhead (~1–50 ms). A pre-compiled strongly-typed invoker via expression trees
    /// would eliminate this overhead but adds significant startup complexity for minimal
    /// per-request benefit.
    /// </remarks>
    public required Func<object?[], CancellationToken, Task<object?>> Invoke { get; init; }

    internal static BoundOperationDefinition From(Delegate del, bool isAction, bool isEntityLevel = false)
    {
        var method = del.Method;

        // Reject compiler-generated names (lambdas produce names like '<.ctor>b__0_0').
        // The name becomes the OData operation name in the EDM — it must be a valid identifier.
        if (method.Name.Contains('<') || method.Name.Contains('>'))
        {
            string kind = isAction ? "action" : "function";
            throw new InvalidOperationException(
                $"Cannot bind a compiler-generated method as an OData {kind}. " +
                $"Use a named method instead of a lambda (detected name: '{method.Name}'). " +
                $"Example: BindFunction(MyFunction) where MyFunction is a named method on the profile.");
        }

        var allParams = method.GetParameters();
        bool hasCt = allParams.Length > 0
            && allParams[^1].ParameterType == typeof(CancellationToken);
        var visibleParams = hasCt ? allParams[..^1] : allParams;

        var returnType = method.ReturnType;
        bool isVoidReturn = returnType == typeof(void)
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
            IsEntityLevel = isEntityLevel,
            Parameters = visibleParams,
            Invoke = AsyncDispatchHelper.BuildInvoker(del, hasCt, isVoidReturn, resultProp)
        };
    }
}
