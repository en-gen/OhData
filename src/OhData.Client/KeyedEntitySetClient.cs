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
    private readonly ODataHttpClient    _http;
    private readonly OhDataClientOptions _options;
    private readonly string             _entitySetName;
    private readonly string             _formattedKey;

    internal KeyedEntitySetClient(
        ODataHttpClient     http,
        OhDataClientOptions options,
        string              entitySetName,
        string              formattedKey)
    {
        _http          = http;
        _options       = options;
        _entitySetName = entitySetName;
        _formattedKey  = formattedKey;
    }

    /// <summary>
    /// GET <c>/{EntitySet}(key)</c> — returns <see langword="null"/> on 404.
    /// </summary>
    public Task<T?> GetAsync(CancellationToken ct = default)
        => _http.GetSingleAsync<T>(Url, ct);

    /// <summary>
    /// PUT <c>/{EntitySet}(key)</c> with a full entity replacement.
    /// Returns the updated entity as returned by the server, or <see langword="null"/>
    /// when the server returns HTTP 204 No Content (preference <c>return=minimal</c>).
    /// </summary>
    public Task<T?> PutAsync(T entity, CancellationToken ct = default)
        => _http.PutAsync(Url, entity, ct);

    /// <summary>
    /// PATCH <c>/{EntitySet}(key)</c> with a partial update.
    /// <paramref name="patch"/> can be an anonymous object (<c>new { Price = 9.99m }</c>)
    /// or any type whose serialised properties are the fields to update.
    /// Returns the updated entity as returned by the server, or <see langword="null"/>
    /// when the server returns HTTP 204 No Content (preference <c>return=minimal</c>).
    /// </summary>
    public Task<T?> PatchAsync(object patch, CancellationToken ct = default)
        => _http.PatchAsync<T>(Url, patch, ct);

    /// <summary>
    /// DELETE <c>/{EntitySet}(key)</c>. Throws <see cref="ODataClientException"/> on failure
    /// (including 404 when the server is configured with non-idempotent deletes).
    /// </summary>
    public Task DeleteAsync(CancellationToken ct = default)
        => _http.DeleteAsync(Url, ct);

    private string Url => $"{_entitySetName}({_formattedKey})";
}
