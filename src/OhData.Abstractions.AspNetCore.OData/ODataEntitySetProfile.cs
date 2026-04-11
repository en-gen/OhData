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
    protected new Func<ODataQueryOptions<TModel>, CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;

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
    bool IODataEntitySetEndpointSource.HasGetODataQueryable  => GetQueryable is not null;
    bool IODataEntitySetEndpointSource.HasPatchDelta => PatchDelta is not null;

    async Task<IQueryable<object>> IODataEntitySetEndpointSource.InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        return (await GetQueryable!(typedOptions, ct)).Cast<object>();
    }

    async Task<object?> IODataEntitySetEndpointSource.InvokePatchDeltaAsync(object key, Delta delta, CancellationToken ct)
    {
        var typedDelta = (Delta<TModel>)delta;
        return (object?)await PatchDelta!((TKey)key, typedDelta, ct);
    }
}
