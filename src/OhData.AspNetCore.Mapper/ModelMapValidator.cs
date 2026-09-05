using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace OhData.AspNetCore.Mapper;

/// <summary>
/// Refuses a map that cannot serve the model, at startup.
/// </summary>
/// <remarks>
/// <para>
/// Validation is <b>unconditional</b>. An opt-in check reports the defect only to the adopters who
/// already suspected it, and every condition below produces a silently wrong response rather than a
/// loud failure: an undeclared member serialises as its default under a <c>200</c>, which no client
/// can distinguish from a genuinely empty value.
/// </para>
/// <para>
/// Every message names the member and the remedy. A validation error that says only that something
/// is wrong costs more than it saves — the whole point of moving the failure to startup is that the
/// developer is standing in front of it.
/// </para>
/// </remarks>
public static class ModelMapValidator
{
    /// <summary>Validates one map and the maps its navigations reach.</summary>
    /// <exception cref="InvalidOperationException">The map cannot serve the model.</exception>
    public static void Validate(ModelMap map, ModelMapRegistry registry)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var errors = new List<string>();
        var seen = new HashSet<Type>();
        Validate(map, registry, errors, seen);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"The map from '{map.EntityType.Name}' to '{map.ModelType.Name}' is incomplete:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", errors));
        }
    }

    private static void Validate(
        ModelMap map, ModelMapRegistry registry, List<string> errors, HashSet<Type> seen)
    {
        if (!seen.Add(map.ModelType)) return;

        RequireParameterlessConstructor(map, errors);
        RequireEveryMemberDeclared(map, errors);
        RequireDecomposableFormats(map, errors);

        foreach (ModelMemberBinding binding in map.Navigations)
        {
            string where = $"'{map.ModelType.Name}.{binding.ModelMember.Name}'";

            if (binding.ElementModelType is null)
            {
                errors.Add($"{where} is a navigation with no model element type.");
                continue;
            }

            ModelMap? target = registry.Find(binding.ElementModelType);
            if (target is null)
            {
                errors.Add(
                    $"{where} maps to model type '{binding.ElementModelType.Name}', which has no map. " +
                    $"Declare one with Nested<{binding.ElementEntityType?.Name ?? "TEntity"}, " +
                    $"{binding.ElementModelType.Name}>(...).");
                continue;
            }

            if (target.EntityType != binding.ElementEntityType)
            {
                errors.Add(
                    $"{where} reaches entity '{binding.ElementEntityType?.Name}' but the map for " +
                    $"'{binding.ElementModelType.Name}' is declared from '{target.EntityType.Name}'.");
                continue;
            }

            Validate(target, registry, errors, seen);
        }
    }

    private static void RequireParameterlessConstructor(ModelMap map, List<string> errors)
    {
        if (map.ModelType.GetConstructor(Type.EmptyTypes) is null)
        {
            errors.Add(
                $"'{map.ModelType.Name}' has no public parameterless constructor, so the projection " +
                $"cannot construct it. Give it one, or declare a different model type.");
        }
    }

    /// <summary>
    /// Every settable model member is declared or explicitly ignored.
    /// </summary>
    /// <remarks>
    /// The surface is the members the serializer will emit: a public property with a public getter.
    /// A get-only property cannot be populated by the projection at all, so requiring a declaration
    /// for one would refuse a map that has no way to satisfy it.
    /// </remarks>
    private static void RequireEveryMemberDeclared(ModelMap map, List<string> errors)
    {
        foreach (PropertyInfo property in map.ModelType.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            if (property.GetGetMethod() is null) continue;
            if (property.GetSetMethod() is null) continue;
            if (map.Find(property.Name) is not null) continue;

            errors.Add(
                $"'{map.ModelType.Name}.{property.Name}' has no binding. Declare where it comes " +
                $"from — Property(d => d.{property.Name}).From(...) — or Ignore(d => d.{property.Name}) " +
                $"if it is not served from the entity.");
        }
    }

    /// <summary>
    /// Every <c>Format(...)</c> really is an interpolation, checked by decomposing it now rather than
    /// on the first request that filters over it.
    /// </summary>
    private static void RequireDecomposableFormats(ModelMap map, List<string> errors)
    {
        var rewriter = new ModelToEntityRewriter(map);
        ParameterExpression entity = Expression.Parameter(map.EntityType, "e");

        foreach (ModelMemberBinding binding in map.Bindings.Where(b => b.Kind == ModelBindingKind.Format))
        {
            try
            {
                rewriter.BindingFor(binding, entity);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"'{map.ModelType.Name}.{binding.ModelMember.Name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Probes that every binding the map declares really translates on the adopter's own provider,
    /// by asking it to produce a query for each one.
    /// </summary>
    /// <remarks>
    /// The structural checks above cannot answer this: <c>Compute(...)</c> takes an arbitrary
    /// expression, so only the provider knows whether it translates. Separated from
    /// <c>Validate</c> because it needs a live queryable, and offered so an adopter can run it
    /// from a startup health check rather than discovering an untranslatable member as a 500 on the
    /// first request that filters over it.
    /// </remarks>
    /// <param name="map">The map to probe.</param>
    /// <param name="registry">The maps its navigations reach.</param>
    /// <param name="source">A queryable over the entity, from the adopter's own provider.</param>
    /// <param name="describe">
    /// Forces translation without executing — <c>q =&gt; q.ToQueryString()</c> on EF Core. A provider
    /// throwing here is what marks the member untranslatable.
    /// </param>
    /// <returns>The members that did not translate, each with the provider's own reason.</returns>
    public static IReadOnlyList<(string Member, string Reason)> ProbeTranslatability<TEntity>(
        ModelMap map,
        ModelMapRegistry registry,
        IQueryable<TEntity> source,
        Func<IQueryable, string> describe)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (describe is null) throw new ArgumentNullException(nameof(describe));

        var failures = new List<(string, string)>();

        foreach (ModelMemberBinding binding in map.Bindings)
        {
            if (binding.Kind is ModelBindingKind.Ignored || binding.IsNavigation) continue;

            // Probed through OrderBy rather than Select: it keeps the member's own type (so nothing
            // is boxed into a shape the provider would reject for a reason of its own) and it puts
            // the expression somewhere EF Core must translate -- a final Select is the one clause it
            // is still allowed to evaluate on the client, so a Select probe would pass for a member
            // that no $filter or $orderby could ever use.
            var rewriter = new ModelToEntityRewriter(map, registry.Resolver);
            try
            {
                Expression value = rewriter.BindingFor(binding, rewriter.EntityParameter)!;
                LambdaExpression key = Expression.Lambda(value, rewriter.EntityParameter);

                MethodInfo orderBy = typeof(Queryable).GetMethods()
                    .Single(m => m.Name == nameof(Queryable.OrderBy) && m.GetParameters().Length == 2)
                    .MakeGenericMethod(typeof(TEntity), key.ReturnType);

                var ordered = (IQueryable)source.Provider.CreateQuery(
                    Expression.Call(orderBy, source.Expression, Expression.Quote(key)));

                describe(ordered);
            }
            catch (Exception ex)
            {
                failures.Add(($"{map.ModelType.Name}.{binding.ModelMember.Name}",
                              (ex.InnerException ?? ex).Message));
            }
        }

        return failures;
    }
}
