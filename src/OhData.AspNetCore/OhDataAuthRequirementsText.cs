using System;
using System.Collections.Generic;
using System.Linq;
using OhData.Abstractions;

namespace OhData.AspNetCore;

/// <summary>
/// How much of OhData's structured authorization data (#199) the opt-in "auth requirements"
/// documentation filters (#220) render into an operation's description.
/// </summary>
/// <remarks>
/// Exact required claim <em>values</em> are the sensitive information-disclosure surface (a value
/// can be an internal identifier), so they are emitted only at <see cref="Full"/>. Requirement
/// kinds and their non-secret identifiers — claim <em>types</em>, role names, policy names — are
/// safe to surface and appear at both levels.
/// </remarks>
public enum AuthRequirementDisclosure
{
    /// <summary>
    /// Render requirement kinds and their non-secret identifiers (claim types, role names, policy
    /// names) but never the exact required claim <em>values</em>. The safe default for public docs.
    /// </summary>
    Kinds,

    /// <summary>
    /// Render full detail, including the exact required claim values. Use only where the generated
    /// document is not public (an internal/trusted API explorer).
    /// </summary>
    Full,
}

/// <summary>
/// Shared renderer that turns a route's structured <see cref="AuthRequirement"/> set (#199) into a
/// single human-readable requirements sentence for the OpenAPI/NSwag "auth requirements" filters
/// (#220). Kept in the core package so both companions render byte-identical text.
/// </summary>
public static class OhDataAuthRequirementsText
{
    /// <summary>
    /// Renders <paramref name="requirements"/> at the given <paramref name="disclosure"/> level, or
    /// <c>null</c> when there is nothing statically documentable (empty set, or only resource-based
    /// requirements). Requirements combine with AND; role names within a single requirement are OR.
    /// </summary>
    public static string? Render(IReadOnlyList<AuthRequirement>? requirements, AuthRequirementDisclosure disclosure)
    {
        if (requirements is null || requirements.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (AuthRequirement req in requirements)
        {
            switch (req.Kind)
            {
                case AuthRequirementKind.AuthenticatedUser:
                    parts.Add("an authenticated user");
                    break;

                case AuthRequirementKind.Role when req.Values is { Count: > 0 }:
                    // Roles are OR-within-requirement. Names are not the sensitive surface (#220).
                    parts.Add("role " + string.Join(" or ", req.Values.Select(Code)));
                    break;

                case AuthRequirementKind.Claim:
                    // Claim VALUES are the sensitive surface — only emitted at Full disclosure (#220).
                    parts.Add(disclosure == AuthRequirementDisclosure.Full && req.Values is { Count: > 0 }
                        ? $"claim {Code(req.Name)} = " + string.Join(" or ", req.Values.Select(Code))
                        : $"claim {Code(req.Name)}");
                    break;

                case AuthRequirementKind.Policy:
                    // A named policy is opaque — surface its name only, never its inner logic (#220).
                    parts.Add($"policy {Code(req.Name)}");
                    break;

                case AuthRequirementKind.Resource:
                    // Layer B (resource/instance-level) authorization is not statically documentable
                    // beyond the possible 403 the baseline emits (#220) — skip it here.
                    break;
            }
        }

        return parts.Count == 0 ? null : "Requires " + string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// The label the "auth requirements" filters prefix onto the rendered requirements sentence.
    /// </summary>
    public const string SectionLabel = "**Authorization:**";

    /// <summary>
    /// Appends the rendered <paramref name="requirements"/> as an <see cref="SectionLabel"/> section
    /// onto an operation's <paramref name="existingDescription"/>, returning the new description.
    /// Keeps the two companions byte-identical and idempotent: an operation that already carries the
    /// exact section is returned unchanged, so registering the filter twice never double-appends.
    /// </summary>
    public static string AppendSection(string? existingDescription, string requirements)
    {
        string section = SectionLabel + " " + requirements;
        if (existingDescription is { Length: > 0 } existing)
        {
            return existing.Contains(section, StringComparison.Ordinal)
                ? existing
                : existing + "\n\n" + section;
        }
        return section;
    }

    private static string Code(string? value) => "`" + (value ?? string.Empty) + "`";
}
