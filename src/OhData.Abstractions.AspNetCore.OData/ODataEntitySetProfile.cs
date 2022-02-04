using System.Linq.Expressions;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.Abstractions.AspNetCore.OData;

public abstract class ODataEntitySetProfile<TKey, TModel> : EntitySetProfile<TKey, TModel>
    where TModel : class
{
    protected Func<TKey, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? GetById = null;
    protected Func<ODataQueryOptions<TModel>, CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;
    protected Func<ODataQueryOptions<TModel>, CancellationToken, Task<IEnumerable<TModel>>>? GetEnumerable = null;
        
    protected Func<TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? Put = null;
    protected Func<TKey, TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? PutById = null;

    protected Func<TModel, ODataQueryOptions<TModel>, CancellationToken, Task<TModel>>? Post = null;

    protected ODataEntitySetProfile(Expression<Func<TModel, TKey>> getKey) : base(getKey)
    {
    }
}