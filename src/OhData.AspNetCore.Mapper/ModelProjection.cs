using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Builds the entity-to-model projection from a <see cref="ModelMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// Composed onto the entity query rather than applied after materialisation, so the provider reads
/// exactly the columns the model needs and a member reached through a reference becomes a JOIN.
/// Mapping after materialisation cannot do that: EF returns an entity with its references unloaded,
/// so every path member renders null unless the whole graph is fetched first.
/// </para>
/// <para>
/// Scalar members only. A navigation reaches the wire through the collection pipeline's own
/// <c>$expand</c> handling — the profile's navigation delegates — and one the request did not expand
/// is omitted from the payload regardless, so binding it here would fetch data no response can
/// carry.
/// </para>
/// </remarks>
internal static class ModelProjection
{
    /// <summary>The projection as a lambda over the entity, for composing onto a queryable.</summary>
    public static LambdaExpression BuildLambda(ModelMap map, ModelMapRegistry registry)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));

        ParameterExpression entity = Expression.Parameter(map.EntityType, "e");
        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(map.EntityType, map.ModelType),
            BuildBody(map, registry, entity),
            entity);
    }

    /// <summary>The <c>new TModel { … }</c> body over an entity-typed expression.</summary>
    public static Expression BuildBody(ModelMap map, ModelMapRegistry registry, Expression entity)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (entity is null) throw new ArgumentNullException(nameof(entity));

        var rewriter = new ModelToEntityRewriter(map, registry);
        var bindings = new List<MemberBinding>();

        foreach (ModelMemberBinding binding in map.Bindings
                     .Where(b => b.Kind is not ModelBindingKind.Ignored && !b.IsNavigation))
        {
            // Through the rewriter, never a second derivation: the value a row is served and the
            // value a $filter compares are then the same expression by construction.
            bindings.Add(Expression.Bind(binding.ModelMember, rewriter.BindingFor(binding, entity)));
        }

        return Expression.MemberInit(Expression.New(map.ModelType), bindings);
    }
}
