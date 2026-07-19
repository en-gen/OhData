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
    /// The delegate's parameters, in declaration order, with a trailing <see cref="CancellationToken"/>
    /// parameter (if present) excluded. For entity-level operations (<see cref="IsEntityLevel"/>),
    /// the leading key parameter is <em>included</em> here at index 0 -- the framework validates at
    /// bind time (<c>EntitySetProfile.BindEntityFunction</c>/<c>BindEntityAction</c>) that it is
    /// present and typed as <c>TKey</c>, then supplies the parsed route key as <c>args[0]</c> at
    /// request time. Only <c>Parameters[1..]</c> are exposed as OData query-string/JSON-body
    /// parameters for entity-level operations.
    /// </summary>
    public required ParameterInfo[] Parameters { get; init; }

    /// <summary>
    /// <c>true</c> when this operation is bound to a single entity
    /// (<c>GET/POST /{EntitySet}({key})/{Name}</c>); <c>false</c> when bound to the entity set
    /// collection (<c>GET/POST /{EntitySet}/{Name}</c>).
    /// </summary>
    public bool IsEntityLevel { get; init; } = false;

    /// <summary>
    /// The delegate's declared return type, unwrapped from <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c>
    /// (and <see langword="null"/> for a <c>void</c>/<c>Task</c>/<c>ValueTask</c> return — every
    /// invocation of such an operation produces <c>204 No Content</c>, never <c>200</c>).
    /// Computed once at bind time; used only to build OpenAPI response documentation
    /// (<c>Produces(...)</c>) for the operation's route — it plays no part in the actual
    /// per-request invocation, which goes through <see cref="Invoke"/> instead.
    /// </summary>
    public Type? ReturnType { get; init; }

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
            string example = isAction
                ? "BindAction(MyAction) where MyAction"
                : "BindFunction(MyFunction) where MyFunction";
            throw new InvalidOperationException(
                $"Cannot bind a compiler-generated method as an OData {kind}. " +
                $"Use a named method instead of a lambda (detected name: '{method.Name}'). " +
                $"Example: {example} is a named method on the profile.");
        }

        var (hasCt, visibleParams) = AsyncDispatchHelper.SplitCancellationToken(method.GetParameters());

        var returnType = method.ReturnType;
        bool isVoidReturn = AsyncDispatchHelper.IsVoidAsyncReturn(returnType);

        // Cache the Result property accessor for Task<T>/ValueTask<T> at registration time
        // rather than using reflection per invocation.
        PropertyInfo? resultProp = AsyncDispatchHelper.GetAsyncResultAccessor(returnType);

        Type? docReturnType = isVoidReturn ? null : AsyncDispatchHelper.UnwrapAsyncReturn(returnType);

        return new BoundOperationDefinition
        {
            Name = method.Name,
            IsAction = isAction,
            IsEntityLevel = isEntityLevel,
            Parameters = visibleParams,
            ReturnType = docReturnType,
            Invoke = AsyncDispatchHelper.BuildInvoker(del, hasCt, isVoidReturn, resultProp)
        };
    }
}
