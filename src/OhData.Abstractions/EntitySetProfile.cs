using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.OData.ModelBuilder;

namespace OhData.Abstractions;

public abstract class EntitySetProfile<TKey, TModel> : IEntitySetProfile, IVisitModelBuilder, IEntitySetEndpointSource
    where TModel : class
{
    private readonly Expression<Func<TModel, TKey>> _getKey;

    protected string EntitySetName { get; init; }

    protected bool? SelectEnabled { get; init; }
    protected bool? ExpandEnabled { get; init; }
    protected bool? FilterEnabled { get; init; }
    protected bool? OrderByEnabled { get; init; }
    protected bool? CountEnabled { get; init; }

    protected string[]? SelectProperties { get; init; } = null;
    protected string[]? ExpandProperties { get; init; } = null;
    protected string[]? FilterProperties { get; init; } = null;
    protected string[]? OrderByProperties { get; init; } = null;

    protected Func<CancellationToken, Task<IEnumerable<TModel>>>? GetAll = null;
    protected Func<CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;
    protected Func<TKey, CancellationToken, Task<TModel?>>? GetById = null;

    protected Func<TKey, TModel, CancellationToken, Task<TModel>>? PutById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Post = null;

    protected Func<TKey, TModel, CancellationToken, Task<TModel?>>? Patch = null;

    protected Func<TKey, CancellationToken, Task<bool>>? Delete = null;

    protected int? MaxTop { get; init; } = null;
    private int? _resolvedMaxTop;

    protected Func<TModel, string>? GetETag = null;

    /// <summary>
    /// Opts in to ETag generation. The framework hashes the values of the specified
    /// properties using SHA-256 and encodes the result as Base64.
    /// <para>
    /// Supports <c>byte[]</c> values (e.g. row-version columns) directly;
    /// all other values are hashed as their UTF-8 string representations.
    /// </para>
    /// </summary>
    protected void UseETag(params Expression<Func<TModel, object?>>[] propertySelectors)
    {
        var getters = propertySelectors.Select(e => e.Compile()).ToArray();
        GetETag = model =>
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var sep = new byte[] { 0x00 };
            for (var i = 0; i < getters.Length; i++)
            {
                if (i > 0) hasher.AppendData(sep);
                var value = getters[i](model);
                if (value is byte[] bytes)
                    hasher.AppendData(bytes);
                else if (value is not null)
                    hasher.AppendData(Encoding.UTF8.GetBytes(value.ToString()!));
            }
            return Convert.ToBase64String(hasher.GetHashAndReset());
        };
    }

    private AuthorizationConfig? _authorization;

    private readonly ICollection<Action<EntityTypeConfiguration<TModel>>> _configurators;
    private readonly ICollection<Delegate> _functions;
    private readonly ICollection<Delegate> _actions;
    private readonly List<NavigationRouteDefinition> _navRoutes = new();

    protected EntitySetProfile(Expression<Func<TModel, TKey>> getKey)
    {
        _getKey = getKey;
        EntitySetName = $"{typeof(TModel).Name}s";

        _configurators = new List<Action<EntityTypeConfiguration<TModel>>>();
        _functions = new List<Delegate>();
        _actions = new List<Delegate>();
    }

    /// <summary>
    /// Hands over full configuration control. If this method is overridden, you are ejecting
    /// from all other configuration behaviors of this class.
    /// </summary>
    protected virtual void AdvancedConfigure(EntitySetConfiguration<TModel> configuration) { }

    // explicit interface implementation to enforce internal
    void IVisitModelBuilder.VisitModelBuilder(ODataModelBuilder builder, OhDataContext context, EntitySetDefaults defaults)
    {
        var entitySet = builder.EntitySet<TModel>(EntitySetName);

        AdvancedConfigure(entitySet);

        // eject if AdvancedConfigure was overridden
        var advancedConfigureDeclaredInType = GetType()
            .GetMethod(
                nameof(AdvancedConfigure),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null,
                new[] { typeof(EntitySetConfiguration<TModel>) },
                null)
            ?.DeclaringType;
        if (advancedConfigureDeclaredInType != typeof(EntitySetProfile<TKey, TModel>)) return;

        // if AdvancedConfigure wasn't overridden, work your magic
        var entityType = entitySet.EntityType;

        _resolvedMaxTop = MaxTop ?? defaults.MaxTop;

        if (SelectEnabled ?? defaults.SelectEnabled) entityType.Select(SelectProperties);
        if (ExpandEnabled ?? defaults.ExpandEnabled) entityType.Expand(ExpandProperties);
        if (FilterEnabled ?? defaults.FilterEnabled) entityType.Filter(FilterProperties);
        if (OrderByEnabled ?? defaults.OrderByEnabled) entityType.OrderBy(OrderByProperties);
        if (CountEnabled ?? defaults.CountEnabled) entityType.Count();

        entityType.HasKey(_getKey);
        foreach (var configurator in _configurators) configurator(entityType);

        var entityCollection = entityType.Collection;

        foreach (var method in _functions.Select(x => x.Method))
        {
            var entityFunction = entityCollection.Function(method.Name);

            foreach (var param in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            {
                var entityFunctionParam = entityFunction.Parameter(param.ParameterType, param.Name!);
                if (param.IsOptional) entityFunctionParam.Optional();
                if (param.HasDefaultValue) entityFunctionParam.HasDefaultValue($"{param.DefaultValue}");
            }

            // Determine return type: unwrap Task<T> if needed
            var rawReturn = method.ReturnType;
            var returnType = rawReturn.IsGenericType && rawReturn.GetGenericTypeDefinition() == typeof(Task<>)
                ? rawReturn.GetGenericArguments()[0]
                : rawReturn == typeof(Task) || rawReturn == typeof(void) ? null : rawReturn;

            if (returnType is not null)
            {
                // FunctionConfiguration only exposes generic Returns<T>/ReturnsCollection<T>; call via reflection.
                var collectionElement = GetCollectionElementType(returnType);
                if (collectionElement is not null)
                {
                    typeof(FunctionConfiguration)
                        .GetMethod(nameof(FunctionConfiguration.ReturnsCollection), Array.Empty<Type>())!
                        .MakeGenericMethod(collectionElement)
                        .Invoke(entityFunction, null);
                }
                else
                {
                    typeof(FunctionConfiguration)
                        .GetMethod(nameof(FunctionConfiguration.Returns), Array.Empty<Type>())!
                        .MakeGenericMethod(returnType)
                        .Invoke(entityFunction, null);
                }
            }
        }

        foreach (var method in _actions.Select(x => x.Method))
        {
            var entityAction = entityCollection.Action(method.Name);

            foreach (var param in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
            {
                var entityActionParam = entityAction.Parameter(param.ParameterType, param.Name!);
                if (param.IsOptional) entityActionParam.Optional();
                if (param.HasDefaultValue) entityActionParam.HasDefaultValue($"{param.DefaultValue}");
            }
        }
    }

    protected void HasOptional<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasOptional(navigation));
    }

    protected void HasOptional<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation?>>? get)
        where TNavigation : class
    {
        HasOptional(navigation);
        if (get is null) return;
        var propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = async (key, ct) => (object?)await get((TKey)key, ct)
        });
    }

    protected void HasRequired<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasRequired(navigation));
    }

    protected void HasRequired<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation>>? get)
        where TNavigation : class
    {
        HasRequired(navigation);
        if (get is null) return;
        var propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = async (key, ct) => (object?)await get((TKey)key, ct)
        });
    }

    protected void HasMany<TNavigation>(Expression<Func<TModel, IEnumerable<TNavigation>>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasMany(navigation));
    }

    protected void HasMany<TNavigation>(
        Expression<Func<TModel, IEnumerable<TNavigation>>> navigation,
        Func<TKey, CancellationToken, Task<IEnumerable<TNavigation>>>? getAll)
        where TNavigation : class
    {
        HasMany(navigation);
        if (getAll is null) return;
        var propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            Handler = async (key, ct) => (object?)await getAll((TKey)key, ct)
        });
    }

    protected void BindFunction(Delegate handler) => _functions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    protected void BindAction(Delegate handler) => _actions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    private static string GetNavigationPropertyName(Expression body)
    {
        if (body is MemberExpression me) return me.Member.Name;
        if (body is UnaryExpression ue) return GetNavigationPropertyName(ue.Operand);
        throw new ArgumentException(
            $"Cannot extract property name from expression type {body.NodeType}. Use a simple property accessor: x => x.PropertyName.");
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type == typeof(string)) return null; // string is IEnumerable<char> but not a "collection" here
        if (type.IsArray) return type.GetElementType();
        foreach (var iface in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    protected void RequireAuthorization() => _authorization = new AuthorizationConfig(required: true, policy: null, roles: null);
    protected void RequireAuthorization(string policy) => _authorization = new AuthorizationConfig(required: true, policy: policy, roles: null);
    protected void RequireRoles(params string[] roles) => _authorization = new AuthorizationConfig(required: true, policy: null, roles: roles);

    // ── IEntitySetEndpointSource ─────────────────────────────────────────────

    string IEntitySetEndpointSource.EntitySetName => EntitySetName;
    Type IEntitySetEndpointSource.KeyType => typeof(TKey);
    Type IEntitySetEndpointSource.ModelType => typeof(TModel);

    bool IEntitySetEndpointSource.HasGetAll => GetAll is not null;
    bool IEntitySetEndpointSource.HasGetQueryable => GetQueryable is not null;
    bool IEntitySetEndpointSource.HasGetById => GetById is not null;
    bool IEntitySetEndpointSource.HasPost => Post is not null;
    bool IEntitySetEndpointSource.HasPutById => PutById is not null;
    bool IEntitySetEndpointSource.HasPatch => Patch is not null;
    bool IEntitySetEndpointSource.HasDelete => Delete is not null;
    bool IEntitySetEndpointSource.HasETag => GetETag is not null;
    AuthorizationConfig? IEntitySetEndpointSource.Authorization => _authorization;
    IReadOnlyList<NavigationRouteDefinition> IEntitySetEndpointSource.NavigationRoutes => _navRoutes;
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundFunctions =>
        _functions.Select(d => BoundOperationDefinition.From(d, isAction: false)).ToList();
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundActions =>
        _actions.Select(d => BoundOperationDefinition.From(d, isAction: true)).ToList();
    string IEntitySetEndpointSource.InvokeGetETag(object model) => GetETag!((TModel)model);

    private Func<TModel, string>? _keyToString;
    private Func<TModel, string> CompileKeyToString()
    {
        var compiled = _getKey.Compile();
        return model => compiled(model)?.ToString() ?? "";
    }
    string IEntitySetEndpointSource.InvokeGetKeyString(object model)
        => (_keyToString ??= CompileKeyToString())((TModel)model);
    int? IEntitySetEndpointSource.MaxTop => _resolvedMaxTop;

    async Task<object?> IEntitySetEndpointSource.InvokeGetAllAsync(CancellationToken ct) =>
        (object?)await GetAll!.Invoke(ct);

    async Task<IQueryable<object>> IEntitySetEndpointSource.InvokeGetQueryableAsync(CancellationToken ct) =>
        (await GetQueryable!.Invoke(ct)).Cast<object>();

    async Task<object?> IEntitySetEndpointSource.InvokeGetByIdAsync(object key, CancellationToken ct) =>
        (object?)await GetById!.Invoke((TKey)key, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePostAsync(object model, CancellationToken ct) =>
        (object?)await Post!.Invoke((TModel)model, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePutByIdAsync(object key, object model, CancellationToken ct) =>
        (object?)await PutById!.Invoke((TKey)key, (TModel)model, ct);

    async Task<object?> IEntitySetEndpointSource.InvokePatchAsync(object key, object model, CancellationToken ct) =>
        (object?)await Patch!.Invoke((TKey)key, (TModel)model, ct);

    Task<bool> IEntitySetEndpointSource.InvokeDeleteAsync(object key, CancellationToken ct) =>
        Delete!.Invoke((TKey)key, ct);
}
