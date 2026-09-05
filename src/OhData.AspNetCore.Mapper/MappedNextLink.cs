using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Builds the server-driven continuation a mapped profile emits, and reads the
/// <c>Prefer: maxpagesize</c> that can narrow the page.
/// </summary>
/// <remarks>
/// <para>
/// The continuation is spelled with <c>$skip</c> rather than <c>$skiptoken</c>. That is not a
/// shortcut: this profile honours <c>$skip</c> on the way in, so the link it emits is one it
/// provably re-reads, while <c>$skiptoken</c> would be a second option to implement whose only job
/// is to encode the same offset. §11.2.5.7 makes a <c>nextLink</c> opaque to the client, so its
/// spelling is the service's business.
/// </para>
/// <para>
/// Every other query option is carried through verbatim, so the second page is the same request
/// against a later window. The framework's own <c>$</c>-sigil gate has already refused any option
/// this route does not implement before the handler runs, so nothing unhonoured can be copied into a
/// link — the defect #359 records for the routes that had no gate.
/// </para>
/// </remarks>
public static class MappedNextLink
{
    private const string MaxPageSizePreference = "maxpagesize";

    /// <summary>
    /// The absolute URL of the next page: this request with <c>$skip</c> advanced and, when the
    /// client bounded the result, <c>$top</c> reduced by what has already been served.
    /// </summary>
    public static string? Build(ODataQueryOptions options, int nextSkip, int? remainingTop)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (remainingTop is <= 0) return null;

        HttpRequest request = options.Request;
        var parameters = new List<KeyValuePair<string, string?>>();

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> entry in request.Query)
        {
            if (IsSkip(entry.Key) || IsTop(entry.Key)) continue;
            foreach (string? value in entry.Value)
                parameters.Add(new KeyValuePair<string, string?>(entry.Key, value));
        }

        parameters.Add(new KeyValuePair<string, string?>(
            "$skip", nextSkip.ToString(CultureInfo.InvariantCulture)));

        if (remainingTop is int top)
        {
            parameters.Add(new KeyValuePair<string, string?>(
                "$top", top.ToString(CultureInfo.InvariantCulture)));
        }

        var query = new StringBuilder();
        foreach (KeyValuePair<string, string?> parameter in parameters)
        {
            query.Append(query.Length == 0 ? '?' : '&')
                 .Append(Uri.EscapeDataString(parameter.Key))
                 .Append('=')
                 .Append(Uri.EscapeDataString(parameter.Value ?? string.Empty));
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{query}";
    }

    /// <summary>Reads a <c>Prefer: maxpagesize=N</c> the client sent.</summary>
    public static bool TryReadMaxPageSize(ODataQueryOptions options, out int maxPageSize)
    {
        maxPageSize = 0;
        if (options is null) return false;

        foreach (string? header in options.Request.Headers["Prefer"])
        {
            if (header is null) continue;

            foreach (string token in header.Split(','))
            {
                string trimmed = token.Trim();
                if (!trimmed.StartsWith(MaxPageSizePreference, StringComparison.OrdinalIgnoreCase))
                    continue;

                int equals = trimmed.IndexOf('=');
                if (equals < 0) continue;

                if (int.TryParse(
                        trimmed.Substring(equals + 1).Trim().Trim('"'),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed > 0)
                {
                    maxPageSize = parsed;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Announces the page size actually applied. RFC 7240 forbids claiming a preference that was not
    /// applied, so this is called only where the value really bounded the page.
    /// </summary>
    public static void ApplyPreference(ODataQueryOptions options, int appliedPageSize)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        options.Request.HttpContext.Response.Headers["Preference-Applied"] =
            $"{MaxPageSizePreference}={appliedPageSize.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool IsSkip(string name) => string.Equals(name, "$skip", StringComparison.OrdinalIgnoreCase);

    private static bool IsTop(string name) => string.Equals(name, "$top", StringComparison.OrdinalIgnoreCase);
}
