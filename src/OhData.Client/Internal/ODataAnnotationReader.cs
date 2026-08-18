using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OhData.Client.Internal;

/// <summary>
/// Extracts OData control information from a response body that has already been deserialized into
/// its typed envelope.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json cannot surface these members. They are <c>@</c>-bearing siblings of the
/// caller's own properties <em>inside</em> the caller's POCO, so there is nothing on the envelope a
/// <c>[JsonExtensionData]</c> bag could hang off — the client owns no member of <c>T</c>. Nor can
/// the drop be observed: <c>UnmappedMemberHandling</c> defaults to <c>Skip</c>, and raising it to
/// <c>Disallow</c> would turn every annotation into a deserialization failure rather than a value.
/// So the annotations are recovered by re-reading the same bytes as a <see cref="JsonDocument"/>.
/// </para>
/// <para>
/// Two passes over one buffer is deliberate. Entity binding stays on the <em>existing</em>
/// code path — the same <c>ODataCollectionResponse&lt;T&gt;</c> type, the same
/// <see cref="JsonSerializerOptions"/> — so an annotated read cannot bind an entity differently
/// from a plain one; this pass only walks names and copies the few values it recognizes. Index
/// alignment with the deserialized <c>value</c> list holds by construction: both come from the same
/// bytes, and System.Text.Json preserves array order.
/// </para>
/// <para>
/// Annotation values are cloned individually rather than by cloning the document root. Control
/// information is small by nature (a URL, a number, an entity tag), and most entities carry none —
/// so per-value cloning keeps retention proportional to what the caller actually holds, instead of
/// rooting a copy of the whole payload for the lifetime of a single count.
/// </para>
/// </remarks>
internal static class ODataAnnotationReader
{
    /// <summary>
    /// Reads the envelope's own annotations and one <see cref="ODataEntityAnnotations"/> per element
    /// of <c>value</c>, in wire order. Returns empties for a body that is not a collection envelope.
    /// </summary>
    internal static (ODataEntityAnnotations Envelope, IReadOnlyList<ODataEntityAnnotations> Items)
        ReadCollection(ReadOnlyMemory<byte> utf8Body, StringComparer comparer)
    {
        if (utf8Body.IsEmpty) return (ODataEntityAnnotations.Empty, []);

        using JsonDocument doc = JsonDocument.Parse(utf8Body);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (ODataEntityAnnotations.Empty, []);

        ODataEntityAnnotations envelope = ReadObject(root, comparer);

        if (!root.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return (envelope, []);

        var items = new ODataEntityAnnotations[value.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in value.EnumerateArray())
            items[i++] = ReadObject(element, comparer);

        return (envelope, items);
    }

    /// <summary>Reads the annotations of a single-entity response body.</summary>
    internal static ODataEntityAnnotations ReadSingle(ReadOnlyMemory<byte> utf8Body, StringComparer comparer)
    {
        if (utf8Body.IsEmpty) return ODataEntityAnnotations.Empty;

        using JsonDocument doc = JsonDocument.Parse(utf8Body);
        return ReadObject(doc.RootElement, comparer);
    }

    // A member name containing '@' at any position is control information — the OData JSON format's
    // leading (§4.5) and embedded (§18) annotation forms both carry one, and an odataIdentifier
    // cannot, so the character alone classifies. This is the same rule the OhData server applies to
    // an unmatched key on the write path.
    private static ODataEntityAnnotations ReadObject(JsonElement obj, StringComparer comparer)
    {
        if (obj.ValueKind != JsonValueKind.Object) return ODataEntityAnnotations.Empty;

        Dictionary<string, JsonElement>? annotations = null;
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!property.Name.Contains('@')) continue;
            annotations ??= new Dictionary<string, JsonElement>(comparer);
            annotations[property.Name] = property.Value.Clone();
        }

        return annotations is null ? ODataEntityAnnotations.Empty : new ODataEntityAnnotations(annotations);
    }
}
