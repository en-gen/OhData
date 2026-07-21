using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace OhData;

/// <summary>
/// Validates the <c>OData-MaxVersion</c> request header per OData 4.0 Protocol §8.2.7:
/// "a service MUST reject a request whose OData-MaxVersion is lower than the version the
/// service uses to respond." OhData is a 4.0 emitter, so any ceiling below 4.0 -- or a value
/// that cannot be parsed as a version -- cannot be honored and the request is rejected with
/// <c>400 Bad Request</c>. An absent header, or a header naming 4.0 or higher (4.01, 5.0, ...),
/// is unconstrained and the request proceeds. The header is never echoed in responses -- it is
/// request-only (client → server).
/// </summary>
internal static class ODataMaxVersionFilter
{
    private const string HeaderName = "OData-MaxVersion";
    private const double MinimumSupportedVersion = 4.0;

    /// <summary>
    /// Returns a 400 <see cref="IResult"/> if the request's OData-MaxVersion header cannot be
    /// honored, or <see langword="null"/> if the request should proceed.
    /// </summary>
    internal static IResult? Validate(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        string raw = values.ToString();
        if (!TryParseVersion(raw, out double version))
        {
            return OhDataEndpointFactory.ODataError(400, "UnsupportedODataVersion",
                $"The {HeaderName} header value '{raw.Trim()}' could not be parsed as an " +
                "OData version (expected 'major.minor', e.g. '4.0').",
                target: HeaderName);
        }

        if (version < MinimumSupportedVersion)
        {
            return OhDataEndpointFactory.ODataError(400, "UnsupportedODataVersion",
                $"The {HeaderName} header value '{raw.Trim()}' is lower than the minimum " +
                $"version ({MinimumSupportedVersion:0.0}) this service can respond with.",
                target: HeaderName);
        }

        return null;
    }

    /// <summary>
    /// Parses a version string in "major.minor" form (e.g. "4.0", "4.01", "3"), tolerating
    /// surrounding whitespace. Returns <see langword="false"/> for anything else (empty,
    /// non-numeric, extra segments).
    /// </summary>
    private static bool TryParseVersion(string raw, out double version)
    {
        version = 0;
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string[] parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
        {
            return false;
        }

        int minorDigits = 0;
        int minor = 0;
        if (parts.Length == 2)
        {
            string minorPart = parts[1];
            if (minorPart.Length == 0 ||
                !int.TryParse(minorPart, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
            {
                return false;
            }

            minorDigits = minorPart.Length;
        }

        version = major + (minorDigits == 0 ? 0.0 : minor / Math.Pow(10, minorDigits));
        return true;
    }
}
