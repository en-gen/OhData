using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.Abstractions.AspNetCore.OData;

public abstract class ODataEntitySetProfile<TKey, TModel> : EntitySetProfile<TKey, TModel>, IODataEntitySetEndpointSource
    where TModel : class
{
    protected new Func<TKey, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? GetById = null;
    protected new Func<ODataQueryOptions<TModel>, CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;
    protected Func<ODataQueryOptions<TModel>, CancellationToken, Task<IEnumerable<TModel>>>? GetEnumerable = null;

    protected new Func<TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? Put = null;
    protected new Func<TKey, TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? PutById = null;

    protected new Func<TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? Post = null;

    protected ODataEntitySetProfile(Expression<Func<TModel, TKey>> getKey) : base(getKey)
    {
    }

    // IODataEntitySetEndpointSource implementation
    bool IODataEntitySetEndpointSource.HasGetODataQueryable  => GetQueryable is not null;
    bool IODataEntitySetEndpointSource.HasGetODataEnumerable => GetEnumerable is not null;

    async Task<IQueryable<object>> IODataEntitySetEndpointSource.InvokeGetODataQueryableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        return (await GetQueryable!(typedOptions, ct)).Cast<object>();
    }

    async Task<IEnumerable<object>> IODataEntitySetEndpointSource.InvokeGetODataEnumerableAsync(ODataQueryOptions options, CancellationToken ct)
    {
        var typedOptions = (ODataQueryOptions<TModel>)options;
        return (await GetEnumerable!(typedOptions, ct)).Cast<object>();
    }
}