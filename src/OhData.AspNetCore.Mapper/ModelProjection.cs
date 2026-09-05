using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Builds the entity-to-model projection from a <see cref="ModelMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// The projection carries the <b>scalar</b> members only. Navigations are deliberately left unset:
/// on the OhData read path a navigation reaches the wire through the collection pipeline's own
/// <c>$expand</c> handling — the profile's navigation delegates — and a navigation the request did
/// not expand is omitted from the payload regardless (JSON Format §4.5.1). Populating one here would
/// therefore fetch data no response can carry, on every request.
/// </para>
/// <para>
/// A member path (<c>o =&gt; o.Category.Name</c>) is guarded at every intermediate reference. In SQL
/// a null reference yields a null column; in memory, which is where this projection runs, the same
/// expression throws. The guard is what makes the two agree, so a row that a database-side
/// <c>$filter</c> over the same path matched is a row this projection can also render.
/// </para>
/// </remarks>
public static class ModelProjection
{
    /// <summary>
    /// The projection as a lambda over the entity, for composing onto a queryable.
    /// </summary>
    /// <remarks>
    /// Composed onto the entity query rather than applied after materialisation, so the provider
    /// reads exactly the columns the model needs and a member reached through a reference becomes a
    /// JOIN. Mapping after materialisation cannot do that: EF returns an entity with its references
    /// unloaded, so every path member would render null unless the whole graph were fetched first
    /// (measured -- it is what the conformance oracle caught on its first run).
    /// </remarks>
    public static LambdaExpression BuildLambda(ModelMap map)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));

        ParameterExpression entity = Expression.Parameter(map.EntityType, "e");
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(map.EntityType, map.ModelType),
            BuildBody(map, entity),
            entity);
    }

    /// <summary>Compiles the projection for one map, for an in-memory source.</summary>
    public static Func<object, object> CompileUntyped(ModelMap map)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));

        ParameterExpression boxed = Expression.Parameter(typeof(object), "o");
        UnaryExpression entity = Expression.Convert(boxed, map.EntityType);
        Expression body = Expression.Convert(BuildBody(map, entity), typeof(object));
        return Expression.Lambda<Func<object, object>>(body, boxed).Compile();
    }

    /// <summary>The <c>new TModel { … }</c> body over an entity-typed expression.</summary>
    public static Expression BuildBody(ModelMap map, Expression entity)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (entity is null) throw new ArgumentNullException(nameof(entity));

        var rewriter = new ModelToEntityRewriter(map);
        var bindings = new List<MemberBinding>();

        foreach (ModelMemberBinding binding in map.Bindings)
        {
            if (binding.Kind is ModelBindingKind.Ignored || binding.IsNavigation) continue;

            Expression? value = rewriter.BindingFor(binding, entity);
            if (value is null) continue;

            Type target = MemberType(binding.ModelMember);
            bindings.Add(Expression.Bind(binding.ModelMember, Coerce(NullSafe(value), target)));
        }

        return Expression.MemberInit(Expression.New(map.ModelType), bindings);
    }

    /// <summary>
    /// Guards every intermediate reference of a member path, so an unset reference yields the
    /// member's default rather than throwing.
    /// </summary>
    internal static Expression NullSafe(Expression expression)
    {
        var chain = new List<MemberExpression>();
        for (Expression? cur = expression; cur is MemberExpression m; cur = m.Expression)
        {
            // Only the OWNERS need guarding -- the outermost member access cannot itself be null-deref.
            if (m.Expression is MemberExpression) chain.Add((MemberExpression)m.Expression);
        }

        if (chain.Count == 0) return expression;

        Expression guarded = expression;
        Type resultType = expression.Type;
        Expression fallback = Expression.Default(NullableOf(resultType));
        if (guarded.Type != fallback.Type) guarded = Expression.Convert(guarded, fallback.Type);

        // Innermost owner last: wrapping outward-in keeps each test ahead of the dereference it guards.
        foreach (MemberExpression owner in chain)
        {
            if (owner.Type.IsValueType && Nullable.GetUnderlyingType(owner.Type) is null) continue;

            guarded = Expression.Condition(
                Expression.Equal(owner, Expression.Constant(null, owner.Type)),
                Expression.Default(fallback.Type),
                guarded);
        }

        return guarded;
    }

    private static Type NullableOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? typeof(Nullable<>).MakeGenericType(type)
            : type;

    /// <summary>
    /// Narrows a guarded value back to the member's own type. A guard widens a non-nullable value
    /// type to its nullable form; the member may not accept that, and an unset reference then means
    /// the member's default.
    /// </summary>
    private static Expression Coerce(Expression value, Type target)
    {
        if (value.Type == target) return value;

        // Coalesce, not a HasValue/Value conditional: the guarded expression would otherwise be
        // emitted twice, and the provider would translate the whole path twice with it.
        if (Nullable.GetUnderlyingType(value.Type) == target)
            return Expression.Coalesce(value, Expression.Default(target));

        return Expression.Convert(value, target);
    }

    internal static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new NotSupportedException($"'{member.Name}' is neither a property nor a field."),
    };
}
