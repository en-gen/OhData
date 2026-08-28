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
        EnsureNextLinkOriginAllowed(absoluteUrl);
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

    // ── nextLink origin policy (#460) ───────────────────────────────────────────

    /// <summary>
    /// Refuses a <c>@odata.nextLink</c> that names an origin other than the client's
    /// <see cref="HttpClient.BaseAddress"/>, unless
    /// <see cref="OhDataClientOptions.FollowCrossOriginNextLinks"/> says otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two <c>…ByAbsoluteUrlAsync</c> methods build a fresh <see cref="HttpRequestMessage"/> for
    /// a URL that came out of a response <em>body</em>, and <see cref="HttpClient"/> attaches its
    /// <see cref="HttpClient.DefaultRequestHeaders"/> — <c>Authorization</c> included — to it. That
    /// is not a redirect, so <see cref="HttpClientHandler"/>'s cross-origin credential stripping
    /// never runs, and a body-injected link exfiltrates the token to the host it names. The guard
    /// belongs here rather than in the walker because this is the one place the request is built.
    /// </para>
    /// <para>
    /// A relative link is passed through untouched: <see cref="HttpClient"/> resolves it against
    /// <see cref="HttpClient.BaseAddress"/>, which makes it same-origin by construction. A link this
    /// method cannot parse is also passed through, so the request layer keeps producing its own
    /// error for a malformed URL rather than this one mislabelling it as a policy violation.
    /// </para>
    /// </remarks>
    private void EnsureNextLinkOriginAllowed(string nextLink)
    {
        if (_options.FollowCrossOriginNextLinks) return;

        if (!Uri.TryCreate(nextLink, UriKind.RelativeOrAbsolute, out Uri? target)) return;
        if (!target.IsAbsoluteUri) return;

        Uri? baseAddress = _http.BaseAddress;
        if (baseAddress is not null && baseAddress.IsAbsoluteUri && IsSameOrigin(baseAddress, target))
            return;

        // The offending URL is quoted because it is the whole diagnostic: the caller needs to see
        // which host the server tried to send them to. It is server-supplied text, so callers that
        // log this should treat it as untrusted like any other response content.
        throw new InvalidOperationException(
            $"Refusing to follow the '@odata.nextLink' '{nextLink}': it names a different origin " +
            $"from the client's base address ('{baseAddress?.GetLeftPart(UriPartial.Authority) ?? "<none>"}'). " +
            "A nextLink is read out of a response body, and following it would re-attach this " +
            "HttpClient's default headers - including Authorization - to a host the server chose. " +
            $"Set {nameof(OhDataClientOptions)}.{nameof(OhDataClientOptions.FollowCrossOriginNextLinks)} " +
            "to true if this service legitimately pages across origins.");
    }

    /// <summary>
    /// RFC 6454 origin comparison: scheme, host and port must all match.
    /// <see cref="Uri.Port"/> reports the scheme's default when none is given, so
    /// <c>https://host</c> and <c>https://host:443</c> compare equal.
    /// </summary>
    private static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    // ── GET collection, annotation-preserving ───────────────────────────────────

    // These sit ALONGSIDE the methods above rather than replacing them. Recovering OData control
    // information means buffering the body and reading it a second time as a JsonDocument, and the
    // methods above deliberately stream (HttpCompletionOption.ResponseHeadersRead + a single
    // ReadFromJsonAsync). Every existing read would otherwise pay for a feature most callers never
    // look at, so the cost is confined to the terminal operations that return an annotated result.
    // Entity binding is literally the same code — same envelope type, same JsonSerializerOptions —
    // so an annotated read cannot bind an entity differently from a plain one.

    internal async Task<ODataAnnotatedPage<T>> GetAnnotatedPageAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, url, ct);
        return await ReadAnnotatedPageAsync<T>(response, ct);
    }

    /// <summary>
    /// Annotation-preserving counterpart of
    /// <see cref="GetPageByAbsoluteUrlAsync{T}(string, CancellationToken)"/>.
    /// </summary>
    internal async Task<ODataAnnotatedPage<T>> GetAnnotatedPageByAbsoluteUrlAsync<T>(
        string absoluteUrl, CancellationToken ct)
        where T : class
    {
        EnsureNextLinkOriginAllowed(absoluteUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, absoluteUrl, ct);
        return await ReadAnnotatedPageAsync<T>(response, ct);
    }

    private async Task<ODataAnnotatedPage<T>> ReadAnnotatedPageAsync<T>(
        HttpResponseMessage response, CancellationToken ct)
        where T : class
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        ODataCollectionResponse<T>? envelope = body.Length == 0
            ? null
            : JsonSerializer.Deserialize<ODataCollectionResponse<T>>(body, _options.JsonOptions);

        (ODataEntityAnnotations envelopeAnnotations, IReadOnlyList<ODataEntityAnnotations> itemAnnotations) =
            ODataAnnotationReader.ReadCollection(body, AnnotationNameComparer);

        List<T> items = envelope?.Value ?? [];
        var entries = new List<ODataAnnotatedEntity<T>>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            // A JSON null inside `value` is not a legal OData entity but a broken server can send
            // one, and ODataAnnotatedEntity refuses to pair annotations with a null entity — drop it
            // rather than throwing out of a read. Alignment is positional and both lists come from
            // the same bytes, so the index guard can only fire for a non-array `value`.
            if (items[i] is not T entity) continue;
            ODataEntityAnnotations annotations = i < itemAnnotations.Count
                ? itemAnnotations[i]
                : ODataEntityAnnotations.Empty;
            entries.Add(new ODataAnnotatedEntity<T>(
                entity, annotations, _options.JsonOptions.PropertyNamingPolicy));
        }

        return new ODataAnnotatedPage<T>
        {
            Entries = entries,
            TotalCount = envelope?.Count,
            // The annotation surface represents every link as a Uri (see ODataAnnotatedPage.NextLink).
            // RelativeOrAbsolute because OData permits either; an unparseable value becomes null rather
            // than throwing out of a read, matching ODataEntityAnnotations.NextLinkFor.
            NextLink = envelope?.NextLink is string nextLink
                && Uri.TryCreate(nextLink, UriKind.RelativeOrAbsolute, out Uri? nextLinkUri)
                    ? nextLinkUri
                    : null,
            Annotations = envelopeAnnotations,
        };
    }

    // ── GET single, annotation-preserving ───────────────────────────────────────

    internal async Task<ODataAnnotatedEntity<T>?> GetAnnotatedSingleAsync<T>(string url, CancellationToken ct)
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
        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0) return null;

        T? entity = JsonSerializer.Deserialize<T>(body, _options.JsonOptions);
        if (entity is null) return null;

        return new ODataAnnotatedEntity<T>(
            entity,
            ODataAnnotationReader.ReadSingle(body, AnnotationNameComparer),
            _options.JsonOptions.PropertyNamingPolicy);
    }

    // Annotations are looked up with the BINDER's comparer, so a camelCase server's annotations
    // resolve exactly as its entity properties do. PropertyNameCaseInsensitive is true by default.
    private StringComparer AnnotationNameComparer =>
        _options.JsonOptions.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
            request.Headers.TryAddWithoutValidation("If-Match", FormatEntityTag(ifMatch));
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
            request.Headers.TryAddWithoutValidation("If-Match", FormatEntityTag(ifMatch));
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
            request.Headers.TryAddWithoutValidation("If-Match", FormatEntityTag(ifMatch));
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
            request.Headers.TryAddWithoutValidation("If-None-Match", FormatEntityTag(ifNoneMatch));
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

    // ── Entity-tag formatting ───────────────────────────────────────────────────

    /// <summary>
    /// Formats a caller-supplied ETag value as an RFC 7232 §2.3 entity-tag for use in an
    /// <c>If-Match</c>/<c>If-None-Match</c> request header. <see cref="ODataHttpClient"/>'s own
    /// GET-with-ETag methods (<see cref="GetSingleWithETagAsync{T}"/>,
    /// <see cref="GetSingleIfChangedAsync{T}"/>) intentionally strip the surrounding quotes from
    /// the value they return, so a caller round-tripping that value straight back into
    /// <c>ifMatch</c>/<c>ifNoneMatch</c> hands us an unquoted opaque-tag string — RFC 7232
    /// requires <c>entity-tag = [ weak ] DQUOTE *etagc DQUOTE</c>, and a strict server rejects
    /// (or silently never matches) an unquoted value. Quote it here unless it is already a
    /// quoted strong/weak tag or the wildcard <c>*</c>, so both "quoted" and "unquoted" callers
    /// produce a spec-valid header without ever double-quoting.
    /// </summary>
    private static string FormatEntityTag(string etag)
    {
        if (etag.Length == 0 || etag == "*") return etag;

        // Weak validator prefix (RFC 7232 §2.3): W/"..."
        if (etag.StartsWith("W/", StringComparison.Ordinal))
        {
            string rest = etag[2..];
            return "W/" + QuoteIfNeeded(rest);
        }

        return QuoteIfNeeded(etag);
    }

    private static string QuoteIfNeeded(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value : $"\"{value}\"";

    // ── Error handling ──────────────────────────────────────────────────────────

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw await ODataClientException.FromResponseAsync(response, url, ct);
    }
}
