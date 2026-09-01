using System;
using System.Collections.Generic;
using System.Text.Json;

namespace OhData.Client;

/// <summary>
/// The OData control information ("annotations") carried by one entity — or by a collection
/// envelope — that <see cref="System.Text.Json.JsonSerializer"/> cannot bind to a CLR property
/// because the member name contains <c>@</c>.
/// </summary>
/// <remarks>
/// <para>
/// The members that matter most are the ones a server attaches to an <em>expanded collection</em>:
/// <c>{Nav}@odata.nextLink</c> (the expansion was server-side paged — what you received is a
/// <em>prefix</em>, not the whole collection) and <c>{Nav}@odata.count</c> (the full size of the
/// related collection, before any nested paging). Without this type both are silently dropped
/// during deserialization, so a truncated nested collection is indistinguishable from a complete
/// one. Instance-level annotations such as <c>@odata.etag</c> and <c>@odata.id</c> are captured
/// on the same pass and are reachable through <see cref="TryGetValue"/>.
/// </para>
/// <para>
/// Only the annotations attached <em>directly</em> to the entity you receive as <c>T</c> (and to
/// the collection envelope) are indexed. Annotations attached to an object nested inside that
/// entity — for example a <c>Chapters@odata.count</c> on an element of an expanded
/// <c>Books</c> collection — are not: pairing them with the nested POCO they belong to needs an
/// index-bearing path key, which is a larger feature than surfacing the entity's own control
/// information.
/// </para>
/// <para>
/// Names are matched with the same comparer the client binds entity properties with — that is,
/// case-insensitively unless <see cref="System.Text.Json.JsonSerializerOptions.PropertyNameCaseInsensitive"/>
/// was turned off on <see cref="OhDataClientOptions.JsonOptions"/> — so a camelCase server's
/// annotations resolve exactly as its entity properties do.
/// </para>
/// <para>
/// The reader classifies a member as an annotation purely by the presence of <c>@</c> and never
/// consults the serialization contract, matching how an OData server classifies control
/// information. A CLR member deliberately renamed to a JSON name containing <c>@</c> is therefore
/// bound to its property <em>and</em> listed here.
/// </para>
/// </remarks>
public sealed class ODataEntityAnnotations
{
    /// <summary>A shared instance carrying no annotations.</summary>
    public static ODataEntityAnnotations Empty { get; } =
        new(new Dictionary<string, JsonElement>(0, StringComparer.Ordinal));

    internal ODataEntityAnnotations(IReadOnlyDictionary<string, JsonElement> values) => Values = values;

    /// <summary>
    /// The raw annotations, keyed by their full wire name (e.g. <c>"Books@odata.nextLink"</c>).
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> is the ceiling here on purpose: beyond <c>nextLink</c> and
    /// <c>count</c> — the two the OData specification pins to a fixed type, and the two with typed
    /// accessors below — the annotation set is open-ended and vocabulary-defined
    /// (<c>@odata.etag</c>, <c>@odata.id</c>, <c>@Org.Example.customTerm</c>, …). The client cannot
    /// know a CLR type for those, so it hands back the parsed value rather than guessing one.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonElement> Values { get; }

    /// <summary><see langword="true"/> when no annotation was present.</summary>
    public bool IsEmpty => Values.Count == 0;

    /// <summary>Looks up one annotation by its full wire name.</summary>
    /// <param name="name">The full annotation name, e.g. <c>"Books@odata.nextLink"</c>.</param>
    /// <param name="value">The parsed annotation value when present.</param>
    /// <returns><see langword="true"/> when the annotation was present.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
    public bool TryGetValue(string name, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Values.TryGetValue(name, out value);
    }

    /// <summary>
    /// The <c>{navigationProperty}@odata.nextLink</c> for an expanded collection — the URL that
    /// serves the rest of it — or <see langword="null"/> when the expansion was not server-side
    /// paged (i.e. what you received is the complete related collection).
    /// </summary>
    /// <param name="navigationProperty">
    /// The navigation property's name as the server spells it on the wire.
    /// </param>
    /// <remarks>
    /// The returned URI may be relative: OData permits either form, and it is resolved against the
    /// request URL. The client exposes the link but does not follow it — see
    /// <see cref="ODataAnnotatedPage{T}"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="navigationProperty"/> is null.</exception>
    public Uri? NextLinkFor(string navigationProperty)
    {
        ArgumentNullException.ThrowIfNull(navigationProperty);
        if (!TryGetTerm(navigationProperty, "nextLink", out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        string? raw = value.GetString();
        return raw is not null && Uri.TryCreate(raw, UriKind.RelativeOrAbsolute, out Uri? uri) ? uri : null;
    }

    /// <summary>
    /// The <c>{navigationProperty}@odata.count</c> for an expanded collection — the size of the
    /// <em>full</em> related collection (OData §11.2.5.5), independent of how many entities this
    /// response actually carried — or <see langword="null"/> when the server did not emit it.
    /// </summary>
    /// <param name="navigationProperty">
    /// The navigation property's name as the server spells it on the wire.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="navigationProperty"/> is null.</exception>
    public long? CountFor(string navigationProperty)
    {
        ArgumentNullException.ThrowIfNull(navigationProperty);
        if (!TryGetTerm(navigationProperty, "count", out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) ? count : null;
    }

    // Both the 4.0 qualified form ("Books@odata.count") and the 4.01 short form ("Books@count") are
    // accepted. OhData servers emit the qualified form (OData-Version: 4.0), but the short form is a
    // SHOULD in 4.01 and this client is not OhData-only.
    private bool TryGetTerm(string target, string term, out JsonElement value) =>
        Values.TryGetValue($"{target}@odata.{term}", out value) ||
        Values.TryGetValue($"{target}@{term}", out value);
}
