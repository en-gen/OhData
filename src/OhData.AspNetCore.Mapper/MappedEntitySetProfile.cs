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
/// <c>Microsoft.AspNetCore.OData</c>'s own binders, then <b>substituted</b> into entity terms and
/// applied to the entity queryable — so the predicate reaches the database. Only the rows of one
/// page are then materialised and mapped. Nothing is filtered, sorted or paged in memory.
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
    /// could not be applied here anyway (the resolved value is computed on the startup instance,
    /// while handlers run on a request-scoped one). 1000 is <c>EntitySetDefaults.MaxTop</c>'s own
    /// default, so an adopter who changes neither sees the same ceiling either way.
    /// </remarks>
    protected int MappedPageSize { get; init; } = 1000;

    private Func<IQueryable<TEntity>>? _entityQuery;
    private ModelMap? _map;
    private ModelMapRegistry? _registry;
    private MappedQueryComposer<TEntity, TModel>? _composer;
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

    /// <summary>The resolved correspondence, available once <see cref="UseMap"/> has run.</summary>
    protected ModelMap Map => _map ?? throw new InvalidOperationException(NotConfigured);

    /// <summary>
    /// The maps this profile declared, root and nested.
    /// </summary>
    /// <remarks>
    /// Deliberately not named <c>Maps</c>: a member on a base class shadows a same-named type in
    /// scope, so a profile whose own declarations live in a helper class called <c>Maps</c> would
    /// stop compiling for a reason that reads as nonsense.
    /// </remarks>
    protected ModelMapRegistry MapRegistry => _registry ?? throw new InvalidOperationException(NotConfigured);

    private static string NotConfigured =>
        "The profile has not declared its map yet. Call UseMap(...) from the constructor.";

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

        _projection = ModelProjection.BuildLambda(_map);
        _entityKey = new ModelToEntityRewriter(_map, _registry.Resolver).RewriteLambda(ModelKeySelector);

        GetODataQueryable = GetCollectionAsync;
        GetById = GetByIdAsync;

        RegisterNavigations();
    }

    // ── Collection ────────────────────────────────────────────────────────────────────────────────

    private Task<ODataQueryResult<TModel>> GetCollectionAsync(
        ODataQueryOptions<TModel> options, CancellationToken ct)
    {
        MappedQueryComposer<TEntity, TModel> composer = ResolveComposer(options);

        IQueryable<TEntity> query = _entityQuery!();
        query = composer.ApplyFilter(query, options.Filter?.FilterClause);

        bool ordered = options.OrderBy?.OrderByClause is not null;
        query = composer.ApplyOrderBy(query, options.OrderBy?.OrderByClause);

        // §11.2.6.5: the count is of the items matching the request, unaffected by $top/$skip -- so it
        // is taken after $filter and before any window, and as its own provider round-trip rather
        // than off the materialised page.
        long? total = options.Count?.Value == true ? query.LongCount() : null;

        // A tie-break so paging is deterministic. Without it a client walking @odata.nextLink over an
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

        // RFC 7240: a preference may only narrow. Preference-Applied is emitted by the core when it
        // does the paging; this profile pages itself, so it emits its own.
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
        Expression keyValue = new ParameterReplacer(_entityKey!.Parameters[0], e).Visit(_entityKey.Body);

        var predicate = Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(keyValue, Expression.Constant(key, _entityKey.ReturnType)), e);

        // null is the framework's own "no such entity" for this handler, answered as 404. Rejecting
        // deliberately is ConfigureExceptions' business, not this profile's.
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
            Type navModelType = binding.ElementModelType
                ?? throw new InvalidOperationException(
                    $"'{typeof(TModel).Name}.{binding.ModelMember.Name}' declares no model element type.");

            if (_registry!.Find(navModelType) is null)
            {
                throw new InvalidOperationException(
                    $"'{typeof(TModel).Name}.{binding.ModelMember.Name}' maps to model type " +
                    $"'{navModelType.Name}', which has no map. Declare one with " +
                    $"Nested<{binding.ElementEntityType?.Name}, {navModelType.Name}>(...) so " +
                    $"$expand and a nested $filter can substitute through its own bindings.");
            }

            MethodInfo register = typeof(MappedEntitySetProfile<TKey, TModel, TEntity>)
                .GetMethod(
                    binding.Kind == ModelBindingKind.Collection
                        ? nameof(RegisterCollectionNavigation)
                        : nameof(RegisterReferenceNavigation),
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(navModelType);

            register.Invoke(this, new object[] { binding });
        }
    }

    private void RegisterCollectionNavigation<TNav>(ModelMemberBinding binding) where TNav : class
    {
        ModelMap elementMap = _registry!.Find(typeof(TNav))!;

        Func<IReadOnlyList<TKey>, CancellationToken, Task<ILookup<TKey, TNav>>> batch = (keys, _) =>
        {
            IReadOnlyList<KeyValuePair<object, object>> rows = MappedNavigationLoader.LoadCollection(
                _entityQuery!(), _entityKey!, binding, elementMap, Box(keys));

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
                _entityQuery!(), _entityKey!, binding, targetMap, Box(keys));

            var byKey = new Dictionary<TKey, TNav?>();
            foreach (KeyValuePair<object, object> row in rows) byKey[(TKey)row.Key] = (TNav)row.Value;
            return Task.FromResult<IReadOnlyDictionary<TKey, TNav?>>(byKey);
        };

        HasOptional(NavigationSelector<TNav>(binding.ModelMember), batch);
    }

    private static IReadOnlyList<object> Box(IReadOnlyList<TKey> keys)
    {
        var boxed = new List<object>(keys.Count);
        foreach (TKey key in keys) if (key is not null) boxed.Add(key);
        return boxed;
    }

    private static Expression<Func<TModel, TSelected>> NavigationSelector<TSelected>(MemberInfo member)
    {
        ParameterExpression d = Expression.Parameter(typeof(TModel), "d");
        Expression access = Expression.MakeMemberAccess(d, member);

        // No Convert node, although the declared member is usually List<T> where the selector's
        // delegate says IEnumerable<T>: Expression.Lambda accepts a reference-assignable body, and
        // ModelBuilder's PropertySelectorVisitor -- which reads this same expression to find the
        // navigation -- throws "Unsupported Expression NodeType" on anything but a bare member
        // access. Measured: adding the conversion fails EDM construction for the whole profile.
        return Expression.Lambda<Func<TModel, TSelected>>(access, d);
    }

    private MappedQueryComposer<TEntity, TModel> ResolveComposer(ODataQueryOptions options) =>
        _composer ??= new MappedQueryComposer<TEntity, TModel>(_map!, _registry!, options.Context.Model);

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly Expression _to;

        public ParameterReplacer(ParameterExpression from, Expression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _from ? _to : node;
    }
}

/// <summary>Declares a profile's root map and the nested maps its navigations reach.</summary>
/// <typeparam name="TEntity">The root entity type.</typeparam>
/// <typeparam name="TModel">The root model type.</typeparam>
public sealed class MappedProfileBuilder<TEntity, TModel>
    where TEntity : class
    where TModel : class
{
    private readonly ModelMapBuilder<TEntity, TModel> _root = new();
    private readonly List<ModelMap> _nested = new();

    /// <summary>Declares the root model's correspondence.</summary>
    public MappedProfileBuilder<TEntity, TModel> Root(Action<ModelMapBuilder<TEntity, TModel>> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        configure(_root);
        return this;
    }

    /// <summary>
    /// Declares the correspondence for a model a navigation reaches, so <c>$expand</c> and a nested
    /// <c>$filter</c> substitute through its own bindings rather than repeating them at each use.
    /// </summary>
    /// <typeparam name="TNestedEntity">The related entity type.</typeparam>
    /// <typeparam name="TNestedModel">The related model type.</typeparam>
    public MappedProfileBuilder<TEntity, TModel> Nested<TNestedEntity, TNestedModel>(
        Action<ModelMapBuilder<TNestedEntity, TNestedModel>> configure)
        where TNestedEntity : class
        where TNestedModel : class
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        ModelMapBuilder<TNestedEntity, TNestedModel> builder = new();
        configure(builder);
        _nested.Add(builder.Build());
        return this;
    }

    internal ModelMapRegistry BuildRegistry()
    {
        var registry = new ModelMapRegistry();
        registry.Add(_root.Build());
        foreach (ModelMap map in _nested) registry.Add(map);
        return registry;
    }
}
