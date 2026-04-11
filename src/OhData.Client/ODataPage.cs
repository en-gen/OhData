using System.Collections.Generic;

namespace OhData.Client;

/// <summary>
/// Represents a page of OData collection results including an optional inline count.
/// Returned by <see cref="EntitySetClient{T}.ToPageAsync"/> when <c>$count=true</c>
/// is requested.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class ODataPage<T>
{
    /// <summary>The entities in this page of results.</summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// The total number of entities matching the query (before <c>$top</c>/<c>$skip</c>),
    /// or <see langword="null"/> when the server did not return an inline count.
    /// </summary>
    public long? TotalCount { get; init; }
}
