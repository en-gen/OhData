using System;
using System.Collections.Generic;
using System.Linq;

namespace OhData;

/// <summary>
/// One model-bound allowlist declaration — the literal argument a profile handed to
/// <c>EntityTypeConfiguration&lt;TModel&gt;.Filter/OrderBy/Select/Expand</c> — captured at the call
/// site in <c>EntitySetProfile.VisitModelBuilder</c>. See <see cref="ModelBoundAllowlists"/> for why
/// this is recorded rather than re-derived.
/// </summary>
/// <remarks>
/// Three states, and they are all distinct:
/// <list type="bullet">
/// <item><description><see cref="Applied"/> <c>false</c> — the capability flag was off, so the
/// profile made no call at all and contributed nothing to the shared type configuration.</description></item>
/// <item><description><see cref="Applied"/> <c>true</c>, <see cref="Properties"/> <c>null</c> — the
/// call was made with no allowlist, which marks the whole type permissive.</description></item>
/// <item><description><see cref="Applied"/> <c>true</c>, <see cref="Properties"/> non-null — the
/// call was made with an allowlist, which is exhaustive for the whole type.</description></item>
/// </list>
/// </remarks>
internal readonly struct ModelBoundAllowlist : IEquatable<ModelBoundAllowlist>
{
    internal ModelBoundAllowlist(bool applied, string[]? properties)
    {
        Applied = applied;
        Properties = properties;
    }

    /// <summary>Whether the profile called the model builder for this query option at all.</summary>
    internal bool Applied { get; }

    /// <summary>
    /// The exact array passed, already resolved to EDM names and (for filter/orderby) already
    /// merged with this profile's navigation names — i.e. the thing that actually landed in the
    /// shared <c>ModelBoundQuerySettings</c>. <c>null</c> means "no allowlist" (permissive).
    /// </summary>
    internal string[]? Properties { get; }

    /// <summary>
    /// Equal when both declarations would write the same model-bound settings. Order-insensitive
    /// and ordinal: <c>Filter("A","B")</c> and <c>Filter("B","A")</c> produce identical settings.
    /// </summary>
    public bool Equals(ModelBoundAllowlist other)
    {
        if (Applied != other.Applied) return false;
        if (Properties is null || other.Properties is null) return Properties is null && other.Properties is null;
        return new HashSet<string>(Properties, StringComparer.Ordinal).SetEquals(other.Properties);
    }

    public override bool Equals(object? obj) => obj is ModelBoundAllowlist other && Equals(other);

    public override int GetHashCode() => Applied ? 1 : 0;

    internal string Describe() => !Applied
        ? "not configured"
        : Properties is null ? "all properties" : $"[{string.Join(", ", Properties.OrderBy(p => p, StringComparer.Ordinal))}]";
}

/// <summary>
/// #458: the four model-bound allowlist declarations one profile contributed to the
/// <b>shared, per-CLR-type</b> <c>EntityTypeConfiguration&lt;TModel&gt;</c>, plus the
/// <see cref="Validate"/> pass that rejects two profiles over one model type declaring divergent
/// ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scope mismatch.</b> <c>FilterProperties</c>/<c>OrderByProperties</c>/
/// <c>SelectProperties</c>/<c>ExpandProperties</c> are configured per <i>entity set</i>, but they
/// are enforced through <c>ModelBoundQuerySettings</c>, which
/// <c>Microsoft.AspNetCore.OData</c> reads off the EDM <i>type</i> (and property) — never off the
/// navigation source. Two profiles over one CLR model type therefore write the same type-level
/// settings, and the result is their <b>union</b>: each entity set then accepts a property its own
/// profile withheld, with responses byte-identical to the correctly-gated case. Measured for all
/// four options, in both registration orders.
/// </para>
/// <para>
/// <b>Per-entity-set model-bound settings do not exist, so this cannot be fixed by scoping it
/// down.</b> In <c>Microsoft.OData.ModelBuilder</c> 2.x the fluent model-bound API
/// (<c>Filter</c>/<c>OrderBy</c>/<c>Select</c>/<c>Expand</c>/<c>Count</c>/<c>Page</c>) is declared
/// only on <c>StructuralTypeConfiguration&lt;T&gt;</c> and <c>PropertyConfiguration</c>;
/// <c>EntitySetConfiguration</c>/<c>NavigationSourceConfiguration</c> expose bindings, the entity
/// type, the name and vocabulary terms, and nothing else. On the consuming side every
/// <c>GetModelBoundQuerySettings</c> overload in <c>Microsoft.AspNetCore.OData</c> takes an
/// <c>IEdmStructuredType</c> (optionally with an <c>IEdmProperty</c>), and the capability-vocabulary
/// annotations that <i>can</i> sit on an entity set are not read by the query validators at all —
/// they are metadata-only advertisement. So the honest fix is to refuse the ambiguous
/// configuration at startup rather than let it silently resolve to the widest of the two.
/// </para>
/// <para>
/// <b>Declarations are recorded, not re-derived.</b> <see cref="ModelBoundAllowlist.Properties"/>
/// holds the literal array handed to the model builder — after EDM-name resolution and after the
/// navigation-name merge — because that array <i>is</i> the shared state. Comparing the raw
/// profile-level allowlists instead would miss a divergence introduced by either transform, and
/// would invent one where two profiles spell the same effective set differently.
/// </para>
/// <para>
/// This is the same hazard shape <c>IgnoredPropertyJsonOptions.BuildIgnoredPropertyMap</c> already
/// guards for <c>Ignore()</c>, and deliberately the same remedy: a startup
/// <see cref="InvalidOperationException"/> naming both entity sets, the option, and each declared
/// set. A warning was rejected — the failure is silent, invisible on the wire, and widens a
/// deliberately narrowed query surface, which is exactly the class of defect a log line does not
/// stop.
/// </para>
/// <para>
/// Legitimate multi-set-per-type registrations are unaffected: the check fires only on
/// <i>divergent</i> declarations. Two profiles that both leave an allowlist unset agree (both
/// permissive), and a profile whose capability flag is off contributes nothing to the shared
/// settings and so agrees with anything — its own requests are already refused by the flag gate
/// before the EDM is consulted.
/// </para>
/// </remarks>
internal sealed class ModelBoundAllowlists
{
    /// <summary>
    /// The declaration for a profile that reached none of the four call sites — an
    /// <c>AdvancedConfigure</c> override, which ejects before them and owns the EDM outright.
    /// Compares equal to itself and to nothing else that was applied, so an eject-hatch profile
    /// never trips the check.
    /// </summary>
    internal static readonly ModelBoundAllowlists None = new(
        default, default, default, default);

    internal ModelBoundAllowlists(
        ModelBoundAllowlist select,
        ModelBoundAllowlist expand,
        ModelBoundAllowlist filter,
        ModelBoundAllowlist orderBy)
    {
        Select = select;
        Expand = expand;
        Filter = filter;
        OrderBy = orderBy;
    }

    internal ModelBoundAllowlist Select { get; }
    internal ModelBoundAllowlist Expand { get; }
    internal ModelBoundAllowlist Filter { get; }
    internal ModelBoundAllowlist OrderBy { get; }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when two profiles in one registration expose
    /// the same CLR model type but declare divergent model-bound allowlists for the same query
    /// option. Called once from <c>MapOhData()</c>, before any request is served.
    /// </summary>
    internal static void Validate(IEnumerable<IEntitySetEndpointSource> profiles)
    {
        var firstSeen = new Dictionary<Type, (string EntitySetName, ModelBoundAllowlists Allowlists)>();

        foreach (IEntitySetEndpointSource profile in profiles)
        {
            ModelBoundAllowlists declared = profile.ModelBoundAllowlists;
            if (!firstSeen.TryGetValue(profile.ModelType, out (string EntitySetName, ModelBoundAllowlists Allowlists) first))
            {
                firstSeen[profile.ModelType] = (profile.EntitySetName, declared);
                continue;
            }

            Check("$select", "SelectProperties", first.Allowlists.Select, declared.Select);
            Check("$expand", "ExpandProperties", first.Allowlists.Expand, declared.Expand);
            Check("$filter", "FilterProperties", first.Allowlists.Filter, declared.Filter);
            Check("$orderby", "OrderByProperties", first.Allowlists.OrderBy, declared.OrderBy);

            void Check(string option, string configurator, ModelBoundAllowlist a, ModelBoundAllowlist b)
            {
                // An option no profile applied is not a divergence, and neither is a profile that
                // did not apply it agreeing with one that did -- see the remarks on this type.
                if (!a.Applied || !b.Applied || a.Equals(b)) return;

                throw new InvalidOperationException(
                    $"Entity sets '{first.EntitySetName}' and '{profile.EntitySetName}' both expose " +
                    $"model type '{profile.ModelType.Name}' but declare different {configurator} " +
                    $"allowlists for {option}: {a.Describe()} vs {b.Describe()}. " +
                    "Model-bound query settings are keyed by CLR type across the whole registration, " +
                    "so the two declarations would be UNIONED and each entity set would accept " +
                    "properties the other allows. Make the allowlists match exactly, or give the " +
                    "entity sets distinct CLR model types.");
            }
        }
    }
}
