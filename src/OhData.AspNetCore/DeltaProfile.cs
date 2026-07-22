using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData;

/// <summary>
/// Base class for declaring dependency-free delta mappings between a DTO/view model and its
/// backing entity, in the same spirit as an <c>EntitySetProfile</c> (and familiar to AutoMapper
/// users). Derive from this class and call <see cref="For{TModel,TEntity}"/> once per
/// <c>(model, entity)</c> pair in the constructor. Register the profile via
/// <c>OhDataBuilder.AddDeltaProfile&lt;TProfile&gt;()</c> or an assembly scan; the framework
/// discovers, compiles, and validates every mapping once at startup and exposes them through the
/// injected <see cref="IDeltaFactory"/>.
/// </summary>
/// <remarks>
/// A <see cref="DeltaProfile"/> declares only how a model's <em>scalar/structural</em> properties
/// map onto an entity's. Navigation writes and nested-object mapping are out of scope by design
/// (that is where a full object-mapper begins). There is no <c>Build()</c> or finalizer — the
/// startup scan is the finalizer, exactly like AutoMapper's <c>CreateMap().ForMember()</c>.
/// </remarks>
public abstract class DeltaProfile
{
    private readonly List<IDeltaMappingSource> _mappings = new();

    /// <summary>The mappings declared by this profile, in declaration order.</summary>
    internal IReadOnlyList<IDeltaMappingSource> Mappings => _mappings;

    /// <summary>
    /// Eagerly registers a mapping from <typeparamref name="TModel"/> to
    /// <typeparamref name="TEntity"/> into this profile and returns a mutable fluent config.
    /// In the common case (a DTO that mirrors its entity — same names, same types) no further
    /// declaration is needed; use <see cref="DeltaMapping{TModel,TEntity}.Rename"/>,
    /// <see cref="DeltaMapping{TModel,TEntity}.Ignore"/>, and
    /// <see cref="DeltaMapping{TModel,TEntity}.Convert{TFrom,TTo}"/> to declare only the
    /// divergences.
    /// </summary>
    /// <typeparam name="TModel">The DTO / view-model type (the delta's <c>Delta&lt;TModel&gt;</c>).</typeparam>
    /// <typeparam name="TEntity">The backing entity type (the produced <c>Delta&lt;TEntity&gt;</c>).</typeparam>
    protected DeltaMapping<TModel, TEntity> For<TModel, TEntity>()
        where TModel : class
        where TEntity : class
    {
        var mapping = new DeltaMapping<TModel, TEntity>();
        _mappings.Add(mapping);
        return mapping;
    }
}

/// <summary>
/// Non-generic view of a <see cref="DeltaMapping{TModel,TEntity}"/> used by the startup compiler
/// to build the immutable, type-erased mapping plan.
/// </summary>
internal interface IDeltaMappingSource
{
    Type ModelType { get; }
    Type EntityType { get; }

    /// <summary>Resolves conventions, validates every rule, and compiles the plan. Throws
    /// <see cref="InvalidOperationException"/> on any unmapped/unwritable/incompatible property.</summary>
    DeltaMappingPlan Compile();
}

/// <summary>
/// Mutable fluent configuration for a single <c>(TModel, TEntity)</c> delta mapping. Obtained from
/// <see cref="DeltaProfile.For{TModel,TEntity}"/>. Each method mutates the mapping in place and
/// returns <c>this</c> for chaining. All selectors must be direct property accesses
/// (e.g. <c>x =&gt; x.Name</c>).
/// </summary>
/// <typeparam name="TModel">The DTO / view-model type.</typeparam>
/// <typeparam name="TEntity">The backing entity type.</typeparam>
public sealed class DeltaMapping<TModel, TEntity> : IDeltaMappingSource
    where TModel : class
    where TEntity : class
{
    // model property name -> entity property name (rename divergences)
    private readonly Dictionary<string, string> _renames = new(StringComparer.Ordinal);
    // model property names excluded from the mapping (DTO-only, no entity target)
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    // model property name -> explicit converter rule
    private readonly Dictionary<string, DeltaConverterRule> _converters = new(StringComparer.Ordinal);

    Type IDeltaMappingSource.ModelType => typeof(TModel);
    Type IDeltaMappingSource.EntityType => typeof(TEntity);

    /// <summary>
    /// Maps a model property to a differently-named entity property. The two properties must be
    /// convention-compatible (same type, reference-assignable, or nullable-wrap); use
    /// <see cref="Convert{TFrom,TTo}"/> when the types also diverge.
    /// </summary>
    /// <param name="from">Model property selector, e.g. <c>d =&gt; d.DisplayName</c>.</param>
    /// <param name="to">Entity property selector, e.g. <c>e =&gt; e.Name</c>.</param>
    public DeltaMapping<TModel, TEntity> Rename(
        Expression<Func<TModel, object?>> from,
        Expression<Func<TEntity, object?>> to)
    {
        if (from is null) throw new ArgumentNullException(nameof(from));
        if (to is null) throw new ArgumentNullException(nameof(to));
        string fromName = DeltaExpressionHelper.GetMemberName(from, nameof(from));
        string toName = DeltaExpressionHelper.GetMemberName(to, nameof(to));
        _renames[fromName] = toName;
        return this;
    }

    /// <summary>
    /// Excludes a model property from the mapping. Ignored properties never propagate to the
    /// entity delta and are excluded from the translated updatable-property allowlist. Use for
    /// DTO-only / computed properties that have no entity target.
    /// </summary>
    /// <param name="property">Model property selector, e.g. <c>d =&gt; d.ComputedTotal</c>.</param>
    public DeltaMapping<TModel, TEntity> Ignore(Expression<Func<TModel, object?>> property)
    {
        if (property is null) throw new ArgumentNullException(nameof(property));
        _ignored.Add(DeltaExpressionHelper.GetMemberName(property, nameof(property)));
        return this;
    }

    /// <summary>
    /// Maps a model property onto an entity property through an explicit converter. Required for
    /// every conversion outside the automatic subset (narrowing, <c>int→long</c> widening,
    /// enum↔string, <c>T?→T</c>, etc.) — the framework never guesses. Also renames when the two
    /// selectors name different properties.
    /// </summary>
    /// <typeparam name="TFrom">The model property's type.</typeparam>
    /// <typeparam name="TTo">The entity property's type.</typeparam>
    /// <param name="from">Model property selector, e.g. <c>d =&gt; d.Status</c>.</param>
    /// <param name="to">Entity property selector, e.g. <c>e =&gt; e.StatusCode</c>.</param>
    /// <param name="converter">Pure function converting the model value to the entity value.</param>
    public DeltaMapping<TModel, TEntity> Convert<TFrom, TTo>(
        Expression<Func<TModel, TFrom>> from,
        Expression<Func<TEntity, TTo>> to,
        Func<TFrom, TTo> converter)
    {
        if (from is null) throw new ArgumentNullException(nameof(from));
        if (to is null) throw new ArgumentNullException(nameof(to));
        if (converter is null) throw new ArgumentNullException(nameof(converter));
        string fromName = DeltaExpressionHelper.GetMemberName(from, nameof(from));
        string toName = DeltaExpressionHelper.GetMemberName(to, nameof(to));
        // Box/unbox at the type-erased boundary. A non-nullable value-typed TFrom can never be
        // null here (its delta value is always present), and a nullable/reference TFrom casts
        // from null cleanly — so the unchecked cast is safe for every reachable value.
        Func<object?, object?> wrapped = v => (object?)converter((TFrom)v!);
        _converters[fromName] = new DeltaConverterRule(toName, typeof(TFrom), typeof(TTo), wrapped);
        return this;
    }

    DeltaMappingPlan IDeltaMappingSource.Compile() =>
        DeltaMappingCompiler.Compile(typeof(TModel), typeof(TEntity), _renames, _ignored, _converters);
}

/// <summary>
/// Shared expression-parsing helper for delta declarations. Accepts only a direct property access
/// on the lambda parameter (after stripping a boxing <c>Convert</c>); rejects method calls,
/// computed values, and nested access with a clear message.
/// </summary>
internal static class DeltaExpressionHelper
{
    internal static string GetMemberName(LambdaExpression expression, string argName)
    {
        Expression body = expression.Body;
        if (body is UnaryExpression unary &&
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is MemberExpression member &&
            member.Expression is ParameterExpression &&
            member.Member is PropertyInfo)
        {
            return member.Member.Name;
        }

        throw new ArgumentException(
            "Delta mapping selectors must be a direct property access on the lambda parameter " +
            "(e.g. x => x.Name). Method calls, computed values, and nested access such as " +
            "x => x.Category.Name are not supported.",
            argName);
    }
}
