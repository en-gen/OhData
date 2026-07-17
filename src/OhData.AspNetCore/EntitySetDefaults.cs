using System;

namespace OhData.Abstractions;

/// <summary>
/// Server-wide default settings applied to all entity set profiles unless overridden at the
/// profile level. Configure via <c>builder.WithDefaults(d => { ... })</c> when calling
/// <c>AddOhData</c>.
/// </summary>
public class EntitySetDefaults
{
    /// <summary>
    /// Whether <c>$select</c> is enabled by default on all entity sets (OData §11.2.4.1).
    /// Profile-level <c>SelectEnabled</c> overrides this value.
    /// </summary>
    public bool SelectEnabled { get; set; }

    /// <summary>
    /// Whether <c>$expand</c> is enabled by default on all entity sets (OData §11.2.4.2).
    /// Profile-level <c>ExpandEnabled</c> overrides this value.
    /// </summary>
    public bool ExpandEnabled { get; set; }

    /// <summary>
    /// Whether <c>$filter</c> is enabled by default on all entity sets (OData §11.2.6.1).
    /// Profile-level <c>FilterEnabled</c> overrides this value.
    /// </summary>
    public bool FilterEnabled { get; set; }

    /// <summary>
    /// Whether <c>$orderby</c> is enabled by default on all entity sets (OData §11.2.6.2).
    /// Profile-level <c>OrderByEnabled</c> overrides this value.
    /// </summary>
    public bool OrderByEnabled { get; set; }

    /// <summary>
    /// Whether <c>$count</c> is enabled by default on all entity sets (OData §11.2.6.5).
    /// Profile-level <c>CountEnabled</c> overrides this value.
    /// </summary>
    public bool CountEnabled { get; set; }

    private int? _maxTop = 1000;

    /// <summary>
    /// Default maximum value for <c>$top</c> across all entity sets (OData §11.2.6.3).
    /// Defaults to <c>1000</c>. Profile-level <c>MaxTop</c> overrides this value.
    /// Must be a positive integer or <c>null</c> (no limit).
    /// </summary>
    public int? MaxTop
    {
        get => _maxTop;
        set
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTop), value, "MaxTop must be a positive integer or null.");
            _maxTop = value;
        }
    }

    /// <summary>
    /// When <c>true</c> (the default), a <c>DELETE</c> on a non-existent resource returns
    /// <c>204 No Content</c> — idempotent per OData spec.
    /// Set to <c>false</c> to return <c>404 Not Found</c> instead.
    /// </summary>
    public bool IdempotentDelete { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, a <c>PUT</c> to a non-existent key will create the entity (upsert).
    /// Default <c>false</c> — PUT to a missing key returns 404.
    /// </summary>
    public bool AllowUpsert { get; set; } = false;

    /// <summary>
    /// Whether individual structural property access
    /// (<c>GET /{EntitySet}({key})/{Property}</c> and <c>GET .../{Property}/$value</c>,
    /// OData §11.2.6 / Part 2 §4.6-4.7) is enabled by default on all entity sets.
    /// Defaults to <c>true</c> (spec-conformant out of the box). Property routes are only
    /// registered when the profile also configures a <c>GetById</c> handler — this flag
    /// alone does not create routes.
    /// Profile-level <c>PropertyAccessEnabled</c> overrides this value.
    /// </summary>
    public bool PropertyAccessEnabled { get; set; } = true;

    /// <summary>
    /// Whether <c>POST /{EntitySet}</c> passes nested navigation-property values (a "deep
    /// insert" graph, OData §11.4.2.2) through to the <c>Post</c> handler by default.
    /// Defaults to <c>false</c>: nested navigation values are stripped before <c>Post</c> is
    /// invoked, so a handler that doesn't expect a graph never silently persists only part of
    /// it. Set to <c>true</c> to opt every entity set into deep insert, or override per profile
    /// via <c>AllowDeepInsert</c>. When enabled, the handler owns atomic persistence of the
    /// whole graph (e.g. a single EF Core <c>SaveChanges</c>).
    /// </summary>
    public bool AllowDeepInsert { get; set; }

    /// <summary>
    /// Midpoint-rounding behavior for the <c>round()</c> canonical function (OData Part 2
    /// §5.1.1.9) on the <c>GetQueryable</c> pushdown path. Defaults to
    /// <c>OhData.Abstractions.RoundingMode.SpecCompliant</c> (round-half-away-from-zero, e.g.
    /// <c>2.5 → 3</c>) — see that type's XML doc for the EF Core provider-translation caveat
    /// that motivates <c>OhData.Abstractions.RoundingMode.BankersRounding</c>.
    /// Profile-level <c>RoundingMode</c> overrides this value.
    /// </summary>
    public RoundingMode RoundingMode { get; set; } = RoundingMode.SpecCompliant;
}
