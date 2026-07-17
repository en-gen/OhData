using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OhData.Client.Internal;

namespace OhData.Client;

/// <summary>
/// Fluent builder for single-entity operations against a known key.
/// Obtained via <see cref="EntitySetClient{T}.Key"/>.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class KeyedEntitySetClient<T> where T : class
{
    private readonly ODataHttpClient _http;
    private readonly string _url;

    internal KeyedEntitySetClient(
        ODataHttpClient http,
        string entitySetName,
        string formattedKey,
        string? select = null,
        string? expand = null)
    {
        _http = http;
        _url = BuildUrl(entitySetName, formattedKey, select, expand);
    }

    /// <summary>
    /// GET <c>/{EntitySet}(key)</c> — returns <see langword="null"/> on 404.
    /// </summary>
    public Task<T?> GetAsync(CancellationToken ct = default)
        => _http.GetSingleAsync<T>(_url, ct);

    /// <summary>
    /// GET <c>/{EntitySet}(key)</c> with ETag — returns the entity and the server's current ETag value.
    /// Pass the ETag to <see cref="PutAsync(T, string?, bool, CancellationToken)"/> for optimistic concurrency.
    /// </summary>
    public Task<(T? Entity, string? ETag)> GetWithETagAsync(CancellationToken ct = default)
        => _http.GetSingleWithETagAsync<T>(_url, ct);

    /// <summary>
    /// GET <c>/{EntitySet}(key)</c> with conditional retrieval via <c>If-None-Match</c>
    /// (RFC 7232 §3.2 / OData §8.2.5).
    /// </summary>
    /// <param name="ifNoneMatch">
    /// The ETag previously observed for this entity (e.g. from <see cref="GetWithETagAsync"/>
    /// or a prior call to this method). When supplied, the server responds with HTTP 304 and
    /// no body if the entity is unchanged. When <see langword="null"/>, no conditional header
    /// is sent and the call behaves like <see cref="GetWithETagAsync"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple where <c>NotModified</c> is <see langword="true"/> when the server confirmed the
    /// cached copy is still current (HTTP 304) — in that case <c>Entity</c> is <see langword="null"/>
    /// and <c>ETag</c> echoes the server's current value. When <c>NotModified</c> is
    /// <see langword="false"/>, <c>Entity</c> holds the fresh representation (or
    /// <see langword="null"/> if the entity does not exist, per <see cref="NotFoundBehavior"/>)
    /// and <c>ETag</c> is that entity's current ETag.
    /// </returns>
    /// <exception cref="ODataClientException">
    /// Thrown with status 404 when the entity is not found and
    /// <see cref="OhDataClientOptions.NotFoundBehavior"/> is <see cref="NotFoundBehavior.Throw"/>.
    /// </exception>
    public Task<(T? Entity, string? ETag, bool NotModified)> GetIfChangedAsync(
        string? ifNoneMatch = null, CancellationToken ct = default)
        => _http.GetSingleIfChangedAsync<T>(_url, ifNoneMatch, ct);

    /// <summary>
    /// PUT <c>/{EntitySet}(key)</c> with a full entity replacement.
    /// Optionally supply an If-Match ETag for optimistic concurrency or set
    /// <paramref name="preferMinimal"/> to request a 204 No Content response.
    /// Returns the updated entity as returned by the server, or <see langword="null"/>
    /// when the server returns HTTP 204 No Content.
    /// Throws <see cref="ODataClientException"/> with status 412 if <paramref name="ifMatch"/>
    /// is supplied and does not match the server's current ETag.
    /// </summary>
    public Task<T?> PutAsync(T entity, string? ifMatch = null, bool preferMinimal = false, CancellationToken ct = default)
        => _http.PutAsync(_url, entity, ifMatch, preferMinimal, ct);

    /// <summary>
    /// PATCH <c>/{EntitySet}(key)</c> with a partial update.
    /// <paramref name="patch"/> can be an anonymous object (<c>new { Price = 9.99m }</c>)
    /// or any type whose serialised properties are the fields to update.
    /// Optionally supply an If-Match ETag for optimistic concurrency or set
    /// <paramref name="preferMinimal"/> to request a 204 No Content response.
    /// Returns the updated entity as returned by the server, or <see langword="null"/>
    /// when the server returns HTTP 204 No Content.
    /// </summary>
    public Task<T?> PatchAsync(object patch, string? ifMatch = null, bool preferMinimal = false, CancellationToken ct = default)
        => _http.PatchAsync<T>(_url, patch, ifMatch, preferMinimal, ct);

    /// <summary>
    /// DELETE <c>/{EntitySet}(key)</c>. Optionally supply an If-Match ETag for optimistic concurrency.
    /// Throws <see cref="ODataClientException"/> on failure (including 404 when the server is
    /// configured with non-idempotent deletes).
    /// </summary>
    public Task DeleteAsync(string? ifMatch = null, CancellationToken ct = default)
        => _http.DeleteAsync(_url, ifMatch, ct);

    internal string BuildEntityUrl() => _url;

    private static string BuildUrl(string entitySetName, string formattedKey, string? select, string? expand)
    {
        string baseUrl = $"{entitySetName}({formattedKey})";
        if (select is null && expand is null) return baseUrl;

        var parts = new List<string>(2);
        if (select is not null) parts.Add($"$select={Uri.EscapeDataString(select)}");
        if (expand is not null) parts.Add($"$expand={Uri.EscapeDataString(expand)}");
        return $"{baseUrl}?{string.Join('&', parts)}";
    }
}
