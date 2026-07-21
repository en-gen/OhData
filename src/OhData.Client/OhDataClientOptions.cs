using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client;

/// <summary>
/// Configuration for <see cref="OhDataClient"/>. All properties have sensible defaults that
/// interoperate with an OhData server out of the box: case-insensitive reads bind the server's
/// PascalCase response payloads, and the camelCase query options / request bodies these defaults
/// produce are accepted case-insensitively by the server.
/// </summary>
public sealed class OhDataClientOptions
{
    /// <summary>
    /// JSON serializer options used for all request bodies and response deserialization.
    /// Defaults to camelCase output + case-insensitive reads + ignore-null-on-write. The
    /// case-insensitive reads bind an OhData server's PascalCase responses (its default), and the
    /// server accepts the camelCase request bodies / query options these defaults emit.
    /// </summary>
    /// <remarks>
    /// You can replace this with a custom <see cref="JsonSerializerOptions"/> instance,
    /// but keep in mind:
    /// <list type="bullet">
    ///   <item><c>PropertyNamingPolicy</c> affects how request bodies are serialized —
    ///         set it to match your server's expected casing.</item>
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Controls how 404 Not Found responses are handled for single-entity GET operations.
    /// Default is <see cref="NotFoundBehavior.ReturnNull"/>.
    /// </summary>
    public NotFoundBehavior NotFoundBehavior { get; set; } = NotFoundBehavior.ReturnNull;
}
