using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;

namespace OhData;

/// <summary>
/// The single place this assembly answers "which EDM type backs this CLR type?".
/// </summary>
/// <remarks>
/// <para>
/// <b>#508 / #491 — the defect class this type exists to make structurally impossible.</b> The
/// idiom it replaces is <c>model.FindDeclaredType(clrType.FullName ?? clrType.Name)</c>, which
/// matches on the EDM type's FULL NAME. That is a convention, not a fact: a registration that
/// renames the schema — <c>ODataConventionModelBuilder.Namespace</c>, or
/// <c>EntityTypeConfiguration.Namespace</c> through <c>AdvancedConfigure</c>'s full EDM control —
/// makes the lookup return <c>null</c> for EVERY type in the model, and every caller then takes its
/// "this type is not in the EDM" branch. Nothing throws and nothing is logged; the whole model
/// silently changes behaviour. #491 measured it (<c>Namespace = "Rt.Custom"</c> →
/// <c>FindDeclaredType(typeof(Beta).FullName)</c> is <c>null</c> while the annotation resolves
/// Beta correctly) and re-keyed the nav-suppression map; #508 found the same call surviving at four
/// read-path sites, where the consequence is that <c>$expand</c> pushdown disengages model-wide.
/// </para>
/// <para>
/// <b>The route is <c>ODataConventionModelBuilder</c>'s own <see cref="ClrTypeAnnotation"/></b> —
/// the record the builder wrote when it built the type. It involves no name convention at all, so
/// renaming a namespace, a type, or both cannot make it miss. Absent only for a hand-built
/// <see cref="IEdmModel"/>, which OhData never produces (every model comes out of
/// <c>EntitySetProfile.VisitModelBuilder</c>'s <c>ODataModelBuilder</c>); callers that had a
/// meaningful fallback keep it.
/// </para>
/// <para>
/// <b>The map covers COMPLEX types as well as entity types (#507).</b>
/// <c>ODataConventionModelBuilder</c> models an entity-typed member of a complex type as a
/// navigation ON THE COMPLEX TYPE, so a map that walks only <c>IEdmEntityType</c> computes an empty
/// navigation set for every complex CLR type — which is how a complex type's own navigation escaped
/// suppression entirely. Keying by <see cref="IEdmStructuredType"/> is what makes the answer total.
/// </para>
/// <para>
/// <b>Lookup is EXACT, deliberately, and this type offers no base-chain walk.</b> That is the one
/// thing it does NOT share with <c>InheritedTypeConfig</c>/<c>InheritedNameSets</c>, whose walk
/// exists because per-type <i>configuration</i> declared on a base must apply to a derived RUNTIME
/// instance. The question here is different: it is "what does the EDM declare for exactly this
/// type", and answering it with a base type's declaration would be actively wrong at the callers.
/// <c>ResolveProfilesForClrType</c> documents that matching a base/derived CLR type rather than the
/// exact EDM type is what made #293's original fix over-broad; and the pushdown projection helpers
/// (<c>IsMemberInitProjectable</c> / <c>ScalarStructuralClrProps</c>) would answer with the BASE
/// type's structural properties and silently drop the derived ones from the projection. Callers
/// that legitimately want the nearest EDM-known ancestor walk the chain themselves over this map
/// (<c>BuildNavClrNames</c> does, and unions rather than taking the nearest, because a navigation
/// set is a suppression boundary).
/// </para>
/// <para>
/// Built once per <see cref="IEdmModel"/> instance and held in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, so it is collected with the model it describes.
/// One registration reuses one model instance for every request, so in steady state a lookup is a
/// single dictionary probe.
/// </para>
/// </remarks>
internal static class EdmClrTypeMap
{
    private static readonly ConditionalWeakTable<IEdmModel, IReadOnlyDictionary<Type, IEdmStructuredType>>
        s_byModel = new();

    /// <summary>
    /// Every EDM structured type <paramref name="model"/> declares, keyed by the CLR type the model
    /// builder recorded for it. Never null; empty for a model carrying no annotations.
    /// </summary>
    internal static IReadOnlyDictionary<Type, IEdmStructuredType> ForModel(IEdmModel model) =>
        s_byModel.GetValue(model, static m => Build(m));

    private static IReadOnlyDictionary<Type, IEdmStructuredType> Build(IEdmModel model)
    {
        var map = new Dictionary<Type, IEdmStructuredType>();
        foreach (IEdmStructuredType edmType in model.SchemaElements.OfType<IEdmStructuredType>())
        {
            Type? clrType = model.GetAnnotationValue<ClrTypeAnnotation>(edmType)?.ClrType;
            // TryAdd, not [], so the FIRST declaration wins if two EDM types somehow claim one CLR
            // type. Deterministic over SchemaElements' order, and #458 already refuses the
            // configuration that could produce a meaningful disagreement within one registration.
            if (clrType is not null) map.TryAdd(clrType, edmType);
        }
        return map;
    }

    /// <summary>The EDM structured type <paramref name="model"/> declares for exactly
    /// <paramref name="clrType"/>, or null.</summary>
    internal static IEdmStructuredType? FindStructuredType(IEdmModel? model, Type clrType) =>
        model is not null && ForModel((IEdmModel)model).TryGetValue(clrType, out IEdmStructuredType? edmType)
            ? edmType
            : null;

    /// <summary>
    /// The EDM ENTITY type <paramref name="model"/> declares for exactly <paramref name="clrType"/>,
    /// or null — including when the model declares it as a COMPLEX type, which is the same answer
    /// <c>FindDeclaredType(...) as IEdmEntityType</c> gave.
    /// </summary>
    internal static IEdmEntityType? FindEntityType(IEdmModel? model, Type clrType) =>
        FindStructuredType(model, clrType) as IEdmEntityType;
}
