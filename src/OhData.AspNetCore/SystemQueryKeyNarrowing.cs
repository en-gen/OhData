using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;
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

    /// <summary>
    /// The Priority-1 route hands the profile an options object whose <c>ApplyTo</c> re-reads the REAL
    /// <c>Request.Query</c> (<c>AddAutoSelectExpandProperties</c> -> <c>GetODataQueryParameters</c>,
    /// <c>ODataQueryOptions.cs:827</c> and <c>:887-917</c> at <c>a05e1ad0</c>), which the narrowing is
    /// no longer in force for. That method trims each key (<c>:892</c>), prefixes a bare supported name
    /// with <c>$</c> when the optional-<c>$</c> scheme is on (<c>:906-911</c>), and <c>Add</c>s into a
    /// case-insensitive dictionary, so two distinct keys that normalize alike throw there and surface as
    /// a <c>500</c> from inside the profile. Returns the offending option, or null (and allocates nothing)
    /// when no two keys collide. An empty trimmed key is refused too: with the scheme on it reaches
    /// <c>IsSupportedQueryOption("")</c>, which throws.
    /// </summary>
    internal static string? FindNormalizedCollision(HttpRequest request, ODataQueryOptions options)
    {
        IQueryCollection query = request.Query;
        if (query.Count == 0)
        {
            return null;
        }

        // The scheme's state is read off Microsoft's own answer for a bare name, so the same
        // container/resolver/options precedence that governs ApplyTo governs this check.
        bool? schemeOn = null;
        bool SchemeOn() => schemeOn ??= options.IsSupportedQueryOption("top");

        foreach (KeyValuePair<string, StringValues> a in query)
        {
            System.ReadOnlySpan<char> na = Normalized(a.Key);
            if (na.IsEmpty)
            {
                if (SchemeOn())
                {
                    return "an empty parameter name";
                }

                continue;
            }

            foreach (KeyValuePair<string, StringValues> b in query)
            {
                if (ReferenceEquals(a.Key, b.Key) || string.Equals(a.Key, b.Key, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!MemoryExtensions.Equals(na, Normalized(b.Key), System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Equal after trim and optional-$ stripping is necessary, not sufficient: MS only adds
                // the keys it recognises, so confirm both would really land in its dictionary.
                string? addedA = AddedName(a.Key, options, SchemeOn());
                string? addedB = AddedName(b.Key, options, SchemeOn());
                if (addedA is not null && addedB is not null &&
                    string.Equals(addedA, addedB, System.StringComparison.OrdinalIgnoreCase))
                {
                    return addedA.StartsWith('@') ? "a parameter alias" : $"'{addedA}'";
                }
            }
        }

        return null;
    }

    private static System.ReadOnlySpan<char> Normalized(string key)
    {
        System.ReadOnlySpan<char> t = key.AsSpan().Trim();
        return t.Length > 0 && t[0] == '$' ? t.Slice(1) : t;
    }

    private static string? AddedName(string key, ODataQueryOptions options, bool schemeOn)
    {
        string t = key.Trim();
        if (t.StartsWith('@'))
        {
            return t;
        }

        if (!schemeOn)
        {
            return t.StartsWith('$') ? t : null;
        }

        return options.IsSupportedQueryOption(t) ? (t.StartsWith('$') ? t : "$" + t) : null;
    }

    private static bool IsVisible(string key) => key.Length > 0 && (key[0] == '$' || key[0] == '@');
}
