using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client;

/// <summary>
/// Configuration for <see cref="OhDataClient"/>. All properties have sensible defaults that
/// interoperate with an OhData server out of the box: request bodies and query-option property
/// names use the CLR/PascalCase names (matching the server's default PascalCase EDM and
/// responses), and case-insensitive reads bind the server's response payloads leniently.
/// </summary>
public sealed class OhDataClientOptions
{
    /// <summary>
    /// JSON serializer options used for all request bodies and response deserialization.
    /// Defaults to PascalCase output (<c>PropertyNamingPolicy = null</c>, i.e. the CLR property
    /// names) + case-insensitive reads + ignore-null-on-write. This matches an OhData server's
    /// PascalCase default for both its EDM/query-option surface and its response payloads; the
    /// case-insensitive reads additionally bind responses leniently regardless of server casing.
    /// </summary>
    /// <remarks>
    /// You can replace this with a custom <see cref="JsonSerializerOptions"/> instance,
    /// but keep in mind:
    /// <list type="bullet">
    ///   <item><c>PropertyNamingPolicy</c> affects how request bodies <em>and</em>
    ///         <c>$filter</c>/<c>$select</c>/<c>$expand</c>/<c>$orderby</c> property names are
    ///         emitted — leave it <c>null</c> for PascalCase (the default), or set it to
    ///         <see cref="JsonNamingPolicy.CamelCase"/> to emit camelCase against a server
    ///         configured for camelCase.</item>
    ///   <item><c>PropertyNameCaseInsensitive</c> affects entity deserialization.
    ///         It does <em>not</em> affect the internal OData envelope fields
    ///         (<c>@odata.count</c>, <c>value</c>) which use <c>[JsonPropertyName]</c>
    ///         attributes and are always matched by name.</item>
    /// </list>
    /// This property is read once at construction time. Mutating the options object
    /// after an <see cref="OhDataClient"/> is created has undefined behaviour.
    /// </remarks>
    public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Controls how 404 Not Found responses are handled for single-entity GET operations.
    /// Default is <see cref="NotFoundBehavior.ReturnNull"/>.
    /// </summary>
    public NotFoundBehavior NotFoundBehavior { get; set; } = NotFoundBehavior.ReturnNull;

    /// <summary>
    /// Whether the <c>@odata.nextLink</c> walker may follow a link whose origin
    /// (scheme + host + port) differs from the <see cref="HttpClient.BaseAddress"/> it was
    /// configured with. Default <see langword="false"/>: a cross-origin link fails the read with
    /// <see cref="InvalidOperationException"/> instead of being fetched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A nextLink is a URL named in a response <em>body</em>, and the walker builds a fresh request
    /// for it — so the <see cref="HttpClient"/>'s <see cref="HttpClient.DefaultRequestHeaders"/>,
    /// <c>Authorization</c> among them, are attached to whatever host that body names. This is not a
    /// redirect, so <see cref="HttpClientHandler"/>'s cross-origin <c>Authorization</c> stripping
    /// never runs. A response-body injection (compromised server, caching proxy, MITM on a plaintext
    /// hop) therefore exfiltrates the bearer token to a host of the attacker's choosing.
    /// </para>
    /// <para>
    /// Defaulting to same-origin rather than to header-stripping is deliberate. The credential at
    /// risk is not only <c>Authorization</c>: a default-header API key
    /// (<c>X-Api-Key</c>, <c>Ocp-Apim-Subscription-Key</c>, …) leaks identically, and dropping one
    /// header by name leaves the rest. Refusing the hop closes all of them at once, and it makes the
    /// unusual case explicit rather than silently degrading into an unauthenticated 401 from a host
    /// the caller never named. Setting this to <see langword="true"/> is an explicit statement that
    /// the service's paging links are trusted to name other origins.
    /// </para>
    /// <para>
    /// A <em>relative</em> nextLink is unaffected: it resolves against
    /// <see cref="HttpClient.BaseAddress"/>, which makes it same-origin by construction.
    /// </para>
    /// </remarks>
    public bool FollowCrossOriginNextLinks { get; set; }

    /// <summary>
    /// The maximum number of <c>@odata.nextLink</c> hops a single enumeration may follow before it
    /// fails with <see cref="InvalidOperationException"/>. Default <c>10_000</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ToAsyncEnumerable</c>/<c>ToListAsync</c> follow the link the server puts in each page
    /// until there isn't one. A server that returns the same link forever — broken, or hostile —
    /// makes that loop unbounded; <c>ToListAsync</c> additionally accumulates every page until the
    /// process runs out of memory. The cap is a termination guarantee, not a paging policy.
    /// </para>
    /// <para>
    /// The default is set so that no legitimate paging run reaches it: at a typical server page
    /// size of 100 it allows a million entities in one enumeration, and OData's own guidance is to
    /// page in far larger chunks than that. Raise it for a genuinely larger sweep, or set it to
    /// <see cref="int.MaxValue"/> to make it effectively unlimited.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxNextLinkHops
    {
        get => _maxNextLinkHops;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "MaxNextLinkHops must be at least 1.");
            }

            _maxNextLinkHops = value;
        }
    }

    private int _maxNextLinkHops = 10_000;
}
