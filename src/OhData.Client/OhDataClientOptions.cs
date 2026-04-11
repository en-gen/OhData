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
    /// Defaults to camelCase output + case-insensitive reads, matching OhData server defaults.
    /// </summary>
    public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };
}
