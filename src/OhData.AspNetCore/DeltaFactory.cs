using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
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
    /// <param name="modelType">The DTO / view-model type.</param>
    /// <param name="entityType">The backing entity type.</param>
    /// <param name="renames">Model property name → entity property name.</param>
    /// <param name="ignored">Model property names excluded from the mapping.</param>
    /// <param name="converters">Model property name → explicit converter rule.</param>
    /// <param name="trackedEntityProperties">
    /// Reads the property names <c>Delta&lt;TEntity&gt;</c> will actually track, off a real
    /// <c>Delta&lt;TEntity&gt;</c> (see <see cref="TrackedEntityProperties{TEntity}"/>). Supplied
    /// as a delegate because the compiler is type-erased while the probe needs the closed generic.
    /// Invoked only after <see cref="DescribeUnconstructableEntity"/> has cleared the entity type —
    /// constructing the probe runs the entity's parameterless constructor.
    /// </param>
    public static DeltaMappingPlan Compile(
        Type modelType,
        Type entityType,
        IReadOnlyDictionary<string, string> renames,
        IReadOnlyCollection<string> ignored,
        IReadOnlyDictionary<string, DeltaConverterRule> converters,
        Func<IReadOnlyCollection<string>> trackedEntityProperties)
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

        // #480: Delta<T>.Reset instantiates the entity with Activator.CreateInstance on EVERY
        // Create call, so an entity type it cannot instantiate is a guaranteed per-request 500.
        // Checked before the probe below, which would otherwise be the thing that threw.
        string? unconstructable = DescribeUnconstructableEntity(entityType);
        if (unconstructable is not null) errors.Add(unconstructable);

        // #479: the entity-side admission rules are Delta<T>'s, not ours — a target outside them
        // is absent from its property surface, TrySetPropertyValue answers false, and the write
        // vanishes. Rather than transcribe those rules (they are Microsoft's and can change), read
        // the resulting set off a real Delta<TEntity>. Null when the type is not constructable:
        // the mapping is already failing, and the remaining checks still produce useful errors.
        HashSet<string>? tracked = null;
        if (unconstructable is null)
        {
            try
            {
                tracked = new HashSet<string>(trackedEntityProperties(), StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Delta<{entityType.Name}> could not be constructed for validation: " +
                    $"{ex.GetType().Name}: {ex.Message}. Every Create call constructs one, so this " +
                    "mapping would fail on every request.");
            }
        }

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
                EntityPropResolution res = ResolveEntityProp(
                    entityProps, tracked, entityType, conv.EntityName, out PropertyInfo? ep, out string? convReason);
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
                if (res == EntityPropResolution.NotTracked)
                {
                    errors.Add(
                        $"Convert() target '{conv.EntityName}' (from model property '{modelProp.Name}') is not " +
                        $"tracked by Delta<{entityType.Name}> — {convReason}. A write to it would be silently " +
                        "discarded at runtime; Ignore() the model property, or target a tracked entity property.");
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
            EntityPropResolution cres = ResolveEntityProp(
                entityProps, tracked, entityType, entityName, out PropertyInfo? entityProp, out string? untrackedReason);
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
            if (cres == EntityPropResolution.NotTracked)
            {
                errors.Add(
                    $"Entity property '{entityName}' (mapped from model property '{modelProp.Name}') is not " +
                    $"tracked by Delta<{entityType.Name}> — {untrackedReason}. A write to it would be silently " +
                    "discarded at runtime; Ignore() the model property, or map it onto a tracked entity " +
                    "property with .Rename(...) / .Convert(...).");
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

    private enum EntityPropResolution { Ok, Missing, NotWritable, NotTracked }

    private static EntityPropResolution ResolveEntityProp(
        Dictionary<string, PropertyInfo> entityProps,
        HashSet<string>? tracked,
        Type entityType,
        string entityName,
        out PropertyInfo? entityProp,
        out string? untrackedReason)
    {
        untrackedReason = null;
        if (!entityProps.TryGetValue(entityName, out entityProp))
            return EntityPropResolution.Missing;

        // OhData's own, deliberately STRICTER rule, kept ahead of the Delta<T> check so its
        // clearer message wins. Delta<T> also admits a setter-less COLLECTION property (it
        // clears-and-refills the existing instance); OhData does not auto-write those and says
        // so at startup instead of at runtime. Widening to match is tracked as #488 item 4 — the
        // divergence below is the one that loses data, and it is only ever narrowing.
        if (entityProp.SetMethod is not { IsPublic: true })
        {
            entityProp = null;
            return EntityPropResolution.NotWritable;
        }

        // #479: everything Delta<T> refuses to track. Decided by the set read off Delta<T>
        // itself, never by a transcription of its predicate.
        if (tracked is not null && !tracked.Contains(entityName))
        {
            untrackedReason = DescribeWhyDeltaSkips(entityType, entityProp);
            entityProp = null;
            return EntityPropResolution.NotTracked;
        }

        return EntityPropResolution.Ok;
    }

    /// <summary>
    /// The exact set of property names a <c>Delta&lt;TEntity&gt;</c> tracks — read off a real one
    /// rather than re-implementing <c>Delta&lt;T&gt;.InitializeProperties</c>'s predicate
    /// (<c>DeltaOfT.cs:699-705</c>), which is Microsoft's and free to change. Constructed with a
    /// null <c>updatableProperties</c>, for which <c>UpdatableProperties</c> is exactly the
    /// internal <c>_allProperties</c> key set (<c>DeltaOfT.cs:706-715</c>) that
    /// <c>TrySetPropertyValue</c> tests membership in. Runs the entity's public parameterless
    /// constructor, so callers must clear <see cref="DescribeUnconstructableEntity"/> first.
    /// </summary>
    /// <typeparam name="TEntity">The backing entity type.</typeparam>
    internal static IReadOnlyCollection<string> TrackedEntityProperties<TEntity>() where TEntity : class =>
        new Delta<TEntity>(typeof(TEntity)).UpdatableProperties.ToArray();

    /// <summary>
    /// #480: <c>Delta&lt;T&gt;.Reset</c> instantiates the entity with
    /// <c>Activator.CreateInstance</c> (<c>DeltaOfT.cs:688</c>) on every <c>Create</c> call.
    /// Returns the startup error for an entity type that cannot satisfy it, or <c>null</c>.
    /// </summary>
    /// <param name="entityType">The backing entity type.</param>
    private static string? DescribeUnconstructableEntity(Type entityType)
    {
        string tail =
            $"Delta<{entityType.Name}> instantiates the entity with Activator.CreateInstance on every " +
            "Create call, so the mapped entity type must be a concrete class with a PUBLIC parameterless " +
            "constructor.";

        if (entityType.IsInterface)
            return $"Entity type {entityType.Name} is an interface. {tail} Map to the concrete type instead.";
        if (entityType.IsAbstract)
            return $"Entity type {entityType.Name} is abstract. {tail} Map to a concrete derived type instead.";
        if (entityType.GetConstructor(Type.EmptyTypes) is null)
        {
            return
                $"Entity type {entityType.Name} has no public parameterless constructor. {tail} A protected " +
                "or private parameterless constructor beside a public parameterized one (the usual EF Core " +
                "shape) and a positional record both fail this — add a public parameterless constructor.";
        }
        return null;
    }

    /// <summary>
    /// Advisory diagnosis only — the DECISION is the set read from <c>Delta&lt;T&gt;</c>. Mirrors
    /// the precedence in <c>Delta&lt;T&gt;.IsIgnoredProperty</c> (<c>DeltaOfT.cs:722-741</c>) so
    /// the startup message names the attribute the developer has to remove. Falls back to a
    /// generic sentence if Microsoft's rules ever grow a case this does not know about.
    /// </summary>
    /// <param name="entityType">The entity type whose delta refused the property.</param>
    /// <param name="prop">The refused property.</param>
    private static string DescribeWhyDeltaSkips(Type entityType, PropertyInfo prop)
    {
        if (prop.GetCustomAttributes(typeof(NotMappedAttribute), inherit: true).Length > 0)
            return "it is marked [NotMapped]";
        // Delta<T> reads the [DataContract] marker off the ENTITY type, not the declaring type.
        if (entityType.GetCustomAttributes(typeof(DataContractAttribute), inherit: true).Length > 0 &&
            prop.GetCustomAttributes(typeof(DataMemberAttribute), inherit: true).Length == 0)
        {
            return $"{entityType.Name} is a [DataContract] type and this property is not marked [DataMember]";
        }
        if (prop.GetCustomAttributes(typeof(IgnoreDataMemberAttribute), inherit: true).Length > 0)
            return "it is marked [IgnoreDataMember]";
        if (prop.GetMethod is not { IsPublic: true })
            return "it has no public getter";
        return "Microsoft.AspNetCore.OData excludes it from the delta's property surface";
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

        Type? generic = enumerableType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        // bare non-generic IEnumerable — unknown element, flag conservatively
        return generic is null ? typeof(object) : generic.GetGenericArguments()[0];
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
            if (!entityDelta.TrySetPropertyValue(rule.EntityName, value))
                throw RejectedWrite(typeof(TModel), typeof(TEntity), rule, value);
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
            if (!entityDelta.TrySetPropertyValue(rule.EntityName, value))
                throw RejectedWrite(typeof(TModel), typeof(TEntity), rule, value);
        }

        return entityDelta;
    }

    /// <summary>
    /// #479: <c>Delta&lt;T&gt;.TrySetPropertyValue</c> returning <c>false</c> means the write was
    /// NOT applied. Every reachable cause is a mapping the startup compiler is supposed to have
    /// rejected, so this is an invariant assertion, not an error path — and discarding the bool
    /// (which is what shipped) turned a lost write into a silent 200/204. Throwing surfaces it as
    /// a 500 in the OData error envelope, which is strictly better than persisting a partial write
    /// under a success status.
    /// </summary>
    private static InvalidOperationException RejectedWrite(
        Type modelType, Type entityType, CompiledPropertyRule rule, object? value) =>
        new(
            $"OhData: delta mapping ({modelType.Name} → {entityType.Name}) produced a write that " +
            $"Delta<{entityType.Name}> rejected, so it was NOT applied: model property " +
            $"'{rule.ModelName}' → entity property '{rule.EntityName}' " +
            $"(value type: {(value is null ? "null" : value.GetType().Name)}). Startup validation " +
            "should make this unreachable — please report it with the mapping declaration.");

    private DeltaMappingPlan Resolve(Type modelType, Type entityType)
    {
        if (_plans.TryGetValue((modelType, entityType), out DeltaMappingPlan? plan)) return plan;
        throw new InvalidOperationException(
            $"OhData: no delta mapping registered for ({modelType.Name} → {entityType.Name}). " +
            $"Declare it in a DeltaProfile with For<{modelType.Name}, {entityType.Name}>() and register the " +
            "profile via AddDeltaProfile<T>() or an assembly scan.");
    }
}
