using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.Abstractions.AspNetCore.OData;

/// <summary>
/// Extends <see cref="EntitySetProfile{TKey,TModel}"/> with OData-aware handler overloads that
/// receive <see cref="ODataQueryOptions{TModel}"/> or <see cref="Delta{TModel}"/> directly,
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
    /// </summary>
    protected Func<ODataQueryOptions<TModel>, CancellationToken, Task<IQueryable<TModel>>>? GetODataQueryable = null;

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

    async Task<IQueryable<object>> IODataEntitySetEndpointSource.InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        return (await GetODataQueryable!(typedOptions, ct)).Cast<object>();
    }
}
