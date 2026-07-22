using System;
using System.Collections.Generic;

namespace OhData;

/// <summary>
/// The coarse operation categories an OhData route can fall into, used to target per-operation
/// authorization (#199). A <c>[Flags]</c> enum so a single rule can cover several categories
/// (e.g. <see cref="Write"/> = <see cref="Create"/> | <see cref="Update"/> | <see cref="Delete"/>).
/// </summary>
[Flags]
public enum OhDataOperation
{
    /// <summary>No category.</summary>
    None = 0,

    /// <summary>Reads: collection/by-id/navigation/property/$count/$value/$ref GETs.</summary>
    Read = 1 << 0,

    /// <summary>Creates: <c>POST</c> to a collection, and <c>POST</c> to a collection navigation.</summary>
    Create = 1 << 1,

    /// <summary>
    /// Updates: <c>PUT</c>/<c>PATCH</c> on an entity, property, or navigation; adding/setting a
    /// link (<c>POST</c>/<c>PUT</c> <c>$ref</c>); and the mutations that leave the row intact —
    /// clearing a property value (<c>DELETE …/{Property}</c>) and removing a link
    /// (<c>DELETE …/$ref</c>).
    /// </summary>
    Update = 1 << 2,

    /// <summary>Deletes that remove a whole entity: <c>DELETE</c> on an entity or on a collection navigation.</summary>
    Delete = 1 << 3,

    /// <summary>Bound function/action invocation (collection- or entity-bound).</summary>
    Invoke = 1 << 4,

    /// <summary><see cref="Create"/> | <see cref="Update"/> | <see cref="Delete"/>.</summary>
    Write = Create | Update | Delete,

    /// <summary>Every category: <see cref="Read"/> | <see cref="Write"/> | <see cref="Invoke"/>.</summary>
    All = Read | Write | Invoke,
}

/// <summary>
/// The kind of a single accumulated authorization requirement. Mirrors the shape of
/// <c>Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder</c> as plain data, so a profile
/// can declare requirements without referencing ASP.NET Core types. The OhData factory replays these
/// onto the real policy builder.
/// </summary>
public enum AuthRequirementKind
{
    /// <summary>Any authenticated identity (<c>RequireAuthenticatedUser</c>).</summary>
    AuthenticatedUser,

    /// <summary>At least one of the named roles (<c>RequireRole</c>; OR within, AND across requirements).</summary>
    Role,

    /// <summary>A claim of the given type, optionally restricted to a set of values (<c>RequireClaim</c>).</summary>
    Claim,

    /// <summary>A named ASP.NET Core authorization policy (applied as a separate requirement).</summary>
    Policy,

    /// <summary>
    /// Resource-based (instance-level) authorization — evaluated inside the handler with the loaded
    /// entity as the resource (Layer B). <see cref="AuthRequirement.Name"/> optionally names a policy;
    /// when null, OhData uses its built-in <c>OhDataOperations</c> requirement.
    /// </summary>
    Resource,
}

/// <summary>
/// One accumulated authorization requirement (plain data). Requirements on a category combine with
/// AND semantics, exactly like <c>AuthorizationPolicyBuilder</c>.
/// </summary>
/// <param name="Kind">The requirement kind.</param>
/// <param name="Name">Policy name (for <see cref="AuthRequirementKind.Policy"/>/<see cref="AuthRequirementKind.Resource"/>) or claim type (for <see cref="AuthRequirementKind.Claim"/>); otherwise null.</param>
/// <param name="Values">Role names (for <see cref="AuthRequirementKind.Role"/>) or accepted claim values (for <see cref="AuthRequirementKind.Claim"/>); otherwise null.</param>
public sealed record AuthRequirement(
    AuthRequirementKind Kind,
    string? Name = null,
    IReadOnlyList<string>? Values = null);

/// <summary>
/// A resolved authorization rule for one or more <see cref="OhDataOperation"/> categories.
/// Produced by <c>EntitySetProfile.ConfigureAuthorization</c>; consumed by the OhData factory to apply
/// per-route authorization (#199).
/// </summary>
/// <param name="Operations">The categories this rule applies to.</param>
/// <param name="AllowAnonymous">When true the operation is explicitly anonymous; <see cref="Requirements"/> is empty.</param>
/// <param name="Requirements">The AND-combined requirements (empty when <see cref="AllowAnonymous"/>).</param>
/// <param name="BoundOperationName">When non-null, this rule targets a single named bound operation (from <c>Invoke("Name", …)</c>).</param>
public sealed record OperationAuthRule(
    OhDataOperation Operations,
    bool AllowAnonymous,
    IReadOnlyList<AuthRequirement> Requirements,
    string? BoundOperationName = null);

/// <summary>
/// Outer builder for <c>ConfigureAuthorization(auth =&gt; …)</c>. Each selector takes a nested
/// per-category lambda that configures that category's requirements.
/// </summary>
public interface IAuthorizationRuleBuilder
{
    /// <summary>Configure authorization for read operations (<see cref="OhDataOperation.Read"/>).</summary>
    IAuthorizationRuleBuilder Read(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for creates (<see cref="OhDataOperation.Create"/>).</summary>
    IAuthorizationRuleBuilder Create(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for updates (<see cref="OhDataOperation.Update"/>).</summary>
    IAuthorizationRuleBuilder Update(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for deletes (<see cref="OhDataOperation.Delete"/>).</summary>
    IAuthorizationRuleBuilder Delete(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for all writes (<see cref="OhDataOperation.Write"/>).</summary>
    IAuthorizationRuleBuilder Writes(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for every category (<see cref="OhDataOperation.All"/>).</summary>
    IAuthorizationRuleBuilder All(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for all bound operations (<see cref="OhDataOperation.Invoke"/>).</summary>
    IAuthorizationRuleBuilder Invoke(Action<ICategoryAuthorizationBuilder> configure);

    /// <summary>Configure authorization for a single named bound function/action.</summary>
    IAuthorizationRuleBuilder Invoke(string boundOperationName, Action<ICategoryAuthorizationBuilder> configure);
}

/// <summary>
/// Inner per-category builder. Mirrors <c>AuthorizationPolicyBuilder</c>: requirements accumulate and
/// combine with AND. <see cref="AllowAnonymous"/> is exclusive — it cannot be combined with any
/// <c>Require*</c> call on the same category.
/// </summary>
public interface ICategoryAuthorizationBuilder
{
    /// <summary>Require any authenticated identity.</summary>
    ICategoryAuthorizationBuilder RequireAuthenticatedUser();

    /// <summary>Require at least one of the given roles (OR within; AND with other requirements).</summary>
    ICategoryAuthorizationBuilder RequireRole(params string[] roles);

    /// <summary>Require a claim of <paramref name="claimType"/>, optionally restricted to <paramref name="allowedValues"/>.</summary>
    ICategoryAuthorizationBuilder RequireClaim(string claimType, params string[] allowedValues);

    /// <summary>Require a named ASP.NET Core authorization policy.</summary>
    ICategoryAuthorizationBuilder RequirePolicy(string policyName);

    /// <summary>
    /// Enable resource-based (instance-level) authorization for this category (Layer B). OhData loads
    /// the entity and evaluates its built-in <c>OhDataOperations</c> requirement against it.
    /// </summary>
    ICategoryAuthorizationBuilder RequireResource();

    /// <summary>
    /// Enable resource-based (instance-level) authorization using a named policy — OhData evaluates
    /// <paramref name="policyName"/> with the loaded entity as the resource, so the policy's
    /// resource-based handlers fire (Layer B).
    /// </summary>
    ICategoryAuthorizationBuilder RequireResource(string policyName);

    /// <summary>Explicitly allow anonymous access. Exclusive — cannot be combined with any <c>Require*</c>.</summary>
    void AllowAnonymous();
}
