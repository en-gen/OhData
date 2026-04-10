using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

    private int? _maxTop;
    protected int? MaxTop
    {
        get => _maxTop;
        init
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTop), value, "MaxTop must be a positive integer or null.");
            _maxTop = value;
        }
    }
    private int? _resolvedMaxTop;

    /// <summary>
    /// When <c>true</c>, <c>DELETE</c> on a non-existent resource returns <c>204 No Content</c> (idempotent).
    /// When <c>false</c>, returns <c>404 Not Found</c>.
    /// Inherits from <see cref="EntitySetDefaults.IdempotentDelete"/> when <c>null</c>.
    /// </summary>
    protected bool? IdempotentDelete { get; init; }
    private bool _resolvedIdempotentDelete;
    private IReadOnlyList<BoundOperationDefinition>? _resolvedBoundFunctions;
    private IReadOnlyList<BoundOperationDefinition>? _resolvedBoundActions;

    private Func<TModel, string>? _getETag;

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
        if (propertySelectors.Length == 0)
            throw new ArgumentException("At least one property selector is required.", nameof(propertySelectors));
        var getters = propertySelectors.Select(e => e.Compile()).ToArray();
        var sep = new byte[] { 0x00 };
        _getETag = model =>
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

    private bool _authRequired;
    private string? _authPolicy;
    private IReadOnlyList<string>? _authRoles;

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
    void IVisitModelBuilder.VisitModelBuilder(ODataModelBuilder builder, EntitySetDefaults defaults)
    {
        var entitySet = builder.EntitySet<TModel>(EntitySetName);

        _resolvedMaxTop = MaxTop ?? defaults.MaxTop;
        _resolvedIdempotentDelete = IdempotentDelete ?? defaults.IdempotentDelete;

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
                if (param.HasDefaultValue)
                {
                    var defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
                    entityFunctionParam.HasDefaultValue(defaultStr);
                }
            }

            // Determine return type: unwrap Task<T>/ValueTask<T> if needed
            var rawReturn = method.ReturnType;
            var returnType = rawReturn.IsGenericType && (rawReturn.GetGenericTypeDefinition() == typeof(Task<>) || rawReturn.GetGenericTypeDefinition() == typeof(ValueTask<>))
                ? rawReturn.GetGenericArguments()[0]
                : rawReturn == typeof(Task) || rawReturn == typeof(void) || rawReturn == typeof(ValueTask) ? null : rawReturn;

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
                if (param.HasDefaultValue)
                {
                    var defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
                    entityActionParam.HasDefaultValue(defaultStr);
                }
            }

            // Resolve return type for $metadata, mirroring the function logic above.
            var rawActionReturn = method.ReturnType;
            var actionReturnType = rawActionReturn.IsGenericType && (rawActionReturn.GetGenericTypeDefinition() == typeof(Task<>) || rawActionReturn.GetGenericTypeDefinition() == typeof(ValueTask<>))
                ? rawActionReturn.GetGenericArguments()[0]
                : rawActionReturn == typeof(Task) || rawActionReturn == typeof(void) || rawActionReturn == typeof(ValueTask) ? null : rawActionReturn;

            if (actionReturnType is not null)
            {
                var collectionElement = GetCollectionElementType(actionReturnType);
                if (collectionElement is not null)
                {
                    typeof(ActionConfiguration)
                        .GetMethod(nameof(ActionConfiguration.ReturnsCollection), Array.Empty<Type>())!
                        .MakeGenericMethod(collectionElement)
                        .Invoke(entityAction, null);
                }
                else
                {
                    typeof(ActionConfiguration)
                        .GetMethod(nameof(ActionConfiguration.Returns), Array.Empty<Type>())!
                        .MakeGenericMethod(actionReturnType)
                        .Invoke(entityAction, null);
                }
            }
        }

        _resolvedBoundFunctions = _functions.Select(d => BoundOperationDefinition.From(d, isAction: false)).ToList();
        _resolvedBoundActions = _actions.Select(d => BoundOperationDefinition.From(d, isAction: true)).ToList();
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

    /// <summary>Requires any authenticated user. May be combined with <see cref="RequireRoles"/>.</summary>
    protected void RequireAuthorization()
    {
        if (_authRequired && _authPolicy is null && _authRoles is null)
            throw new InvalidOperationException(
                $"RequireAuthorization() has already been called on this profile. Call it only once.");
        _authRequired = true;
    }

    /// <summary>
    /// Requires a named ASP.NET Core authorization policy.
    /// Register policies via <c>services.AddAuthorization(o => o.AddPolicy(...))</c>.
    /// May be combined with <see cref="RequireRoles"/>.
    /// </summary>
    protected void RequireAuthorization(string policy)
    {
        if (_authPolicy is not null)
            throw new InvalidOperationException(
                $"A policy ('{_authPolicy}') has already been set on this profile. Call RequireAuthorization(policy) only once.");
        _authPolicy = policy;
        _authRequired = true;
    }

    /// <summary>
    /// Requires the user to be in at least one of the specified roles (OR semantics).
    /// May be combined with <see cref="RequireAuthorization(string)"/>.
    /// For AND semantics, register a named policy via <c>services.AddAuthorizationBuilder().AddPolicy(...)</c>.
    /// </summary>
    protected void RequireRoles(params string[] roles)
    {
        if (roles.Length == 0)
            throw new ArgumentException("At least one role must be specified.", nameof(roles));
        if (_authRoles is not null)
            throw new InvalidOperationException(
                $"Roles have already been set on this profile. Call RequireRoles only once.");
        _authRoles = Array.AsReadOnly(roles);
        _authRequired = true;
    }

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
    bool IEntitySetEndpointSource.HasETag => _getETag is not null;
    AuthorizationConfig? IEntitySetEndpointSource.Authorization =>
        _authRequired ? new AuthorizationConfig(true, _authPolicy, _authRoles) : null;
    IReadOnlyList<NavigationRouteDefinition> IEntitySetEndpointSource.NavigationRoutes => _navRoutes;
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundFunctions =>
        _resolvedBoundFunctions ?? _functions.Select(d => BoundOperationDefinition.From(d, isAction: false)).ToList();
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundActions =>
        _resolvedBoundActions ?? _actions.Select(d => BoundOperationDefinition.From(d, isAction: true)).ToList();
    string IEntitySetEndpointSource.InvokeGetETag(object model) => _getETag!((TModel)model);

    private Func<TModel, string>? _keyToString;
    private Func<TModel, string> CompileKeyToString()
    {
        var compiled = _getKey.Compile();
        return model => string.Format(CultureInfo.InvariantCulture, "{0}", compiled(model)) ?? "";
    }
    string IEntitySetEndpointSource.InvokeGetKeyString(object model)
        => LazyInitializer.EnsureInitialized(ref _keyToString, CompileKeyToString)((TModel)model);
    int? IEntitySetEndpointSource.MaxTop => _resolvedMaxTop;
    bool IEntitySetEndpointSource.IdempotentDelete => _resolvedIdempotentDelete;
    string IEntitySetEndpointSource.KeyPropertyName => GetNavigationPropertyName(_getKey.Body);

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
