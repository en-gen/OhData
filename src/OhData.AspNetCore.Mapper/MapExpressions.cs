using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// The expression utilities every part of the mapper shares.
/// </summary>
/// <remarks>
/// One site, several consumers. Each of these was written twice or three times before being
/// collected here, and this repository treats two independently-derived answers to one question as a
/// defect class in its own right.
/// </remarks>
internal static class MapExpressions
{
    /// <summary>Replaces a lambda's parameter with a concrete instance expression.</summary>
    public static Expression Inline(LambdaExpression lambda, Expression instance) =>
        new ParameterInliner(lambda.Parameters[0], instance).Visit(lambda.Body);

    /// <summary>The element type of a sequence type.</summary>
    public static Type ElementTypeOf(Type collectionType) =>
        collectionType.IsArray
            ? collectionType.GetElementType()!
            : collectionType.GetInterfaces()
                  .Concat(new[] { collectionType })
                  .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                  .GetGenericArguments()[0];

    /// <summary>The declared type of a property or field.</summary>
    public static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new NotSupportedException($"'{member.Name}' is neither a property nor a field."),
    };

    /// <summary>
    /// Guards every intermediate reference of a member path and narrows the result to
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Applied where the binding is resolved, so the value a row is <i>served</i> and the value a
    /// <c>$filter</c> <i>compares</i> are the same expression. They were derived separately once: the
    /// projection guarded and coalesced while the predicate read the raw path, so a row served
    /// <c>"CatId": 0</c> for an unset reference and then failed to match <c>CatId eq 0</c>.
    /// </remarks>
    public static Expression GuardAndNarrow(Expression value, Type target)
    {
        Expression guarded = Guard(value);
        return Narrow(guarded, target);
    }

    private static Expression Guard(Expression expression)
    {
        var owners = new List<MemberExpression>();
        for (Expression? cur = expression; cur is MemberExpression m; cur = m.Expression)
        {
            // Only the OWNERS need guarding -- the outermost member access cannot itself be a null
            // dereference.
            if (m.Expression is MemberExpression owner) owners.Add(owner);
        }

        if (owners.Count == 0) return expression;

        Type nullable = NullableOf(expression.Type);
        Expression guarded = expression.Type == nullable
            ? expression
            : Expression.Convert(expression, nullable);

        // Outermost owner first, so each test sits ahead of the dereference it guards.
        foreach (MemberExpression owner in owners)
        {
            if (owner.Type.IsValueType && Nullable.GetUnderlyingType(owner.Type) is null) continue;

            guarded = Expression.Condition(
                Expression.Equal(owner, Expression.Constant(null, owner.Type)),
                Expression.Default(nullable),
                guarded);
        }

        return guarded;
    }

    /// <summary>
    /// Converts a bound value to the model member's own type.
    /// </summary>
    /// <remarks>
    /// A model member is routinely declared wider than the column behind it — <c>int?</c> over a
    /// non-nullable column is how an API contract makes a value optional, and <c>long</c> over an
    /// <c>int</c> key is ordinary. Without this the projection succeeded and every <c>$filter</c> over
    /// the member threw <i>"the binary operator Equal is not defined for Int32 and Int64"</i>, which
    /// is the pass-startup-fail-at-request shape this package exists to remove.
    /// </remarks>
    private static Expression Narrow(Expression value, Type target)
    {
        if (value.Type == target) return value;

        // Coalesce rather than a HasValue/Value conditional: the guarded expression would otherwise be
        // emitted twice and the provider would translate the whole path twice with it.
        if (Nullable.GetUnderlyingType(value.Type) == target)
            return Expression.Coalesce(value, Expression.Default(target));

        return Expression.Convert(value, target);
    }

    private static Type NullableOf(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? typeof(Nullable<>).MakeGenericType(type)
            : type;

    private sealed class ParameterInliner : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private readonly Expression _replacement;

        public ParameterInliner(ParameterExpression parameter, Expression replacement)
        {
            _parameter = parameter;
            _replacement = replacement;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _parameter ? _replacement : node;
    }
}
