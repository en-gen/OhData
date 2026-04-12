using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.Abstractions.AspNetCore.OData;

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
    /// PATCH handler that receives an OData <see cref="Delta{TModel}"/> representing only the
    /// properties present in the request body. Preferred over the base <c>Patch</c> delegate
    /// when true partial update semantics are needed.
    /// </summary>
    protected Func<TKey, Delta<TModel>, CancellationToken, Task<TModel?>>? PatchDelta = null;

    protected ODataEntitySetProfile(Expression<Func<TModel, TKey>> getKey) : base(getKey)
    {
    }

    // IODataEntitySetEndpointSource implementation
    bool IODataEntitySetEndpointSource.HasGetODataQueryable => GetODataQueryable is not null;
    bool IODataEntitySetEndpointSource.HasPatchDelta => PatchDelta is not null;

    async Task<IQueryable<object>> IODataEntitySetEndpointSource.InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        return (await GetODataQueryable!(typedOptions, ct)).Cast<object>();
    }

    async Task<object?> IODataEntitySetEndpointSource.InvokePatchDeltaAsync(object key, Delta delta, CancellationToken ct)
    {
        var typedDelta = (Delta<TModel>)delta;
        return (object?)await PatchDelta!((TKey)key, typedDelta, ct);
    }
}
