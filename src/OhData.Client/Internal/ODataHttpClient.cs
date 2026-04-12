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

    internal async Task<T?> PostAsync<T>(string url, T body, CancellationToken ct)
        where T : class
    {
        using var response = await _http.PostAsJsonAsync(url, body, _options.JsonOptions, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct);
    }

    // ── PUT ─────────────────────────────────────────────────────────────────────

    internal async Task<T?> PutAsync<T>(string url, T body, CancellationToken ct)
        where T : class
    {
        using var content = JsonContent.Create(body, options: _options.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
               ?? throw new InvalidOperationException($"PUT to '{url}' returned an empty body.");
    }

    // ── PATCH ───────────────────────────────────────────────────────────────────

    internal async Task<T?> PatchAsync<T>(string url, object body, CancellationToken ct)
        where T : class
    {
        // body may be an anonymous type — serialize via its actual runtime type
        using var content = JsonContent.Create(body, body.GetType(), options: _options.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, url, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await response.Content.ReadFromJsonAsync<T>(_options.JsonOptions, ct)
               ?? throw new InvalidOperationException($"PATCH to '{url}' returned an empty body.");
    }

    // ── DELETE ──────────────────────────────────────────────────────────────────

    internal async Task DeleteAsync(string url, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, url, ct);
    }

    // ── Error handling ──────────────────────────────────────────────────────────

    // Cached options for parsing OData error envelopes — avoids allocating a new instance per error.
    private static readonly JsonSerializerOptions _errorParseOptions =
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw await ODataClientException.FromResponseAsync(
            response, url, _errorParseOptions, ct);
    }
}
