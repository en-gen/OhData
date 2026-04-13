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
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, url, ct);
        var envelope = await response.Content
            .ReadFromJsonAsync<ODataCollectionResponse<T>>(_options.JsonOptions, ct);
        return envelope?.Value ?? [];
    }

    internal async Task<ODataPage<T>> GetPageAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct);
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

    // ── GET single ──────────────────────────────────────────────────────────────

    internal async Task<T?> GetSingleAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
    }

    // ── GET $count ──────────────────────────────────────────────────────────────

    internal async Task<long> GetCountAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
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
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return (null, null);
        T? entity = await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
        string? etag = response.Headers.ETag?.Tag?.Trim('"');
        return (entity, etag);
    }

    // ── Error handling ──────────────────────────────────────────────────────────

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw await ODataClientException.FromResponseAsync(response, url, ct);
    }
}
