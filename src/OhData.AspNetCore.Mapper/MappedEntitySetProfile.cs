using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Query;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// An entity set whose wire model differs from the entity behind it.
/// </summary>
/// <remarks>
/// <para>
/// The adopter declares <b>correspondences</b> — where each model member comes from — and this
/// profile composes the query. It never asks for a projection: a hand-written
/// <c>Select(o =&gt; new Dto { … })</c> has no request context, so it must bind every navigation it
/// might ever need on every request, and a member the provider cannot translate fails at the row
/// rather than at startup.
/// </para>
/// <para>
/// <c>$filter</c> and <c>$orderby</c> are parsed against the model, bound by
/// <c>Microsoft.AspNetCore.OData</c>'s own binders, then substituted into entity terms and applied to
/// the entity queryable — so the predicate reaches the database and only the rows of one page are
/// materialised. Nothing is filtered, sorted or paged in memory.
/// </para>
/// <para>
/// This is a Priority-1 profile, so it owns query application. What it does <i>not</i> own is the
/// rest of the response: <c>$select</c>, <c>$expand</c>, ETags and the envelope all come from the
/// core's shared collection pipeline, exactly as they do for any other profile.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The CLR type of the key.</typeparam>
/// <typeparam name="TModel">The type on the wire and in <c>$metadata</c>.</typeparam>
/// <typeparam name="TEntity">The persistence type the query runs against.</typeparam>
public abstract class MappedEntitySetProfile<TKey, TModel, TEntity> : ODataEntitySetProfile<TKey, TModel>
    where TKey : notnull
    where TModel : class
    where TEntity : class
{
    /// <summary>
    /// The page size this profile serves when the client asks for no smaller one.
    /// </summary>
    /// <remarks>
    /// A Priority-1 profile owns paging, so it also owns the ceiling: the framework's own
    /// <c>MaxTop</c> cap is skipped once the profile emits its own <c>@odata.nextLink</c>, and it
    /// could not be read here anyway (the resolved value is computed on the startup instance, while
    /// handlers run on a request-scoped one). 1000 is <c>EntitySetDefaults.MaxTop</c>'s own default,
    /// so an adopter who changes neither sees the same ceiling either way.
    /// </remarks>
    protected int MappedPageSize { get; init; } = 1000;

    private Func<IQueryable<TEntity>>? _entityQuery;
    private ModelMap? _map;
    private ModelMapRegistry? _registry;
    private LambdaExpression? _entityKey;
    private LambdaExpression? _projection;

    /// <summary>Initialises the profile with the model's key selector.</summary>
    /// <param name="getKey">Selects the key from the model, e.g. <c>d =&gt; d.Id</c>.</param>
    protected MappedEntitySetProfile(Expression<Func<TModel, TKey>> getKey) : base(getKey)
    {
        ModelKeySelector = getKey ?? throw new ArgumentNullException(nameof(getKey));

        // $skiptoken is absent deliberately: this profile pages with $skip-bearing continuations, so
        // a client-sent $skiptoken is refused with 501 rather than accepted and ignored (§11.2.5).
        HonouredQueryOptions =
            OhDataSystemQueryOption.Filter | OhDataSystemQueryOption.OrderBy |
            OhDataSystemQueryOption.Top | OhDataSystemQueryOption.Skip |
            OhDataSystemQueryOption.Select | OhDataSystemQueryOption.Expand |
            OhDataSystemQueryOption.Count;
    }

    /// <summary>The model-side key selector this profile was constructed with.</summary>
    protected Expression<Func<TModel, TKey>> ModelKeySelector { get; }

    /// <summary>
    /// Declares the correspondence and wires every handler that follows from it.
    /// </summary>
    /// <remarks>
    /// Call once, from the constructor. It builds the maps, validates them, derives the entity-side
    /// key from the model-side one (so the two cannot disagree), and registers the collection
    /// handler, <c>GetById</c> and one batched navigation load per declared navigation.
    /// </remarks>
    /// <param name="entityQuery">
    /// Opens a fresh queryable over the entity — typically <c>() =&gt; _db.Products</c>. Called once
    /// per request; the profile is scoped, so capturing a <c>DbContext</c> here is the intended shape.
    /// </param>
    /// <param name="configure">Declares the root map and any nested maps.</param>
    protected void UseMap(
        Func<IQueryable<TEntity>> entityQuery,
        Action<MappedProfileBuilder<TEntity, TModel>> configure)
    {
        if (entityQuery is null) throw new ArgumentNullException(nameof(entityQuery));
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        if (_map is not null)
            throw new InvalidOperationException("UseMap(...) has already been called on this profile.");

        var builder = new MappedProfileBuilder<TEntity, TModel>();
        configure(builder);

        _entityQuery = entityQuery;
        _registry = builder.BuildRegistry();
        _map = _registry.Find(typeof(TModel))!;

        ModelMapValidator.Validate(_map, _registry);

        _projection = ModelProjection.BuildLambda(_map, _registry);
        _entityKey = new ModelToEntityRewriter(_map, _registry).RewriteLambda(ModelKeySelector);

        HideUnmappedMembers();

        GetODataQueryable = GetCollectionAsync;
        GetById = GetByIdAsync;

        RegisterNavigations();
    }

    /// <summary>
    /// Withdraws every member declared <c>Ignore()</c> from the EDM.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. A member with no entity source cannot be evaluated, so leaving it in the EDM
    /// let the parser accept <c>$filter=RenderedAt eq …</c> and the rewriter then threw — a
    /// <c>500</c> for a request only the map could have refused. Withdrawing it makes the
    /// framework's own <c>400</c> the answer, and stops the member being serialised on every row as
    /// its CLR default.
    /// </remarks>
    private void HideUnmappedMembers()
    {
        Expression<Func<TModel, object?>>[] unmapped = _map!.Bindings
            .Where(b => b.Kind == ModelBindingKind.Ignored)
            .Select(b =>
            {
                ParameterExpression d = Expression.Parameter(typeof(TModel), "d");
                return Expression.Lambda<Func<TModel, object?>>(
                    Expression.Convert(Expression.MakeMemberAccess(d, b.ModelMember), typeof(object)), d);
            })
            .ToArray();

        if (unmapped.Length > 0) Ignore(unmapped);
    }

    // ── Collection ────────────────────────────────────────────────────────────────────────────────

    private Task<ODataQueryResult<TModel>> GetCollectionAsync(
        ODataQueryOptions<TModel> options, CancellationToken ct)
    {
        var composer = new MappedQueryComposer<TEntity, TModel>(_map!, _registry!, options.Context.Model);

        IQueryable<TEntity> query = _entityQuery!();
        query = composer.ApplyFilter(query, options.Filter?.FilterClause);

        bool ordered = options.OrderBy?.OrderByClause is not null;
        query = composer.ApplyOrderBy(query, options.OrderBy?.OrderByClause);

        // §11.2.6.5: the count is of the items matching the request, unaffected by $top/$skip -- so it
        // is taken after $filter and before any window, as its own provider round-trip.
        long? total = options.Count?.Value == true ? query.LongCount() : null;

        // A tie-break so paging is deterministic: without it a client walking @odata.nextLink over an
        // unordered set can see one row twice and miss another.
        query = composer.Stabilize(query, _entityKey!, ordered);

        int skip = options.Skip?.Value ?? 0;
        int? requestedTop = options.Top?.Value;
        int pageSize = ResolvePageSize(requestedTop, options);

        if (skip > 0) query = query.Skip(skip);

        // One row past the page, so a full final page is distinguishable from a full page with more
        // behind it without a second round-trip.
        List<TModel> rows = query
            .Take(pageSize + 1)
            .Select((Expression<Func<TEntity, TModel>>)_projection!)
            .ToList();

        bool more = rows.Count > pageSize;
        if (more) rows.RemoveRange(pageSize, rows.Count - pageSize);

        string? nextLink = more
            ? MappedNextLink.Build(options, skip + pageSize, requestedTop is null ? null : requestedTop - pageSize)
            : null;

        return Task.FromResult(new ODataQueryResult<TModel>
        {
            Items = rows.AsQueryable(),
            TotalCount = total,
            NextLink = nextLink,
        });
    }

    private int ResolvePageSize(int? requestedTop, ODataQueryOptions<TModel> options)
    {
        int ceiling = MappedPageSize;

        // RFC 7240: a preference may only narrow, and one that was not applied must not be claimed.
        if (MappedNextLink.TryReadMaxPageSize(options, out int preferred) && preferred < ceiling)
        {
            ceiling = preferred;
            MappedNextLink.ApplyPreference(options, ceiling);
        }

        return requestedTop is int top && top < ceiling ? top : ceiling;
    }

    // ── Single entity ─────────────────────────────────────────────────────────────────────────────

    private Task<OhDataResult<TModel?>> GetByIdAsync(TKey key, CancellationToken ct)
    {
        ParameterExpression e = Expression.Parameter(typeof(TEntity), "e");

        // The key is read off a box rather than embedded as a ConstantExpression: EF Core inlines a
        // constant into the SQL, so every distinct key would compile and cache its own query plan.
        var box = new KeyBox { Value = key };

        var predicate = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(
                MapExpressions.Inline(_entityKey!, e),
                Expression.Field(Expression.Constant(box), nameof(KeyBox.Value))),
            e);

        // null is the framework's own "no such entity" for this handler, answered as 404.
        TModel? row = _entityQuery!()
            .Where(predicate)
            .Select((Expression<Func<TEntity, TModel>>)_projection!)
            .ToList()
            .FirstOrDefault();

        return Task.FromResult(OhDataResult.Success<TModel?>(row));
    }

    // ── Navigations ───────────────────────────────────────────────────────────────────────────────

    private void RegisterNavigations()
    {
        foreach (ModelMemberBinding binding in _map!.Navigations)
        {
            MethodInfo register = typeof(MappedEntitySetProfile<TKey, TModel, TEntity>)
                .GetMethod(
                    binding.Kind == ModelBindingKind.Collection
                        ? nameof(RegisterCollectionNavigation)
                        : nameof(RegisterReferenceNavigation),
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(binding.ElementModelType!);

            register.Invoke(this, new object[] { binding });
        }
    }

    private void RegisterCollectionNavigation<TNav>(ModelMemberBinding binding) where TNav : class
    {
        ModelMap elementMap = _registry!.Find(typeof(TNav))!;

        Func<IReadOnlyList<TKey>, CancellationToken, Task<ILookup<TKey, TNav>>> batch = (keys, _) =>
        {
            IReadOnlyList<KeyValuePair<object, object>> rows = MappedNavigationLoader.LoadCollection(
                _entityQuery!(), _entityKey!, binding, elementMap, _registry!, Box(keys));

            return Task.FromResult(rows.ToLookup(r => (TKey)r.Key, r => (TNav)r.Value));
        };

        HasMany(NavigationSelector<IEnumerable<TNav>>(binding.ModelMember), batch);
    }

    private void RegisterReferenceNavigation<TNav>(ModelMemberBinding binding) where TNav : class
    {
        ModelMap targetMap = _registry!.Find(typeof(TNav))!;

        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TNav?>>> batch = (keys, _) =>
        {
            IReadOnlyList<KeyValuePair<object, object>> rows = MappedNavigationLoader.LoadReference(
                _entityQuery!(), _entityKey!, binding, targetMap, _registry!, Box(keys));

            var byKey = new Dictionary<TKey, TNav?>();
            foreach (KeyValuePair<object, object> row in rows) byKey[(TKey)row.Key] = (TNav)row.Value;
            return Task.FromResult<IReadOnlyDictionary<TKey, TNav?>>(byKey);
        };

        HasOptional(NavigationSelector<TNav>(binding.ModelMember), batch);
    }

    private static IReadOnlyList<object> Box(IReadOnlyList<TKey> keys) => keys.Cast<object>().ToList();

    private static Expression<Func<TModel, TSelected>> NavigationSelector<TSelected>(MemberInfo member)
    {
        ParameterExpression d = Expression.Parameter(typeof(TModel), "d");

        // No Convert node, although the declared member is usually List<T> where the delegate says
        // IEnumerable<T>: Expression.Lambda accepts a reference-assignable body, and ModelBuilder's
        // PropertySelectorVisitor -- which reads this same expression to find the navigation --
        // throws "Unsupported Expression NodeType" on anything but a bare member access.
        return Expression.Lambda<Func<TModel, TSelected>>(Expression.MakeMemberAccess(d, member), d);
    }

    private sealed class KeyBox
    {
        public TKey Value = default!;
    }
}
