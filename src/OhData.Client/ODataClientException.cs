using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Client;

/// <summary>
/// Thrown when an OData HTTP request returns a non-success status code.
/// Parses the OData error envelope (<c>{ "error": { "code": "...", "message": "..." } }</c>)
/// when present; falls back to the raw response body otherwise.
/// </summary>
public sealed class ODataClientException : Exception
{
    /// <summary>HTTP status code returned by the server.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// The OData error code from the response body (e.g. <c>"NotFound"</c>, <c>"BadRequest"</c>).
    /// Empty string when the response was not an OData error envelope.
    /// </summary>
    public string ODataErrorCode { get; }

    /// <summary>
    /// The OData error message from the response body, or the raw response text
    /// when the body is not an OData error envelope.
    /// </summary>
    public string ODataErrorMessage { get; }

    private ODataClientException(int statusCode, string code, string message, string url)
        : base($"OData request to '{url}' failed with HTTP {statusCode}: [{code}] {message}")
    {
        StatusCode        = statusCode;
        ODataErrorCode    = code;
        ODataErrorMessage = message;
    }

    internal static async Task<ODataClientException> FromResponseAsync(
        HttpResponseMessage response,
        string requestUrl,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        var statusCode = (int)response.StatusCode;

        string? body = null;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { /* best-effort */ }

        if (body is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    var code    = errorEl.TryGetProperty("code",    out var c) ? c.GetString() ?? "" : "";
                    var message = errorEl.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    return new ODataClientException(statusCode, code, message, requestUrl);
                }
            }
            catch { /* not an OData envelope — fall through */ }
        }

        var raw = body is { Length: > 500 } ? body[..500] + "…" : body ?? "";
        return new ODataClientException(statusCode, "", raw, requestUrl);
    }
}
