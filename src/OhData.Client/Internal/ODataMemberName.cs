using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client.Internal;

/// <summary>
/// #253: resolves the OData query-option name a client emits for a CLR member so it matches the
/// server's EDM name. A member carrying <c>[System.Text.Json.Serialization.JsonPropertyName]</c> is
/// emitted under that exact name (verbatim, ahead of any naming policy — the same precedence the
/// server's EDM and System.Text.Json use); otherwise the configured naming policy converts the CLR
/// name (or the CLR name verbatim when no policy is set). Keeps <c>$filter</c>/<c>$select</c>/
/// <c>$orderby</c>/<c>$expand</c> property names in agreement with the server's <c>$metadata</c>.
/// </summary>
/// <remarks>
/// #253 completion: this applies UNIFORMLY to structural AND navigation members (reverses #184) — the
/// server now renames navigation identifiers too, so a <c>[JsonPropertyName]</c>-renamed navigation is
/// emitted under its JSON name on every path segment, exactly like a structural leaf.
/// </remarks>
internal static class ODataMemberName
{
    internal static string Resolve(MemberInfo member, JsonNamingPolicy? namingPolicy)
    {
        JsonPropertyNameAttribute? rename = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (rename is not null) return rename.Name;
        return namingPolicy?.ConvertName(member.Name) ?? member.Name;
    }
}
