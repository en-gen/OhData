using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OhData.Client.Internal;

/// <summary>
/// Thin HTTP wrapper that speaks OData: handles collection envelopes, single-entity
/// responses, $count plain-text, and OData error bodies.
/// Per-instance — no static state.
/// </summary>
internal sealed class ODataHttpClient
{
    private readonly HttpClient _http;
    private readonly OhDataClientOptions _options;

    internal ODataHttpClient(HttpClient http, OhDataClientOptions options)
    {
        _http = http;
        _options = options;
    }

    // ── GET collection ──────────────────────────────────────────────────────────

    internal async Task<List<T>> GetCollectionAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, url, ct);
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionResponse<T>>(_options.JsonOptions, ct);
        return envelope?.Value ?? [];
    }

    internal async Task<ODataPage<T>> GetPageAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, url, ct);
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionResponse<T>>(_options.JsonOptions, ct);
        return new ODataPage<T>
        {
            Items = envelope?.Value ?? [],
            TotalCount = envelope?.Count,
            NextLink = envelope?.NextLink,
        };
    }

    /// <summary>
    /// Fetches a page using an absolute URL (e.g. a <c>@odata.nextLink</c> value).
    /// Unlike <see cref="GetPageAsync{T}(string, CancellationToken)"/>, the URL is used
    /// as-is with <see cref="HttpMethod.Get"/> so no base-address composition occurs.
    /// </summary>
    internal async Task<ODataPage<T>> GetPageByAbsoluteUrlAsync<T>(string absoluteUrl, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, absoluteUrl, ct);
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionResponse<T>>(_options.JsonOptions, ct);
        return new ODataPage<T>
        {
            Items = envelope?.Value ?? [],
            TotalCount = envelope?.Count,
            NextLink = envelope?.NextLink,
        };
    }

    // ── GET single ──────────────────────────────────────────────────────────────

    internal async Task<T?> GetSingleAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (_options.NotFoundBehavior == NotFoundBehavior.Throw)
                throw await ODataClientException.FromResponseAsync(response, url, ct);
            return null;
        }
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
    }

    // ── GET $count ──────────────────────────────────────────────────────────────

    internal async Task<long> GetCountAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, url, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        string trimmed = text.Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long count))
        {
            throw new InvalidOperationException(
                $"GET '{url}' returned a non-numeric $count body: '{trimmed}'");
        }

        return count;
    }

    // ── POST ────────────────────────────────────────────────────────────────────

    internal async Task<T?> PostAsync<T>(string url, T body, bool preferMinimal, CancellationToken ct)
        where T : class
    {
        using var content = JsonContent.Create(body, options: _options.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (preferMinimal)
            request.Headers.Add("Prefer", "return=minimal");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
    }

    internal Task<T?> PostAsync<T>(string url, T body, CancellationToken ct)
        where T : class
        => PostAsync(url, body, preferMinimal: false, ct);

    // ── PUT ─────────────────────────────────────────────────────────────────────

    internal async Task<T?> PutAsync<T>(string url, T body, string? ifMatch, bool preferMinimal, CancellationToken ct)
        where T : class
    {
        using var content = JsonContent.Create(body, options: _options.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (preferMinimal)
            request.Headers.Add("Prefer", "return=minimal");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
               ?? throw new InvalidOperationException($"PUT to '{url}' returned an empty body.");
    }

    internal Task<T?> PutAsync<T>(string url, T body, CancellationToken ct)
        where T : class
        => PutAsync(url, body, ifMatch: null, preferMinimal: false, ct);

    // ── PATCH ───────────────────────────────────────────────────────────────────

    internal async Task<T?> PatchAsync<T>(string url, object body, string? ifMatch, bool preferMinimal, CancellationToken ct)
        where T : class
    {
        // body may be an anonymous type — serialize via its actual runtime type
        using var content = JsonContent.Create(body, body.GetType(), options: _options.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (preferMinimal)
            request.Headers.Add("Prefer", "return=minimal");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
               ?? throw new InvalidOperationException($"PATCH to '{url}' returned an empty body.");
    }

    internal Task<T?> PatchAsync<T>(string url, object body, CancellationToken ct)
        where T : class
        => PatchAsync<T>(url, body, ifMatch: null, preferMinimal: false, ct);

    // ── DELETE ──────────────────────────────────────────────────────────────────

    internal async Task DeleteAsync(string url, string? ifMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
    }

    internal Task DeleteAsync(string url, CancellationToken ct)
        => DeleteAsync(url, ifMatch: null, ct);

    // ── GET single with ETag ────────────────────────────────────────────────────

    internal async Task<(T? Entity, string? ETag)> GetSingleWithETagAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (_options.NotFoundBehavior == NotFoundBehavior.Throw)
                throw await ODataClientException.FromResponseAsync(response, url, ct);
            return (null, null);
        }
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return (null, null);
        T? entity = await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
        string? etag = response.Headers.ETag?.Tag?.Trim('"');
        return (entity, etag);
    }

    // ── GET single, conditional (If-None-Match) ─────────────────────────────────

    /// <summary>
    /// GET <c>/{EntitySet}(key)</c> with an optional <c>If-None-Match</c> request header for
    /// conditional retrieval (RFC 7232 §3.2 / OData §8.2.5).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Server confirms the cached copy is current (HTTP 304) → returns
    ///       <c>(Entity: null, ETag: &lt;current&gt;, NotModified: true)</c>. No body is read.</item>
    /// <item>Server returns a fresh representation (HTTP 200) → returns
    ///       <c>(Entity: &lt;entity&gt;, ETag: &lt;current&gt;, NotModified: false)</c>.</item>
    /// <item>Entity not found (HTTP 404) → returns <c>(null, null, false)</c>, or throws
    ///       <see cref="ODataClientException"/> when <see cref="OhDataClientOptions.NotFoundBehavior"/>
    ///       is <see cref="NotFoundBehavior.Throw"/> — same convention as <see cref="GetSingleAsync{T}"/>.</item>
    /// </list>
    /// When <paramref name="ifNoneMatch"/> is <see langword="null"/>, no conditional header is sent
    /// and the call behaves like <see cref="GetSingleWithETagAsync{T}"/> (always <c>NotModified: false</c>).
    /// </remarks>
    internal async Task<(T? Entity, string? ETag, bool NotModified)> GetSingleIfChangedAsync<T>(
        string url, string? ifNoneMatch, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            string? currentETag = response.Headers.ETag?.Tag?.Trim('"');
            return (null, currentETag, true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (_options.NotFoundBehavior == NotFoundBehavior.Throw)
                throw await ODataClientException.FromResponseAsync(response, url, ct);
            return (null, null, false);
        }

        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return (null, null, false);

        T? entity = await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
        string? etag = response.Headers.ETag?.Tag?.Trim('"');
        return (entity, etag, false);
    }

    // ── Error handling ──────────────────────────────────────────────────────────

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw await ODataClientException.FromResponseAsync(response, url, ct);
    }
}
