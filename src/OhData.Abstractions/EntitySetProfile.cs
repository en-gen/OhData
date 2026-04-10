using System.Linq.Expressions;
using Microsoft.OData.ModelBuilder;

namespace OhData.Abstractions;

public abstract class EntitySetProfile<TKey, TModel> : IEntitySetProfile, IVisitModelBuilder, IEntitySetEndpointSource
    where TModel : class
{
    private readonly Expression<Func<TModel, TKey>> _getKey;

    protected string EntitySetName;

    protected bool? SelectEnabled;
    protected bool? ExpandEnabled;
    protected bool? FilterEnabled;
    protected bool? OrderByEnabled;
    protected bool? CountEnabled;

    protected string[]? SelectProperties = null;
    protected string[]? ExpandProperties = null;
    protected string[]? FilterProperties = null;
    protected string[]? OrderByProperties = null;

    protected Func<CancellationToken, Task<IEnumerable<TModel>>>? GetAll = null;
    protected Func<CancellationToken, Task<IQueryable<TModel>>>? GetQueryable = null;
    protected Func<TKey, CancellationToken, Task<TModel?>>? GetById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Put = null;
    protected Func<TKey, TModel, CancellationToken, Task<TModel>>? PutById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Post = null;

    protected Func<TKey, TModel, CancellationToken, Task<TModel?>>? Patch = null;

    protected Func<TKey, CancellationToken, Task<bool>>? Delete = null;

    private AuthorizationConfig? _authorization;

    private readonly ICollection<Action<EntityTypeConfiguration<TModel>>> _configurators;
    private readonly ICollection<Delegate> _functions;
    private readonly ICollection<Delegate> _actions;

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

            foreach (var param in method.GetParameters())
            {
                var entityFunctionParam = entityFunction.Parameter(param.ParameterType, param.Name!);

                // TODO: this will probably need to be a tad more robust...
                if (param.IsOptional) entityFunctionParam.Optional();
                if (param.HasDefaultValue) entityFunctionParam.HasDefaultValue($"{param.DefaultValue}");
            }

            // TODO: what to do with return type
            // TODO:    - basic return type is simple
            // TODO:    - returns entityset is not simple
            //entityFunction.Returns<>()
            //entityFunction.ReturnsCollection<>()
            //entityFunction.ReturnsFromEntitySet<>()

            // TODO: check context if EntitySetProfile or EntityProfile was defined for method return type
        }

        foreach (var method in _actions.Select(x => x.Method))
        {
            var entityAction = entityCollection.Action(method.Name);

            foreach (var param in method.GetParameters())
            {
                var entityActionParam = entityAction.Parameter(param.ParameterType, param.Name!);
                if (param.IsOptional) entityActionParam.Optional();
                if (param.HasDefaultValue) entityActionParam.HasDefaultValue($"{param.DefaultValue}");
            }

            // TODO: concerns above re: functions are applicable here, too
        }
    }

    protected void HasOptional<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasOptional(navigation));
    }

    protected void HasRequired<TNavigation>(Expression<Func<TModel, TNavigation>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasRequired(navigation));
    }

    protected void HasMany<TNavigation>(Expression<Func<TModel, IEnumerable<TNavigation>>> navigation)
        where TNavigation : class
    {
        if (navigation == null) throw new ArgumentNullException(nameof(navigation));
        _configurators.Add(x => x.HasMany(navigation));
    }

    protected void BindFunction(Delegate handler) => _functions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
    protected void BindAction(Delegate handler) => _actions.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

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
    AuthorizationConfig? IEntitySetEndpointSource.Authorization => _authorization;

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
