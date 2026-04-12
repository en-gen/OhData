using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhData.Client;

/// <summary>
/// Configuration for <see cref="OhDataClient"/>. All properties have sensible defaults
/// that match the OhData server's out-of-box behaviour (camelCase JSON, case-insensitive reads).
/// </summary>
public sealed class OhDataClientOptions
{
    /// <summary>
    /// JSON serializer options used for all request bodies and response deserialization.
    /// Defaults to camelCase output + case-insensitive reads + ignore-null-on-write,
    /// matching OhData server defaults.
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
}
