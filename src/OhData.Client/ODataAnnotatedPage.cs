using System.Collections.Generic;

namespace OhData.Client;

/// <summary>
/// A page of OData collection results in which every entity keeps the OData control information
/// the server attached to it — most importantly <c>{Nav}@odata.nextLink</c> and
/// <c>{Nav}@odata.count</c> on an expanded collection.
/// Returned by <see cref="EntitySetClient{T}.ToAnnotatedPageAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the annotation-preserving counterpart of <see cref="ODataPage{T}"/>. It exists as a
/// separate type — and behind a separate terminal method — because preserving annotations means
/// buffering and re-reading the response body, which every existing read would otherwise pay for a
/// feature most callers never look at. <see cref="ODataPage{T}"/> and every method returning it are
/// untouched.
/// </para>
/// <para>
/// The client <em>exposes</em> a nested <c>nextLink</c>; it does not follow one.
/// <see cref="EntitySetClient{T}.ToAnnotatedAsyncEnumerable"/> follows the collection's own
/// envelope-level <c>@odata.nextLink</c> across pages, exactly as
/// <see cref="EntitySetClient{T}.ToAsyncEnumerable"/> does, but a <em>nested</em> link addresses a
/// different resource with a different element type, so following it is the caller's call to make
/// with the <see cref="System.Uri"/> handed back.
/// </para>
/// </remarks>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class ODataAnnotatedPage<T> where T : class
{
    private IReadOnlyList<T>? _items;

    /// <summary>The entities in this page, each paired with its annotations.</summary>
    public IReadOnlyList<ODataAnnotatedEntity<T>> Entries { get; init; } = [];

    /// <summary>The entities in this page, without their annotations.</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            if (_items is not null) return _items;
            var items = new T[Entries.Count];
            for (int i = 0; i < items.Length; i++) items[i] = Entries[i].Entity;
            return _items = items;
        }
    }

    /// <summary>
    /// The total number of entities matching the query (before <c>$top</c>/<c>$skip</c>),
    /// or <see langword="null"/> when the server did not return an inline count.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// The collection's own <c>@odata.nextLink</c> — the URL of the next page of <em>this</em>
    /// collection — or <see langword="null"/> when there are no more pages.
    /// </summary>
    public string? NextLink { get; init; }

    /// <summary>
    /// Control information carried by the response envelope rather than by an individual entity.
    /// Includes <c>@odata.context</c>, and <c>@odata.count</c>/<c>@odata.nextLink</c> which are also
    /// surfaced typed as <see cref="TotalCount"/>/<see cref="NextLink"/>.
    /// </summary>
    public ODataEntityAnnotations Annotations { get; init; } = ODataEntityAnnotations.Empty;
}
