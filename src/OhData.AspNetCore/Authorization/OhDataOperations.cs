using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace OhData.AspNetCore;

/// <summary>
/// The five OhData operation requirements used for resource-based (instance-level) authorization
/// (#199 Layer B). When a profile opts a category into <c>.RequireResource()</c>, OhData loads the
/// entity and evaluates the matching requirement against it via
/// <c>IAuthorizationService.AuthorizeAsync(user, entity, requirement)</c>.
/// </summary>
/// <remarks>
/// These are the framework's own <see cref="OperationAuthorizationRequirement"/> instances (the type
/// the canonical ASP.NET Core resource-based-authorization sample uses). Write an
/// <c>AuthorizationHandler&lt;OperationAuthorizationRequirement, TModel&gt;</c> and switch on
/// <see cref="OperationAuthorizationRequirement.Name"/>:
/// <code>
/// public sealed class OrderAuthorizationHandler
///     : AuthorizationHandler&lt;OperationAuthorizationRequirement, Order&gt;
/// {
///     protected override Task HandleRequirementAsync(
///         AuthorizationHandlerContext ctx, OperationAuthorizationRequirement req, Order order)
///     {
///         if (req.Name == OhDataOperations.Read.Name) ctx.Succeed(req);
///         if (req.Name == OhDataOperations.Update.Name &amp;&amp; order.OwnerId == ctx.User.FindFirst("sub")?.Value)
///             ctx.Succeed(req);
///         return Task.CompletedTask;
///     }
/// }
/// // services.AddScoped&lt;IAuthorizationHandler, OrderAuthorizationHandler&gt;();
/// </code>
/// The resource <em>type</em> (the handler's <c>TModel</c>) discriminates the entity set; the
/// <see cref="OperationAuthorizationRequirement.Name"/> discriminates the operation.
/// </remarks>
public static class OhDataOperations
{
    /// <summary>Read requirement (name <c>"Read"</c>).</summary>
    public static readonly OperationAuthorizationRequirement Read = new() { Name = nameof(Read) };

    /// <summary>Create requirement (name <c>"Create"</c>).</summary>
    public static readonly OperationAuthorizationRequirement Create = new() { Name = nameof(Create) };

    /// <summary>Update requirement (name <c>"Update"</c>).</summary>
    public static readonly OperationAuthorizationRequirement Update = new() { Name = nameof(Update) };

    /// <summary>Delete requirement (name <c>"Delete"</c>).</summary>
    public static readonly OperationAuthorizationRequirement Delete = new() { Name = nameof(Delete) };

    /// <summary>Bound-operation invocation requirement (name <c>"Invoke"</c>).</summary>
    public static readonly OperationAuthorizationRequirement Invoke = new() { Name = nameof(Invoke) };
}
