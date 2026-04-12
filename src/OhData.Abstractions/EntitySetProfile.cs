using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    private string[]? _selectProperties;
    private string[]? _expandProperties;
    private string[]? _filterProperties;
    private string[]? _orderByProperties;

    protected Func<CancellationToken, Task<IEnumerable<TModel>>>? GetAll = null;
    protected Func<CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;
    protected Func<TKey, CancellationToken, Task<TModel?>>? GetById = null;

    protected Func<TKey, TModel, CancellationToken, Task<TModel>>? PutById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Post = null;

    protected Func<TKey, TModel, CancellationToken, Task<TModel?>>? Patch = null;

    protected Func<TKey, CancellationToken, Task<bool>>? Delete = null;

    // Gap 4: $search support
    protected Func<string, CancellationToken, Task<IEnumerable<TModel>>>? Search = null;

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

    /// <summary>
    /// When <c>true</c>, a <c>PUT</c> to a non-existent key creates the entity (upsert, §11.4.4).
    /// Requires <see cref="Post"/> to also be configured. Inherits from
    /// <see cref="EntitySetDefaults.AllowUpsert"/> when <c>null</c>.
    /// </summary>
    protected bool? AllowUpsert { get; init; }
    private bool _resolvedAllowUpsert;
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
        byte[] sep = new byte[] { 0x00 };
        _getETag = model =>
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < getters.Length; i++)
            {
                if (i > 0) hasher.AppendData(sep);
                object? value = getters[i](model);
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
    private readonly ICollection<Delegate> _entityFunctions;
    private readonly ICollection<Delegate> _entityActions;
    private readonly List<NavigationRouteDefinition> _navRoutes = new();

    protected EntitySetProfile(Expression<Func<TModel, TKey>> getKey)
    {
        _getKey = getKey;
        EntitySetName = $"{typeof(TModel).Name}s";

        _configurators = new List<Action<EntityTypeConfiguration<TModel>>>();
        _functions = new List<Delegate>();
        _actions = new List<Delegate>();
        _entityFunctions = new List<Delegate>();
        _entityActions = new List<Delegate>();
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
        _resolvedAllowUpsert = AllowUpsert ?? defaults.AllowUpsert;

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

        if (SelectEnabled ?? defaults.SelectEnabled) entityType.Select(_selectProperties);
        if (ExpandEnabled ?? defaults.ExpandEnabled) entityType.Expand(_expandProperties);
        if (FilterEnabled ?? defaults.FilterEnabled) entityType.Filter(_filterProperties);
        if (OrderByEnabled ?? defaults.OrderByEnabled) entityType.OrderBy(_orderByProperties);
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
                    string defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
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
                    string defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
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

        // Gap 7: Register entity-level functions bound to the entity type (not collection)
        foreach (var method in _entityFunctions.Select(x => x.Method))
        {
            // Skip the first parameter — it is the key (TKey), not an OData parameter
            var entityFunction = entityType.Function(method.Name);
            var allParams = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).Skip(1);

            foreach (var param in allParams)
            {
                var entityFunctionParam = entityFunction.Parameter(param.ParameterType, param.Name!);
                if (param.IsOptional) entityFunctionParam.Optional();
                if (param.HasDefaultValue)
                {
                    string defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
                    entityFunctionParam.HasDefaultValue(defaultStr);
                }
            }

            var rawReturn = method.ReturnType;
            var returnType = rawReturn.IsGenericType && (rawReturn.GetGenericTypeDefinition() == typeof(Task<>) || rawReturn.GetGenericTypeDefinition() == typeof(ValueTask<>))
                ? rawReturn.GetGenericArguments()[0]
                : rawReturn == typeof(Task) || rawReturn == typeof(void) || rawReturn == typeof(ValueTask) ? null : rawReturn;

            if (returnType is not null)
            {
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

        // Gap 7: Register entity-level actions bound to the entity type (not collection)
        foreach (var method in _entityActions.Select(x => x.Method))
        {
            var entityAction = entityType.Action(method.Name);
            var allParams = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).Skip(1);

            foreach (var param in allParams)
            {
                var entityActionParam = entityAction.Parameter(param.ParameterType, param.Name!);
                if (param.IsOptional) entityActionParam.Optional();
                if (param.HasDefaultValue)
                {
                    string defaultStr = param.DefaultValue is bool b ? (b ? "true" : "false") : $"{param.DefaultValue}";
                    entityActionParam.HasDefaultValue(defaultStr);
                }
            }

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

        _resolvedBoundFunctions = _functions.Select(d => BoundOperationDefinition.From(d, isAction: false))
            .Concat(_entityFunctions.Select(d => BoundOperationDefinition.From(d, isAction: false, isEntityLevel: true)))
            .ToList();
        _resolvedBoundActions = _actions.Select(d => BoundOperationDefinition.From(d, isAction: true))
            .Concat(_entityActions.Select(d => BoundOperationDefinition.From(d, isAction: true, isEntityLevel: true)))
            .ToList();
    }

    /// <summary>
    /// Restricts the properties that may appear in <c>$filter</c> queries.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void FilterProperties(params Expression<Func<TModel, object?>>[] properties)
        => _filterProperties = ExtractNames(properties);

    /// <summary>
    /// Restricts the properties that may appear in <c>$filter</c> queries.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void FilterProperties(params string[]? properties)
        => _filterProperties = properties;

    /// <summary>
    /// Restricts the properties that may appear in <c>$orderby</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void OrderByProperties(params Expression<Func<TModel, object?>>[] properties)
        => _orderByProperties = ExtractNames(properties);

    /// <summary>
    /// Restricts the properties that may appear in <c>$orderby</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void OrderByProperties(params string[]? properties)
        => _orderByProperties = properties;

    /// <summary>
    /// Restricts the properties that may appear in <c>$select</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void SelectProperties(params Expression<Func<TModel, object?>>[] properties)
        => _selectProperties = ExtractNames(properties);

    /// <summary>
    /// Restricts the properties that may appear in <c>$select</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void SelectProperties(params string[]? properties)
        => _selectProperties = properties;

    /// <summary>
    /// Restricts the properties that may be used in <c>$expand</c> clauses.
    /// Set using either this overload or the string overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void ExpandProperties(params Expression<Func<TModel, object?>>[] properties)
        => _expandProperties = ExtractNames(properties);

    /// <summary>
    /// Restricts the properties that may be used in <c>$expand</c> clauses.
    /// Set using either this overload or the expression overload, not both.
    /// Pass no arguments (or call with <c>null</c>) to allow all properties.
    /// </summary>
    protected void ExpandProperties(params string[]? properties)
        => _expandProperties = properties;

    /// <summary>
    /// Extracts member names from a set of simple property-access expressions.
    /// Throws <see cref="ArgumentException"/> if an expression is not a direct member access.
    /// </summary>
    private static string[] ExtractNames(Expression<Func<TModel, object?>>[] expressions)
    {
        string[] names = new string[expressions.Length];
        for (int i = 0; i < expressions.Length; i++)
        {
            var body = expressions[i].Body;

            // Strip boxing Convert / ConvertChecked nodes (e.g. value types cast to object)
            if (body is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            if (body is not MemberExpression member)
            {
                throw new ArgumentException(
                    $"Expression at index {i} must be a direct property access (e.g. x => x.Name). " +
                    $"Nested access such as x => x.Category.Name is not supported.",
                    nameof(expressions));
            }

            names[i] = member.Member.Name;
        }
        return names;
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
        string propName = GetNavigationPropertyName(navigation.Body);
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
        string propName = GetNavigationPropertyName(navigation.Body);
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
        string propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            Handler = async (key, ct) => (object?)await getAll((TKey)key, ct)
        });
    }

    /// <summary>
    /// Registers a many navigation with optional <c>$ref</c> link management handlers (§11.4.6).
    /// </summary>
    protected void HasMany<TNavigation>(
        Expression<Func<TModel, IEnumerable<TNavigation>>> navigation,
        Func<TKey, CancellationToken, Task<IEnumerable<TNavigation>>>? getAll,
        Func<TKey, string, CancellationToken, Task>? addRef = null,
        Func<TKey, string, CancellationToken, Task>? removeRef = null)
        where TNavigation : class
    {
        HasMany(navigation);
        if (getAll is null && addRef is null && removeRef is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = true,
            Handler = getAll is not null
                ? async (key, ct) => (object?)await getAll((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            AddRef = addRef is not null
                ? (key, relatedId, ct) => addRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            RemoveRef = removeRef is not null
                ? (key, relatedId, ct) => removeRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
        });
    }

    /// <summary>
    /// Registers an optional navigation with optional <c>$ref</c> link management handlers (§11.4.6).
    /// </summary>
    protected void HasOptional<TNavigation>(
        Expression<Func<TModel, TNavigation>> navigation,
        Func<TKey, CancellationToken, Task<TNavigation?>>? get,
        Func<TKey, string, CancellationToken, Task>? setRef = null,
        Func<TKey, string, CancellationToken, Task>? removeRef = null)
        where TNavigation : class
    {
        HasOptional(navigation);
        if (get is null && setRef is null && removeRef is null) return;
        string propName = GetNavigationPropertyName(navigation.Body);
        _navRoutes.Add(new NavigationRouteDefinition
        {
            PropertyName = propName,
            IsCollection = false,
            Handler = get is not null
                ? async (key, ct) => (object?)await get((TKey)key, ct)
                : (_, _) => Task.FromResult<object?>(null),
            AddRef = setRef is not null
                ? (key, relatedId, ct) => setRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
            RemoveRef = removeRef is not null
                ? (key, relatedId, ct) => removeRef((TKey)key, (string)relatedId, ct)
                : (Func<object, object, CancellationToken, Task>?)null,
        });
    }

    protected void BindFunction(Delegate handler) => _functions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    protected void BindAction(Delegate handler) => _actions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>
    /// Registers an entity-level bound function: GET /{EntitySet}({key})/{MethodName}.
    /// The handler's first non-CancellationToken parameter must be the key (TKey).
    /// </summary>
    protected void BindEntityFunction(Delegate handler) => _entityFunctions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>
    /// Registers an entity-level bound action: POST /{EntitySet}({key})/{MethodName}.
    /// The handler's first non-CancellationToken parameter must be the key (TKey).
    /// </summary>
    protected void BindEntityAction(Delegate handler) => _entityActions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

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
        if (_authRequired)
        {
            throw new InvalidOperationException(
                "Authorization has already been configured on this profile. " +
                "RequireAuthorization() is implicit when RequireAuthorization(policy) or RequireRoles() is called.");
        }

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
        {
            throw new InvalidOperationException(
                $"A policy ('{_authPolicy}') has already been set on this profile. Call RequireAuthorization(policy) only once.");
        }

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
        {
            throw new InvalidOperationException(
                $"Roles have already been set on this profile. Call RequireRoles only once.");
        }

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
        _resolvedBoundFunctions ?? _functions.Select(d => BoundOperationDefinition.From(d, isAction: false))
            .Concat(_entityFunctions.Select(d => BoundOperationDefinition.From(d, isAction: false, isEntityLevel: true)))
            .ToList();
    IReadOnlyList<BoundOperationDefinition> IEntitySetEndpointSource.BoundActions =>
        _resolvedBoundActions ?? _actions.Select(d => BoundOperationDefinition.From(d, isAction: true))
            .Concat(_entityActions.Select(d => BoundOperationDefinition.From(d, isAction: true, isEntityLevel: true)))
            .ToList();
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
    bool IEntitySetEndpointSource.AllowUpsert => _resolvedAllowUpsert;
    bool IEntitySetEndpointSource.HasSearch => Search is not null;
    string IEntitySetEndpointSource.KeyPropertyName => GetNavigationPropertyName(_getKey.Body);

    async Task<IEnumerable<object>> IEntitySetEndpointSource.InvokeSearchAsync(string searchTerm, CancellationToken ct)
    {
        var result = await Search!.Invoke(searchTerm, ct);
        return result.Cast<object>();
    }

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
