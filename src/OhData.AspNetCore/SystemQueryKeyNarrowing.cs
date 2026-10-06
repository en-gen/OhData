using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace OhData;

/// <summary>
/// What <c>ODataQueryOptions</c> is allowed to see of the query string: only keys whose FIRST
/// character, untrimmed, is <c>$</c> or <c>@</c>. OhData declares OData 4.0, under which any other
/// key is a custom option (Part 2 §5.2) and is never applied. <c>Microsoft.AspNetCore.OData</c> would
/// otherwise apply a bare <c>filter</c> when its optional-<c>$</c> scheme is on, and trims every key
/// before its own <c>$</c> test, so <c>%20$filter</c> would be applied while OhData's gates call it
/// custom (#714, #718). Narrowing the input makes both of those arms unreachable whatever the host
/// configured, which a swap of <c>ODataOptions</c> could not (a <c>WithODataOptions</c> endpoint
/// reads <c>ODataMiniOptions</c> instead).
/// </summary>
internal static class SystemQueryKeyNarrowing
{
    /// <summary>Null when every key already qualifies, so the common request allocates nothing.</summary>
    internal static IQueryCollection? Narrow(IQueryCollection query)
    {
        if (query.Count == 0)
        {
            return null;
        }

        bool anyHidden = false;
        foreach (KeyValuePair<string, StringValues> pair in query)
        {
            if (!IsVisible(pair.Key))
            {
                anyHidden = true;
                break;
            }
        }

        if (!anyHidden)
        {
            return null;
        }

        var kept = new Dictionary<string, StringValues>(query.Count, System.StringComparer.Ordinal);
        foreach (KeyValuePair<string, StringValues> pair in query)
        {
            if (IsVisible(pair.Key))
            {
                kept.Add(pair.Key, pair.Value);
            }
        }

        return new QueryCollection(kept);
    }

    private static bool IsVisible(string key) => key.Length > 0 && (key[0] == '$' || key[0] == '@');
}
