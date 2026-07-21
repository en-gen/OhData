using System.Text.Json.Serialization;

namespace OhData;

/// <summary>
/// Documentation-only shape of the request body accepted by
/// <c>PUT</c>/<c>PATCH /{EntitySet}({key})/{Property}</c> (OData Protocol §11.4.9.1/.2). Never
/// used to actually deserialize a request — the real handler reads and validates the body by
/// hand (see the "POST/PUT/PATCH deserialize the request body by hand" note in CLAUDE.md). It
/// exists purely so OpenAPI document generators can render a body editor for these routes via
/// <see cref="OhDataRequestBodyMetadata"/> instead of showing none at all.
/// </summary>
/// <typeparam name="T">The property's CLR type.</typeparam>
public sealed class ODataPropertyWriteRequest<T>
{
    /// <summary>The property's new value.</summary>
    [JsonPropertyName("value")]
    public T? Value { get; set; }
}

/// <summary>
/// Documentation-only shape of the request body accepted by
/// <c>POST</c>/<c>PUT /{EntitySet}({key})/{Nav}/$ref</c> (OData Protocol §11.4.6.2/.3). Never
/// used to actually deserialize a request — see <see cref="ODataPropertyWriteRequest{T}"/> for
/// why.
/// </summary>
public sealed class ODataRefWriteRequest
{
    /// <summary>The canonical URL of the entity to link.</summary>
    [JsonPropertyName("@odata.id")]
    public string ODataId { get; set; } = "";
}
