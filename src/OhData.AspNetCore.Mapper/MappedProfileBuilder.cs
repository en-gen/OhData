using System;
using System.Collections.Generic;

namespace OhData.AspNetCore.Mapper;

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
