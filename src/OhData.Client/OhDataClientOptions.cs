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
}
