using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Expressions;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Applies a request's <c>$filter</c> and <c>$orderby</c> — parsed against the <b>model</b> — to a
/// queryable over the <b>entity</b>.
/// </summary>
/// <remarks>
/// <para>
/// The clauses are bound by <c>Microsoft.AspNetCore.OData</c>'s own
/// <c>FilterBinder</c>/<c>OrderByBinder</c>, exactly as the core binds a nested expand, and only
/// then rewritten. That ordering is the point: every operator, canonical function and lambda the
/// framework supports is bound by the framework's binder, so this package can neither miss one nor
/// interpret one differently.
/// </para>
/// <para>
/// <see cref="HandleNullPropagationOption.False"/> is what a database-side query wants: SQL's own
/// three-valued logic already yields null for a null reference, and the guarded form
/// <c>Microsoft.AspNetCore.OData</c> emits for in-memory evaluation would only add conditionals the
/// provider must then translate. It is the same value the core uses for its pushdown binds.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The persistence type the query runs against.</typeparam>
/// <typeparam name="TModel">The type on the wire and in <c>$metadata</c>.</typeparam>
internal sealed class MappedQueryComposer<TEntity, TModel>
    where TEntity : class
    where TModel : class
{
    // Both binders are stateless -- every per-clause value flows through QueryBinderContext -- so one
    // instance each is shared, as the core does for the same two types.
    private static readonly FilterBinder s_filterBinder = new();
    private static readonly OrderByBinder s_orderByBinder = new();

    private static readonly Dictionary<string, MethodInfo> s_ordering =
        new[] { nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
                nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending) }
            .ToDictionary(
                name => name,
                name => typeof(Queryable).GetMethods()
                    .Single(m => m.Name == name && m.GetParameters().Length == 2),
                StringComparer.Ordinal);

    private static readonly ODataQuerySettings s_binderSettings = new()
    {
        HandleNullPropagation = HandleNullPropagationOption.False,
    };

    private readonly ModelMap _map;
    private readonly ModelMapRegistry _registry;
    private readonly IEdmModel _edmModel;

    /// <summary>Creates a composer for one map.</summary>
    public MappedQueryComposer(ModelMap map, ModelMapRegistry registry, IEdmModel edmModel)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _edmModel = edmModel ?? throw new ArgumentNullException(nameof(edmModel));
    }

    /// <summary>Rewrites a parsed <c>$filter</c> into an entity-side predicate.</summary>
    public Expression<Func<TEntity, bool>> RewritePredicate(FilterClause clause)
    {
        if (clause is null) throw new ArgumentNullException(nameof(clause));

        var ctx = new QueryBinderContext(_edmModel, s_binderSettings, typeof(TModel));
        var modelLambda = (LambdaExpression)s_filterBinder.BindFilter(clause, ctx);
        return (Expression<Func<TEntity, bool>>)NewRewriter().RewriteLambda(modelLambda);
    }

    /// <summary>Rewrites a parsed <c>$orderby</c> into entity-side sort keys, outermost first.</summary>
    public IReadOnlyList<(LambdaExpression Key, bool Descending)> RewriteOrderBy(OrderByClause clause)
    {
        if (clause is null) throw new ArgumentNullException(nameof(clause));

        var ctx = new QueryBinderContext(_edmModel, s_binderSettings, typeof(TModel));
        OrderByBinderResult? result = s_orderByBinder.BindOrderBy(clause, ctx);

        var keys = new List<(LambdaExpression, bool)>();
        for (OrderByBinderResult? cur = result; cur is not null; cur = cur.ThenBy)
        {
            var modelKey = (LambdaExpression)cur.OrderByExpression;
            keys.Add((NewRewriter().RewriteLambda(modelKey),
                      cur.Direction == OrderByDirection.Descending));
        }

        return keys;
    }

    /// <summary>Applies <c>$filter</c>, if the request carried one.</summary>
    public IQueryable<TEntity> ApplyFilter(IQueryable<TEntity> source, FilterClause? clause) =>
        clause is null ? source : source.Where(RewritePredicate(clause));

    /// <summary>Applies <c>$orderby</c>, if the request carried one.</summary>
    public IQueryable<TEntity> ApplyOrderBy(IQueryable<TEntity> source, OrderByClause? clause) =>
        clause is null ? source : ApplyKeys(source, RewriteOrderBy(clause), alreadyOrdered: false);

    /// <summary>
    /// Appends a tie-breaking sort key.
    /// </summary>
    /// <remarks>
    /// Paging over an unstable order returns rows in an arbitrary sequence per page, so a client
    /// walking <c>@odata.nextLink</c> can see a row twice and miss another. The core injects the same
    /// stabiliser on the path where it owns skip/take, and declines to on Priority-1 only because the
    /// profile owns paging there. This profile owns paging, so it owes the stabiliser.
    /// </remarks>
    public IQueryable<TEntity> Stabilize(IQueryable<TEntity> source, LambdaExpression key, bool ordered) =>
        ApplyKeys(source, new[] { (key, false) }, alreadyOrdered: ordered);

    private ModelToEntityRewriter NewRewriter() => new(_map, _registry);

    private static IQueryable<TEntity> ApplyKeys(
        IQueryable<TEntity> source,
        IReadOnlyList<(LambdaExpression Key, bool Descending)> keys,
        bool alreadyOrdered)
    {
        Expression expression = source.Expression;
        bool ordered = alreadyOrdered;

        foreach ((LambdaExpression key, bool descending) in keys)
        {
            string name = (ordered, descending) switch
            {
                (false, false) => nameof(Queryable.OrderBy),
                (false, true) => nameof(Queryable.OrderByDescending),
                (true, false) => nameof(Queryable.ThenBy),
                (true, true) => nameof(Queryable.ThenByDescending),
            };

            MethodInfo method = s_ordering[name].MakeGenericMethod(typeof(TEntity), key.ReturnType);

            expression = Expression.Call(method, expression, Expression.Quote(key));
            ordered = true;
        }

        return source.Provider.CreateQuery<TEntity>(expression);
    }
}
