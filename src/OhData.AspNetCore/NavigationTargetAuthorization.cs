using System;
using System.Collections.Generic;
using System.Linq;

namespace OhData;

/// <summary>
/// #481/#368: the data half of the startup diagnostic that names a navigation whose target entity
/// set is protected more strictly than the entity set declaring it.
/// <para>
/// <b>Diagnostic only.</b> Nothing here runs at request time and nothing here changes what any
/// route does — the owner's ruling on #481 rejected enforcement outright (see
/// <c>OhDataEndpointFactory.WarnNavigationTargetAuthorization</c> for the reasons). This type only
/// answers "what does profile P require for operation category C", as plain data, so the warning
/// can subtract one answer from another.
/// </para>
/// <para>
/// It lives beside <c>EntitySetProfile</c> rather than inside the factory because it touches no
/// ASP.NET Core type: it reads <see cref="AuthorizationConfig"/> and <see cref="OperationAuthRule"/>,
/// which are the framework's own plain-data mirrors of
/// <c>AuthorizationPolicyBuilder</c> (see "Profile types have no ASP.NET Core dependency").
/// </para>
/// </summary>
internal static class NavigationTargetAuthorization
{
    /// <summary>
    /// The requirements profile <paramref name="profile"/> applies to routes in category
    /// <paramref name="category"/>, as normalized human-readable tokens. Empty means "anonymous" —
    /// either no authorization at all, or an explicit <c>AllowAnonymous()</c> on that category.
    /// <para>
    /// Tokens are compared with <see cref="StringComparison.Ordinal"/> and rendered into the
    /// warning verbatim, so the same requirement declared through either authorization model, and
    /// with roles or claim values listed in a different ORDER, produces the same token — otherwise
    /// two profiles that agree would be reported as disagreeing.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Requirements(
        IEntitySetEndpointSource profile, OhDataOperation category)
    {
        IReadOnlyList<OperationAuthRule>? rules = profile.OperationAuthorization;
        if (rules is null)
        {
            // Legacy profile-wide model. RequireAuthorization(policy)/RequireRoles() both set
            // _authRequired, so Required is true whenever Authorization is non-null; the policy and
            // roles are additional AND-ed requirements exactly as the factory applies them.
            AuthorizationConfig? config = profile.Authorization;
            if (config is null || !config.Required) return Array.Empty<string>();

            List<string> legacy = new() { "an authenticated user" };
            if (config.Policy is not null) legacy.Add(PolicyToken(config.Policy));
            if (config.Roles is { Count: > 0 }) legacy.Add(RoleToken(config.Roles));
            return legacy;
        }

        // Mirrors OhDataEndpointFactory.ResolveOperationRule for the no-bound-operation-name case:
        // the LAST generic rule matching the category wins, and a rule naming a bound operation is
        // never generic. Navigation routes never carry a bound-operation name, so that half of the
        // resolution cannot apply here.
        IReadOnlyList<AuthRequirement> resolved = ResolveRuleRequirements(rules, category);
        List<string> tokens = new(resolved.Count);
        foreach (AuthRequirement requirement in resolved) tokens.Add(Describe(requirement));
        return tokens;
    }

    /// <summary>
    /// The requirements the rule model resolves for <paramref name="category"/>, mirroring
    /// <c>OhDataEndpointFactory.ResolveOperationRule</c> for the no-bound-operation-name case: the
    /// LAST generic rule matching the category wins, and a rule naming a bound operation is never
    /// generic. Navigation routes never carry a bound-operation name.
    /// <para>
    /// Split out so <see cref="Requirements"/> and <see cref="ResourceTokens"/> project from ONE
    /// resolution — a second copy of this rule is the independently-derived-second-model hazard
    /// this codebase records repeatedly, and here it would silently change which requirements the
    /// warning cancels.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AuthRequirement> ResolveRuleRequirements(
        IReadOnlyList<OperationAuthRule> rules, OhDataOperation category)
    {
        OperationAuthRule? resolved = rules.LastOrDefault(
            rule => (rule.Operations & category) != 0 && rule.BoundOperationName is null);

        // No rule for the category → ApplyOperationAuth returns without applying anything, and the
        // profile-wide model is mutually exclusive with this one, so the category is anonymous.
        if (resolved is null || resolved.AllowAnonymous) return Array.Empty<AuthRequirement>();
        return resolved.Requirements;
    }

    /// <summary>
    /// #549: the rendered tokens of <paramref name="profile"/>'s RESOURCE requirements for this
    /// category, which <see cref="RequirementsNotApplied"/> refuses to let cancel.
    /// <para>
    /// Empty for the legacy profile-wide model by construction: <c>RequireResource()</c> exists only
    /// on the rule model, so a legacy profile can carry no resource requirement to compare.
    /// </para>
    /// </summary>
    private static HashSet<string> ResourceTokens(
        IEntitySetEndpointSource profile, OhDataOperation category)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<OperationAuthRule>? rules = profile.OperationAuthorization;
        if (rules is null) return set;

        foreach (AuthRequirement r in ResolveRuleRequirements(rules, category)
            .Where(r => r.Kind == AuthRequirementKind.Resource))
        {
            set.Add(Describe(r));
        }
        return set;
    }

    /// <summary>
    /// Everything <paramref name="target"/> requires for <paramref name="category"/> that
    /// <paramref name="declaring"/> does not — i.e. exactly what will not be applied to a route
    /// that reaches <paramref name="target"/>'s rows through <paramref name="declaring"/>. Empty
    /// when the target is equally or less strict, which is never the hazard.
    /// </summary>
    internal static IReadOnlyList<string> RequirementsNotApplied(
        IEntitySetEndpointSource declaring, IEntitySetEndpointSource target, OhDataOperation category)
    {
        IReadOnlyList<string> wanted = Requirements(target, category);
        if (wanted.Count == 0) return Array.Empty<string>();

        IReadOnlyList<string> have = Requirements(declaring, category);

        // #549: a RESOURCE requirement never cancels, even against an identical one on the declaring
        // profile. Every other kind is a statement about the CALLER -- roles, claims, policies,
        // authenticated-user -- so token equality really does imply the check is applied. A resource
        // requirement is a statement about the RESOURCE, and Layer B evaluates it against the entity
        // loaded from the DECLARING set's {key}; it never sees the target set's rows. Two profiles
        // both declaring RequireResource() therefore rendered the same token, cancelled, and the
        // warning went silent on the one requirement kind where equality does not imply protection.
        HashSet<string> resourceWanted = ResourceTokens(target, category);
        List<string> missing = wanted
            .Where(w => resourceWanted.Contains(w) || !have.Contains(w, StringComparer.Ordinal))
            .ToList();
        return missing;
    }

    private static string Describe(AuthRequirement requirement) => requirement.Kind switch
    {
        AuthRequirementKind.AuthenticatedUser => "an authenticated user",
        AuthRequirementKind.Role => RoleToken(requirement.Values ?? Array.Empty<string>()),
        AuthRequirementKind.Claim => ClaimToken(requirement.Name, requirement.Values),
        AuthRequirementKind.Policy => PolicyToken(requirement.Name),
        AuthRequirementKind.Resource => requirement.Name is null
            ? "resource-based authorization"
            : $"the resource-based policy '{requirement.Name}'",
        _ => "an unrecognized requirement",
    };

    // Sorted so RequireRoles("a", "b") and RequireRoles("b", "a") — which mean the same thing, the
    // roles being OR-ed — cannot be reported as a difference.
    private static string RoleToken(IReadOnlyList<string> roles) =>
        $"one of the roles ({string.Join(", ", roles.OrderBy(r => r, StringComparer.Ordinal))})";

    private static string ClaimToken(string? claimType, IReadOnlyList<string>? values) =>
        values is { Count: > 0 }
            ? $"a claim '{claimType}' with one of the values " +
              $"({string.Join(", ", values.OrderBy(v => v, StringComparer.Ordinal))})"
            : $"a claim '{claimType}'";

    private static string PolicyToken(string? policy) => $"the policy '{policy}'";
}
