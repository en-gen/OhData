using System.Collections.Generic;
using System.Linq;

namespace OhData;

/// <summary>
/// Wraps the result of a <c>GetODataQueryable</c> handler, allowing profiles to return
/// pre-paging metadata (total count, next link) alongside the paged item sequence.
/// </summary>
/// <typeparam name="TModel">The CLR type of the entity.</typeparam>
public sealed class ODataQueryResult<TModel> where TModel : class
{
    /// <summary>The item sequence to materialise. Defaults to an empty queryable.</summary>
    public IQueryable<TModel> Items { get; init; } = Enumerable.Empty<TModel>().AsQueryable();

    /// <summary>
    /// Pre-paging total count. When provided, used as <c>@odata.count</c> in the response
    /// instead of <c>items.Length</c>. Leave <c>null</c> to fall back to item count.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// When provided, emitted as <c>@odata.nextLink</c> in the response envelope.
    /// Takes priority over any framework-computed next link.
    /// </summary>
    public string? NextLink { get; init; }

    /// <summary>
    /// Creates an <see cref="ODataQueryResult{TModel}"/> from a queryable.
    /// </summary>
    public static ODataQueryResult<TModel> FromQueryable(IQueryable<TModel> queryable)
        => new() { Items = queryable };
}
