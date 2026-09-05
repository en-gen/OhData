using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// One (parent key, related value) row, the shape a batched navigation load projects into.
/// </summary>
/// <remarks>
/// A named type rather than an anonymous one because the projection is assembled as an expression
/// tree at startup, where an anonymous type cannot be constructed. Both members are typed, so the
/// provider sees a plain member-init over concrete columns and never an <c>object</c>-typed slot.
/// </remarks>
/// <typeparam name="TKey">The parent's key type.</typeparam>
/// <typeparam name="TValue">The related entity type.</typeparam>
public sealed class KeyedRow<TKey, TValue>
{
    /// <summary>The parent entity's key.</summary>
    public TKey Key { get; set; } = default!;

    /// <summary>The related entity.</summary>
    public TValue Value { get; set; } = default!;
}

/// <summary>
/// Builds the batched navigation loads a mapped profile registers for <c>$expand</c>.
/// </summary>
/// <remarks>
/// <para>
/// One query per expanded navigation per page — the shape OhData's <c>batchGetAll</c> overload
/// exists for — rather than one per parent row. The parent keys are pushed into the query as an
/// <c>IN</c> list, and a reshaped collection's join entity is elided by the same
/// <c>Element(...)</c> hop the map already declares, so a many-to-many never reaches the wire.
/// </para>
/// <para>
/// The load runs against the <b>entity</b> queryable and maps only what it fetched. It never
/// projects into the model type in the provider, which is the strategy this package exists to avoid:
/// a model-typed projection has to name every navigation it might ever need on every request, and a
/// member the provider cannot translate fails at the row rather than at startup.
/// </para>
/// </remarks>
public static class MappedNavigationLoader
{
    private static readonly MethodInfo s_contains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    // SelectMany(source, collectionSelector, resultSelector) -- NOT the indexed overload beside it,
    // whose collection selector takes (TSource, int). Both have three parameters and three generic
    // arguments, so the selector's own arity is what separates them.
    private static readonly MethodInfo s_selectMany = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.SelectMany)
                     && m.GetParameters().Length == 3
                     && m.GetGenericArguments().Length == 3
                     && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                         .GetGenericArguments().Length == 2);

    private static readonly MethodInfo s_select = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Select)
                     && m.GetParameters().Length == 2
                     && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                         .GetGenericArguments().Length == 2);

    private static readonly MethodInfo s_where = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Where)
                     && m.GetParameters().Length == 2
                     && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                         .GetGenericArguments().Length == 2);

    /// <summary>Loads a reshaped collection navigation for a page of parents.</summary>
    public static IReadOnlyList<KeyValuePair<object, object>> LoadCollection(
        IQueryable rootQuery,
        LambdaExpression entityKey,
        ModelMemberBinding binding,
        ModelMap elementMap,
        IReadOnlyList<object> parentKeys)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (binding.Kind != ModelBindingKind.Collection)
            throw new ArgumentException("Expected a collection binding.", nameof(binding));

        Type entityType = entityKey.Parameters[0].Type;
        Type keyType = entityKey.ReturnType;
        Expression filtered = RestrictToParents(rootQuery, entityKey, keyType, parentKeys);

        ParameterExpression parent = Expression.Parameter(entityType, "p");
        Expression collection = Inline(binding.Source!, parent);
        Type sourceElement = ElementTypeOf(collection.Type);

        ParameterExpression sourceItem = Expression.Parameter(sourceElement, "x");
        Expression relatedEntity = binding.ElementSource is null
            ? sourceItem
            : Inline(binding.ElementSource, sourceItem);

        Type rowType = typeof(KeyedRow<,>).MakeGenericType(keyType, elementMap.ModelType);
        Expression row = Expression.MemberInit(
            Expression.New(rowType),
            Expression.Bind(KeyProperty(rowType), Inline(entityKey, parent)),
            Expression.Bind(ValueProperty(rowType), ModelProjection.BuildBody(elementMap, relatedEntity)));

        Expression call = Expression.Call(
            s_selectMany.MakeGenericMethod(entityType, sourceElement, rowType),
            filtered,
            // Typed IEnumerable<TCollection> explicitly: the declared member is List<T>, and
            // Queryable.SelectMany's parameter type is matched exactly rather than by assignability.
            Expression.Quote(Expression.Lambda(
                typeof(Func<,>).MakeGenericType(
                    entityType, typeof(IEnumerable<>).MakeGenericType(sourceElement)),
                collection,
                parent)),
            Expression.Quote(Expression.Lambda(row, parent, sourceItem)));

        return Materialize(rootQuery.Provider, call, rowType);
    }

    /// <summary>Loads a single-valued navigation for a page of parents.</summary>
    public static IReadOnlyList<KeyValuePair<object, object>> LoadReference(
        IQueryable rootQuery,
        LambdaExpression entityKey,
        ModelMemberBinding binding,
        ModelMap targetMap,
        IReadOnlyList<object> parentKeys)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (binding.Kind != ModelBindingKind.Reference)
            throw new ArgumentException("Expected a reference binding.", nameof(binding));

        Type entityType = entityKey.Parameters[0].Type;
        Type keyType = entityKey.ReturnType;
        Expression filtered = RestrictToParents(rootQuery, entityKey, keyType, parentKeys);

        ParameterExpression parent = Expression.Parameter(entityType, "p");
        Type rowType = typeof(KeyedRow<,>).MakeGenericType(keyType, targetMap.ModelType);
        Expression target = Inline(binding.Source!, parent);

        // A null reference stays null rather than becoming an empty model: the dictionary the core
        // reads treats an absent value as "no related entity", which is what an unset single-valued
        // navigation means.
        Expression row = Expression.Condition(
            Expression.Equal(target, Expression.Constant(null, target.Type)),
            Expression.Constant(null, rowType),
            Expression.MemberInit(
                Expression.New(rowType),
                Expression.Bind(KeyProperty(rowType), Inline(entityKey, parent)),
                Expression.Bind(ValueProperty(rowType), ModelProjection.BuildBody(targetMap, target))));

        Expression call = Expression.Call(
            s_select.MakeGenericMethod(entityType, rowType),
            filtered,
            Expression.Quote(Expression.Lambda(row, parent)));

        return Materialize(rootQuery.Provider, call, rowType);
    }

    private static PropertyInfo KeyProperty(Type rowType) => rowType.GetProperty("Key")!;

    private static PropertyInfo ValueProperty(Type rowType) => rowType.GetProperty("Value")!;

    private static Expression RestrictToParents(
        IQueryable rootQuery, LambdaExpression entityKey, Type keyType, IReadOnlyList<object> parentKeys)
    {
        Type entityType = entityKey.Parameters[0].Type;
        ParameterExpression e = Expression.Parameter(entityType, "e");

        // A typed array constant, so the provider sees an IN list rather than a boxed sequence it
        // cannot translate.
        Array typedKeys = Array.CreateInstance(keyType, parentKeys.Count);
        for (int i = 0; i < parentKeys.Count; i++) typedKeys.SetValue(parentKeys[i], i);

        Expression predicate = Expression.Call(
            s_contains.MakeGenericMethod(keyType),
            Expression.Constant(typedKeys, typeof(IEnumerable<>).MakeGenericType(keyType)),
            Inline(entityKey, e));

        return Expression.Call(
            s_where.MakeGenericMethod(entityType),
            rootQuery.Expression,
            Expression.Quote(Expression.Lambda(predicate, e)));
    }

    private static IReadOnlyList<KeyValuePair<object, object>> Materialize(
        IQueryProvider provider, Expression query, Type rowType)
    {
        PropertyInfo keyProp = KeyProperty(rowType);
        PropertyInfo valueProp = ValueProperty(rowType);

        var results = new List<KeyValuePair<object, object>>();
        foreach (object? row in (IEnumerable)provider.CreateQuery(query))
        {
            if (row is null) continue;

            object? key = keyProp.GetValue(row);
            object? value = valueProp.GetValue(row);
            if (key is null || value is null) continue;

            results.Add(new KeyValuePair<object, object>(key, value));
        }

        return results;
    }

    private static Expression Inline(LambdaExpression lambda, Expression instance) =>
        new Inliner(lambda.Parameters[0], instance).Visit(lambda.Body);

    private static Type ElementTypeOf(Type collectionType) =>
        collectionType.IsArray
            ? collectionType.GetElementType()!
            : collectionType.GetInterfaces()
                  .Concat(new[] { collectionType })
                  .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                  .GetGenericArguments()[0];

    private sealed class Inliner : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private readonly Expression _replacement;

        public Inliner(ParameterExpression parameter, Expression replacement)
        {
            _parameter = parameter;
            _replacement = replacement;
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _parameter ? _replacement : node;
    }
}
