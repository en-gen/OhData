using System;
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
    // Racy-but-idempotent cache. `volatile` is what makes it deterministic under concurrent readers:
    // it pairs a release store with an acquire load, so a reader that observes the reference is
    // guaranteed to observe the fully populated array behind it. Without it the element writes below
    // may be reordered after the reference publication, and a second thread can legally read a
    // partially filled array. Two threads racing may still each build an array — harmless, since both
    // are equal and every caller gets a complete one.
    private volatile IReadOnlyList<T>? _items;

    /// <summary>The entities in this page, each paired with its annotations.</summary>
    public IReadOnlyList<ODataAnnotatedEntity<T>> Entries { get; init; } = [];

    /// <summary>The entities in this page, without their annotations.</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            IReadOnlyList<T>? cached = _items;
            if (cached is not null) return cached;
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
    /// <remarks>
    /// <para>
    /// A <see cref="Uri"/>, not a <see cref="string"/>. Every link in the annotation surface is a
    /// <see cref="Uri"/> — this property, <see cref="ODataEntityAnnotations.NextLinkFor(string)"/> and
    /// <see cref="ODataAnnotatedEntity{T}.NextLinkFor"/> — so one concept has one representation.
    /// <see cref="ODataPage{T}.NextLink"/> remains a <see cref="string"/>: it is pre-existing public
    /// API and changing it would break callers for no benefit. That is the one seam, and it is a
    /// compile error rather than a silent difference when you migrate
    /// <see cref="EntitySetClient{T}.ToPageAsync"/> to
    /// <see cref="EntitySetClient{T}.ToAnnotatedPageAsync"/>.
    /// </para>
    /// <para>
    /// The URI may be relative: OData permits either form, and it is resolved against the request URL.
    /// </para>
    /// </remarks>
    public Uri? NextLink { get; init; }

    /// <summary>
    /// Control information carried by the response envelope rather than by an individual entity.
    /// Includes <c>@odata.context</c>, and <c>@odata.count</c>/<c>@odata.nextLink</c> which are also
    /// surfaced typed as <see cref="TotalCount"/>/<see cref="NextLink"/>.
    /// </summary>
    public ODataEntityAnnotations Annotations { get; init; } = ODataEntityAnnotations.Empty;
}
