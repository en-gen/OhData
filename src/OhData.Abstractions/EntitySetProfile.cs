using System.Linq.Expressions;
using Microsoft.OData.ModelBuilder;

namespace OhData.Abstractions;

public abstract class EntitySetProfile<TKey, TModel> : IVisitModelBuilder
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

    // TODO: how to do Get w/o ODataQueryOptions<T>?

    protected Func<TKey, CancellationToken, Task<TModel>>? GetById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Put = null;
    protected Func<TKey, TModel, CancellationToken, Task<TModel>>? PutById = null;

    protected Func<TModel, CancellationToken, Task<TModel>>? Post = null;

    // TODO: is patch feasible w/o Delta<T>?

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
    /// Hands over full configuration control.  If this method is overridden, you are ejecting
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
            .GetMethod(nameof(AdvancedConfigure), new[] {typeof(EntitySetConfiguration<TModel>)})
            .DeclaringType;
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
                var entityFunctionParam = entityFunction.Parameter(param.ParameterType, param.Name);

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
                var entityActionParam = entityAction.Parameter(param.ParameterType, param.Name);
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
}