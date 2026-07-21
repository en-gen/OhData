using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;

namespace OhData;

/// <summary>
/// An explicit converter declared via <c>DeltaMapping.Convert(...)</c>: the entity property name,
/// the converter's input (<c>FromType</c>) and result (<c>ToType</c>) types — both validated at
/// startup against the model/entity properties — and a type-erased converter (boxed model value →
/// boxed entity value).
/// </summary>
internal sealed record DeltaConverterRule(
    string EntityName,
    Type FromType,
    Type ToType,
    Func<object?, object?> Converter);

/// <summary>
/// A single compiled model→entity property rule. <see cref="Converter"/> is <c>null</c> for the
/// automatic (identity / reference-assignable / nullable-wrap) subset — the boxed model value is
/// passed straight into <c>Delta&lt;TEntity&gt;.TrySetPropertyValue</c>, which performs the safe
/// unbox/convert itself.
/// </summary>
internal sealed record CompiledPropertyRule(
    string ModelName,
    string EntityName,
    Func<object?, object?>? Converter,
    Func<object, object?> ModelAccessor);

/// <summary>
/// The immutable, type-erased plan for one <c>(model, entity)</c> mapping. Built once at startup;
/// read-only and safe to share across threads.
/// </summary>
internal sealed class DeltaMappingPlan
{
    public Type ModelType { get; }
    public Type EntityType { get; }

    /// <summary>Entity-side updatable-property allowlist seeded into every produced
    /// <c>Delta&lt;TEntity&gt;</c> — the model's structural properties minus <c>Ignore()</c>d
    /// names, translated through the rename/convert map. Preserves immutability/security
    /// constraints across the DTO→entity boundary.</summary>
    public string[] UpdatableEntityProperties { get; }

    /// <summary>All rules, in model declaration order (used by the model→delta path).</summary>
    public IReadOnlyList<CompiledPropertyRule> Rules { get; }

    /// <summary>Rules keyed by model property name (used by the delta→delta path).</summary>
    public IReadOnlyDictionary<string, CompiledPropertyRule> RulesByModelName { get; }

    public DeltaMappingPlan(Type modelType, Type entityType, string[] updatableEntityProperties,
        IReadOnlyList<CompiledPropertyRule> rules)
    {
        ModelType = modelType;
        EntityType = entityType;
        UpdatableEntityProperties = updatableEntityProperties;
        Rules = rules;
        var byName = new Dictionary<string, CompiledPropertyRule>(StringComparer.Ordinal);
        foreach (var rule in rules) byName[rule.ModelName] = rule;
        RulesByModelName = byName;
    }
}

/// <summary>
/// Resolves conventions and validates every rule for one <c>(model, entity)</c> mapping, failing
/// fast at startup on anything unmapped, unwritable, or type-incompatible.
/// </summary>
internal static class DeltaMappingCompiler
{
    public static DeltaMappingPlan Compile(
        Type modelType,
        Type entityType,
        IReadOnlyDictionary<string, string> renames,
        IReadOnlyCollection<string> ignored,
        IReadOnlyDictionary<string, DeltaConverterRule> converters)
    {
        // All public instance properties (for existence checks); the "in-scope" model surface is
        // the subset with both a public getter and a public setter — a get-only computed property
        // can never appear in a Delta<TModel> and needs no mapping.
        var allModelPropNames = new HashSet<string>(
            PublicInstanceProperties(modelType).Select(p => p.Name), StringComparer.Ordinal);
        var writableModelProps = ByName(PublicInstanceProperties(modelType)
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true }));
        var entityProps = ByName(PublicInstanceProperties(entityType));

        var errors = new List<string>();
        var rules = new List<CompiledPropertyRule>();
        var updatable = new List<string>();

        // Declarations must reference real properties — catch typos/refactor drift at startup.
        foreach (string ign in ignored)
        {
            if (!allModelPropNames.Contains(ign))
                errors.Add($"Ignore() references '{ign}', which is not a property of {modelType.Name}.");
        }
        foreach (string renameSource in renames.Keys)
        {
            if (!writableModelProps.ContainsKey(renameSource))
                errors.Add($"Rename() source '{renameSource}' is not a writable property of {modelType.Name}.");
            // A property declared in both maps is contradictory: the compile loop skips ignored
            // properties before any rename runs, so Ignore() silently wins and the Rename() is
            // dropped. Reject rather than let one quietly no-op (mirrors the Convert()+Rename() check).
            if (ignored.Contains(renameSource))
                errors.Add($"Model property '{renameSource}' is declared in both Ignore() and Rename(); use only one.");
        }
        foreach (string convertSource in converters.Keys)
        {
            if (!writableModelProps.ContainsKey(convertSource))
                errors.Add($"Convert() source '{convertSource}' is not a writable property of {modelType.Name}.");
            // A property declared in both maps is ambiguous: Convert already renames+converts, so a
            // co-declared Rename would be silently dropped. Reject rather than guess.
            if (renames.ContainsKey(convertSource))
                errors.Add($"Model property '{convertSource}' is declared in both Rename() and Convert(); use only Convert() (it maps the target too).");
            // Ignore() wins in the compile loop (skipped before the converter runs), so a co-declared
            // Convert() silently no-ops. Reject rather than let one quietly no-op (mirrors Ignore()+Rename()).
            if (ignored.Contains(convertSource))
                errors.Add($"Model property '{convertSource}' is declared in both Ignore() and Convert(); use only one.");
        }

        // Two model properties targeting one entity property is an ambiguous last-writer-wins map.
        var entityTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modelProp in writableModelProps.Values)
        {
            if (ignored.Contains(modelProp.Name)) continue;

            if (converters.TryGetValue(modelProp.Name, out DeltaConverterRule? conv))
            {
                EntityPropResolution res = ResolveEntityProp(entityProps, conv.EntityName, out PropertyInfo? ep);
                if (res == EntityPropResolution.Missing)
                {
                    errors.Add($"Convert() target '{conv.EntityName}' (from model property '{modelProp.Name}') does not exist on {entityType.Name}.");
                    continue;
                }
                if (res == EntityPropResolution.NotWritable)
                {
                    errors.Add($"Convert() target '{conv.EntityName}' (from model property '{modelProp.Name}') is not writable.");
                    continue;
                }
                // The converter's INPUT type must match the model property so the runtime unbox
                // (TFrom)boxedModelValue succeeds — otherwise a source-selector cast (e.g.
                // d => (long)d.IntProp) passes startup but throws InvalidCastException per request.
                if (!IsAutomaticallyCompatible(modelProp.PropertyType, conv.FromType))
                {
                    errors.Add(
                        $"Convert() for '{modelProp.Name}' takes {FriendlyName(conv.FromType)} but the model " +
                        $"property is {FriendlyName(modelProp.PropertyType)}; the converter's input type must " +
                        "match the model property (do not cast inside the source selector).");
                    continue;
                }
                if (!IsAutomaticallyCompatible(conv.ToType, ep!.PropertyType))
                {
                    errors.Add(
                        $"Convert() for '{modelProp.Name}' produces {FriendlyName(conv.ToType)} but entity " +
                        $"property '{conv.EntityName}' is {FriendlyName(ep.PropertyType)}; the converter's " +
                        "result type must be assignable to the entity property.");
                    continue;
                }
                if (!entityTargets.Add(conv.EntityName))
                {
                    errors.Add($"Entity property '{conv.EntityName}' is targeted by more than one model property.");
                    continue;
                }
                rules.Add(new CompiledPropertyRule(modelProp.Name, conv.EntityName, conv.Converter, CompileAccessor(modelProp)));
                updatable.Add(conv.EntityName);
                continue;
            }

            // Enforce the "scalars/structural only" invariant. A navigation-collection property
            // reaching the convention path (plain or renamed — neither carries a converter, so both
            // would copy the collection by identity) must not be auto-written onto Delta<TEntity>.
            // Convert()'d properties never reach here (handled above), so an explicit Convert() is
            // the sole opt-in — as is Ignore() (skipped above). Same-typed navigations otherwise pass
            // IsAutomaticallyCompatible's identity check and silently land in UpdatableEntityProperties.
            if (IsNavigationCollectionType(modelProp.PropertyType))
            {
                errors.Add(
                    $"Model property '{modelProp.Name}' is a navigation/collection type " +
                    $"({FriendlyName(modelProp.PropertyType)}); delta mapping is scalars/structural only " +
                    "— Ignore() it or map it explicitly with Convert().");
                continue;
            }

            string entityName = renames.TryGetValue(modelProp.Name, out string? renamed) ? renamed : modelProp.Name;
            bool wasRenamed = renamed is not null;
            EntityPropResolution cres = ResolveEntityProp(entityProps, entityName, out PropertyInfo? entityProp);
            if (cres == EntityPropResolution.Missing)
            {
                errors.Add(wasRenamed
                    ? $"Rename() target '{entityName}' (from model property '{modelProp.Name}') does not exist on {entityType.Name}."
                    : $"Model property '{modelProp.Name}' ({FriendlyName(modelProp.PropertyType)}) has no entity " +
                      $"counterpart named '{entityName}' on {entityType.Name}. Add a .Rename(...), .Convert(...), " +
                      "or .Ignore(...) for it.");
                continue;
            }
            if (cres == EntityPropResolution.NotWritable)
            {
                errors.Add($"Entity property '{entityName}' (mapped from model property '{modelProp.Name}') is not writable.");
                continue;
            }

            if (!IsAutomaticallyCompatible(modelProp.PropertyType, entityProp!.PropertyType))
            {
                errors.Add(
                    $"Model property '{modelProp.Name}' ({FriendlyName(modelProp.PropertyType)}) cannot be " +
                    $"mapped to entity property '{entityName}' ({FriendlyName(entityProp.PropertyType)}) by " +
                    "convention. Supply an explicit .Convert(...) converter.");
                continue;
            }

            if (!entityTargets.Add(entityName))
            {
                errors.Add($"Entity property '{entityName}' is targeted by more than one model property.");
                continue;
            }

            rules.Add(new CompiledPropertyRule(modelProp.Name, entityName, Converter: null, CompileAccessor(modelProp)));
            updatable.Add(entityName);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"OhData: delta mapping ({modelType.Name} → {entityType.Name}) is invalid:" +
                string.Concat(errors.Select(e => "\n  - " + e)));
        }

        return new DeltaMappingPlan(modelType, entityType, updatable.ToArray(), rules);
    }

    private enum EntityPropResolution { Ok, Missing, NotWritable }

    private static EntityPropResolution ResolveEntityProp(
        Dictionary<string, PropertyInfo> entityProps, string entityName, out PropertyInfo? entityProp)
    {
        if (!entityProps.TryGetValue(entityName, out entityProp))
            return EntityPropResolution.Missing;
        if (entityProp.SetMethod is not { IsPublic: true })
        {
            entityProp = null;
            return EntityPropResolution.NotWritable;
        }
        return EntityPropResolution.Ok;
    }

    /// <summary>
    /// The strict, safe automatic-conversion subset (never <c>Convert.ChangeType</c>): identity,
    /// reference-assignable (<c>target.IsAssignableFrom(source)</c>), and nullable-wrap
    /// (<c>T → T?</c>). Notably <c>T? → T</c> is excluded (null has no target) and value-type
    /// widening such as <c>int → long</c> is excluded — both require an explicit converter.
    /// </summary>
    internal static bool IsAutomaticallyCompatible(Type source, Type target)
    {
        if (target.IsAssignableFrom(source)) return true; // identity + reference-assignable
        Type? targetUnderlying = Nullable.GetUnderlyingType(target);
        return targetUnderlying is not null && targetUnderlying == source; // nullable-wrap T -> T?
    }

    /// <summary>
    /// True when <paramref name="type"/> is a navigation-collection type — one OhData would model as a
    /// HasMany navigation, which delta mapping (scalars/structural only) must never auto-write. A type is
    /// flagged only when it is a collection (<see cref="System.Collections.IEnumerable"/>) whose ELEMENT
    /// type is a class/entity (a related-entity or complex-class write, e.g. <c>List&lt;Order&gt;</c>).
    /// A collection of a scalar/structural element — primitive, enum, <c>Guid</c>, <c>DateTime</c>,
    /// <c>DateTimeOffset</c>, <c>decimal</c>, nullable value type, or the collection-shaped scalars
    /// <c>string</c>/<c>byte[]</c> — is STRUCTURAL and must stay auto-mappable (<c>List&lt;int&gt;</c>,
    /// <c>string[]</c>, <c>List&lt;DateTime&gt;</c>, Collection(Edm.PrimitiveType)). The two collection-shaped
    /// scalars <c>string</c> (an <c>IEnumerable&lt;char&gt;</c>) and <c>byte[]</c> (OData <c>Edm.Binary</c>)
    /// are never collections here. Single, non-collection reference types are deliberately NOT flagged:
    /// reflection cannot distinguish an EDM complex (structural) type from an entity (navigation) type,
    /// and the documented conversion policy blesses reference-assignable single references as automatic,
    /// so they remain mappable and an explicit Convert()/Ignore() is available if one is a navigation.
    /// A bare non-generic <see cref="System.Collections.IEnumerable"/> (unknown element type) is flagged
    /// conservatively. Nullable value types (never collections) are unwrapped defensively for robustness.
    /// </summary>
    internal static bool IsNavigationCollectionType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return false;   // IEnumerable<char>, but a scalar
        if (t == typeof(byte[])) return false;   // Edm.Binary scalar
        if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(t)) return false;
        return !IsStructuralScalar(EnumerableElementType(t));
    }

    /// <summary>
    /// The element judgment reused for collection classification: <c>string</c>/<c>byte[]</c> and every
    /// non-class type (primitive, enum, <c>Guid</c>, <c>DateTime</c>/<c>DateTimeOffset</c>, <c>decimal</c>,
    /// other structs, and nullable value types once unwrapped) are structural scalars; a class element is
    /// an entity/complex reference. Mirrors the "scalars/structural only" single-property intent.
    /// </summary>
    private static bool IsStructuralScalar(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(byte[])) return true;
        return !t.IsClass;
    }

    /// <summary>
    /// The element type of an <see cref="System.Collections.IEnumerable"/>: the array element for arrays,
    /// the <c>T</c> of the first <c>IEnumerable&lt;T&gt;</c> the type is or implements otherwise, and
    /// <c>object</c> for a bare non-generic <see cref="System.Collections.IEnumerable"/>.
    /// </summary>
    private static Type EnumerableElementType(Type enumerableType)
    {
        if (enumerableType.IsArray) return enumerableType.GetElementType()!;

        if (enumerableType.IsGenericType && enumerableType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return enumerableType.GetGenericArguments()[0];

        foreach (Type i in enumerableType.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        }
        return typeof(object); // bare non-generic IEnumerable — unknown element, flag conservatively
    }

    private static PropertyInfo[] PublicInstanceProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0) // skip indexers
            .ToArray();

    // Keys properties by name, tolerating `new`-shadowed members (GetProperties returns both the
    // base and derived declaration) by keeping the most-derived one.
    private static Dictionary<string, PropertyInfo> ByName(IEnumerable<PropertyInfo> props)
    {
        var dict = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (PropertyInfo p in props)
        {
            if (!dict.TryGetValue(p.Name, out PropertyInfo? existing) || IsMoreDerived(p, existing))
                dict[p.Name] = p;
        }
        return dict;
    }

    private static bool IsMoreDerived(PropertyInfo candidate, PropertyInfo current) =>
        candidate.DeclaringType != current.DeclaringType &&
        current.DeclaringType!.IsAssignableFrom(candidate.DeclaringType);

    private static Func<object, object?> CompileAccessor(PropertyInfo prop)
    {
        ParameterExpression param = Expression.Parameter(typeof(object), "m");
        UnaryExpression typed = Expression.Convert(param, prop.DeclaringType!);
        MemberExpression access = Expression.Property(typed, prop);
        UnaryExpression boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
    }

    private static string FriendlyName(Type type)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        return underlying is not null ? underlying.Name + "?" : type.Name;
    }
}

/// <summary>
/// Cross-registration accumulator of the <see cref="DeltaProfile"/> types added on an
/// <see cref="IServiceCollection"/>. Registered once as an instance singleton (like the entity
/// profile tracker) so <c>AddDeltaProfile</c> can mutate it before the container is built, and the
/// single <see cref="IDeltaFactory"/> can read every registration's profiles in one pass.
/// </summary>
internal sealed class DeltaProfileRegistry
{
    internal readonly List<Type> Types = new();
}

/// <summary>
/// Immutable, thread-safe implementation of <see cref="IDeltaFactory"/>. Holds compiled plans keyed
/// by <c>(TModel, TEntity)</c>; every <c>Create</c> allocates a fresh <c>Delta&lt;TEntity&gt;</c>
/// with no shared mutable state.
/// </summary>
internal sealed class DeltaFactory : IDeltaFactory
{
    private readonly IReadOnlyDictionary<(Type Model, Type Entity), DeltaMappingPlan> _plans;

    internal DeltaFactory(IReadOnlyDictionary<(Type, Type), DeltaMappingPlan> plans) => _plans = plans;

    /// <summary>
    /// Startup construction: resolves every registered <see cref="DeltaProfile"/>, compiles and
    /// validates its mappings once, and fails fast on any invalid or duplicated mapping.
    /// </summary>
    internal static DeltaFactory Build(IServiceProvider serviceProvider, DeltaProfileRegistry registry)
    {
        var plans = new Dictionary<(Type, Type), DeltaMappingPlan>();
        using IServiceScope scope = serviceProvider.CreateScope();
        foreach (Type profileType in registry.Types)
        {
            var profile = (DeltaProfile)scope.ServiceProvider.GetRequiredService(profileType);
            foreach (IDeltaMappingSource mapping in profile.Mappings)
            {
                DeltaMappingPlan plan = mapping.Compile();
                if (!plans.TryAdd((plan.ModelType, plan.EntityType), plan))
                {
                    throw new InvalidOperationException(
                        $"OhData: duplicate delta mapping for ({plan.ModelType.Name} → {plan.EntityType.Name}). " +
                        "A (model, entity) pair may be declared only once across all DeltaProfiles.");
                }
            }
        }
        return new DeltaFactory(plans);
    }

    /// <inheritdoc />
    public Delta<TEntity> Create<TModel, TEntity>(Delta<TModel> delta)
        where TModel : class
        where TEntity : class
    {
        if (delta is null) throw new ArgumentNullException(nameof(delta));
        DeltaMappingPlan plan = Resolve(typeof(TModel), typeof(TEntity));
        var entityDelta = new Delta<TEntity>(typeof(TEntity), plan.UpdatableEntityProperties);

        foreach (string modelName in delta.GetChangedPropertyNames())
        {
            if (!plan.RulesByModelName.TryGetValue(modelName, out CompiledPropertyRule? rule)) continue;
            delta.TryGetPropertyValue(modelName, out object? value);
            if (rule.Converter is not null) value = rule.Converter(value);
            entityDelta.TrySetPropertyValue(rule.EntityName, value);
        }

        return entityDelta;
    }

    /// <inheritdoc />
    public Delta<TEntity> Create<TModel, TEntity>(TModel model)
        where TModel : class
        where TEntity : class
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        DeltaMappingPlan plan = Resolve(typeof(TModel), typeof(TEntity));
        var entityDelta = new Delta<TEntity>(typeof(TEntity), plan.UpdatableEntityProperties);

        foreach (CompiledPropertyRule rule in plan.Rules)
        {
            object? value = rule.ModelAccessor(model);
            if (rule.Converter is not null) value = rule.Converter(value);
            entityDelta.TrySetPropertyValue(rule.EntityName, value);
        }

        return entityDelta;
    }

    private DeltaMappingPlan Resolve(Type modelType, Type entityType)
    {
        if (_plans.TryGetValue((modelType, entityType), out DeltaMappingPlan? plan)) return plan;
        throw new InvalidOperationException(
            $"OhData: no delta mapping registered for ({modelType.Name} → {entityType.Name}). " +
            $"Declare it in a DeltaProfile with For<{modelType.Name}, {entityType.Name}>() and register the " +
            "profile via AddDeltaProfile<T>() or an assembly scan.");
    }
}
