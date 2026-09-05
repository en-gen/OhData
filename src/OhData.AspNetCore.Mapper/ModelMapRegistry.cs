using System;
using System.Collections.Generic;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Resolves the <see cref="ModelMap"/> for a model type.
/// </summary>
/// <remarks>
/// A nested lambda (<c>d =&gt; d.Tags.Any(t =&gt; t.Label eq 'x')</c>) and a nested <c>$expand</c>
/// both have to substitute through the <i>target's</i> own bindings. Resolving them from one registry
/// rather than repeating the target's correspondences at every use site is the same "one site, N
/// consumers" rule the core enforces: two transcriptions of one correspondence is the defect shape
/// this repository has hit repeatedly.
/// </remarks>
public sealed class ModelMapRegistry
{
    private readonly Dictionary<Type, ModelMap> _byModelType = new();

    /// <summary>Adds a map, refusing a second map for the same model type.</summary>
    public ModelMapRegistry Add(ModelMap map)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));

        // Refused rather than last-write-wins: with two maps for one model type the wire shape would
        // depend on registration order, which is the defect #546 fixed for authorization rules.
        if (_byModelType.TryGetValue(map.ModelType, out ModelMap? existing))
        {
            throw new InvalidOperationException(
                $"Two maps are registered for model type '{map.ModelType.Name}' " +
                $"(from '{existing.EntityType.Name}' and from '{map.EntityType.Name}'). " +
                $"A model type may correspond to exactly one entity type.");
        }

        _byModelType.Add(map.ModelType, map);
        return this;
    }

    /// <summary>The map for a model type, or <c>null</c>.</summary>
    public ModelMap? Find(Type modelType) =>
        _byModelType.TryGetValue(modelType, out ModelMap? map) ? map : null;

    /// <summary>Every registered map.</summary>
    public IReadOnlyCollection<ModelMap> Maps => _byModelType.Values;

    /// <summary>The resolver shape <see cref="ModelToEntityRewriter"/> takes.</summary>
    public Func<Type, ModelMap?> Resolver => Find;
}
