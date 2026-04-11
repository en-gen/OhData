using System;
using System.Net.Http;
using System.Threading.Tasks;
using OhData.Client.Internal;

namespace OhData.Client;

/// <summary>
/// Entry point for querying and mutating an OhData (or any OData 4.0) service.
/// </summary>
/// <example>
/// <code>
/// // Simplest setup
/// var client = new OhDataClient("https://api.example.com/v1");
///
/// // With an injected HttpClient (IHttpClientFactory / test double)
/// var client = new OhDataClient(httpClient);
///
/// // Query
/// var products = await client.For&lt;Product&gt;()
///     .Filter(x => x.Price > 10)
///     .OrderBy(x => x.Name)
///     .Top(20)
///     .ToListAsync();
///
/// // Mutate
/// var created = await client.For&lt;Product&gt;().InsertAsync(newProduct);
/// await client.For&lt;Product&gt;().Key(42).PatchAsync(new { Price = 9.99m });
/// await client.For&lt;Product&gt;().Key(42).DeleteAsync();
/// </code>
/// </example>
public sealed class OhDataClient : IDisposable
{
    private readonly ODataHttpClient    _http;
    private readonly OhDataClientOptions _options;
    private readonly HttpClient         _httpClient;
    private readonly bool               _ownsHttpClient;

    /// <summary>
    /// Creates a client with an internally managed <see cref="HttpClient"/>.
    /// The client is disposed when this instance is disposed.
    /// </summary>
    /// <param name="baseAddress">
    /// Base URL of the OData service, e.g. <c>https://api.example.com/v1</c>.
    /// A trailing slash is added automatically if absent.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="baseAddress"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="baseAddress"/> is empty.</exception>
    public OhDataClient(string baseAddress, OhDataClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (baseAddress.Length == 0)
            throw new ArgumentException("Base address must not be empty.", nameof(baseAddress));
        _options        = options ?? new OhDataClientOptions();
        _httpClient     = new HttpClient { BaseAddress = new Uri(baseAddress.TrimEnd('/') + '/') };
        _http           = new ODataHttpClient(_httpClient, _options);
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a client that wraps a caller-supplied <see cref="HttpClient"/>.
    /// The caller retains ownership of <paramref name="httpClient"/>; it is not
    /// disposed when this instance is disposed. Useful with
    /// <c>IHttpClientFactory</c> or test doubles.
    /// </summary>
    public OhDataClient(HttpClient httpClient, OhDataClientOptions? options = null)
    {
        _options        = options ?? new OhDataClientOptions();
        _httpClient     = httpClient;
        _http           = new ODataHttpClient(httpClient, _options);
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Returns a fluent builder for the entity set that corresponds to
    /// <typeparamref name="T"/> (resolved via <see cref="ODataEntitySetAttribute"/>
    /// or simple pluralisation of the type name).
    /// </summary>
    public EntitySetClient<T> For<T>() where T : class
        => new(_http, _options, EntitySetNameConvention.Resolve(typeof(T)));

    /// <summary>
    /// Returns a fluent builder for the named entity set.
    /// Use this overload when the conventional name differs from the server's
    /// configured <c>EntitySetName</c>.
    /// </summary>
    public EntitySetClient<T> For<T>(string entitySetName) where T : class
        => new(_http, _options, entitySetName);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
