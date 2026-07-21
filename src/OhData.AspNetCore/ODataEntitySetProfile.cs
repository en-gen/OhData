using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;

namespace OhData;

/// <summary>
/// Extends <see cref="EntitySetProfile{TKey,TModel}"/> with OData-aware handler overloads that
/// receive <see cref="ODataQueryOptions{TModel}"/> or <see cref="Microsoft.AspNetCore.OData.Deltas.Delta{TModel}"/> directly,
/// enabling full OData pushdown to the data source (e.g. EF Core) and true partial-update semantics.
/// </summary>
/// <typeparam name="TKey">The CLR type of the entity's primary key.</typeparam>
/// <typeparam name="TModel">The CLR type of the entity.</typeparam>
public abstract class ODataEntitySetProfile<TKey, TModel> : EntitySetProfile<TKey, TModel>, IODataEntitySetEndpointSource
    where TModel : class
{
    /// <summary>
    /// Collection GET handler that receives <see cref="ODataQueryOptions{TModel}"/> directly,
    /// allowing the profile to apply (or selectively skip) OData query options itself.
    /// This is the preferred handler when the underlying data source can push query options
    /// all the way to the database (e.g. EF Core with full OData pushdown).
    /// Use the base-class <c>GetQueryable</c> when you only need the standard
    /// filter/orderby/skip/top pipeline that the framework applies automatically.
    /// <para>
    /// Returns an <see cref="ODataQueryResult{TModel}"/> which may carry a pre-paging
    /// <see cref="ODataQueryResult{TModel}.TotalCount"/> and/or a
    /// <see cref="ODataQueryResult{TModel}.NextLink"/>. An <see cref="IQueryable{TModel}"/>
    /// is implicitly convertible to <see cref="ODataQueryResult{TModel}"/> for backward
    /// compatibility.
    /// </para>
    /// </summary>
    protected Func<ODataQueryOptions<TModel>, CancellationToken, Task<ODataQueryResult<TModel>>>? GetODataQueryable = null;

    /// <summary>
    /// Initialises the profile. Pass a key-selector expression that identifies the entity's
    /// primary key property, e.g. <c>x => x.Id</c>.
    /// </summary>
    /// <param name="getKey">
    /// Expression that selects the key property from <typeparamref name="TModel"/>.
    /// </param>
    protected ODataEntitySetProfile(Expression<Func<TModel, TKey>> getKey) : base(getKey)
    {
    }

    // IODataEntitySetEndpointSource implementation
    bool IODataEntitySetEndpointSource.HasGetODataQueryable => GetODataQueryable is not null;

    async Task<ODataQueryResult<object>> IODataEntitySetEndpointSource.InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        ODataQueryResult<TModel> result = await GetODataQueryable!(typedOptions, ct);
        return new ODataQueryResult<object>
        {
            Items = result.Items.Cast<object>().AsQueryable(),
            TotalCount = result.TotalCount,
            NextLink = result.NextLink,
        };
    }
}
