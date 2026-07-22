using System;

namespace OhData;

/// <summary>
/// Server-wide default settings applied to all entity set profiles unless overridden at the
/// profile level. Configure via <c>builder.WithDefaults(d => { ... })</c> when calling
/// <c>AddOhData</c>.
/// </summary>
public sealed class EntitySetDefaults
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

    private long? _maxRequestBodyBytes;

    /// <summary>
    /// Default maximum request-body size, in bytes, for write operations (POST/PUT/PATCH and their
    /// navigation/<c>$ref</c>/property/action variants) across all entity sets. <c>null</c> (the
    /// default) applies no OhData-level limit — the host's Kestrel <c>MaxRequestBodySize</c> (~30 MB
    /// by default) still applies. When set, a request whose body exceeds the limit is rejected with
    /// <c>413 Payload Too Large</c> before the body is deserialized. Profile-level
    /// <see cref="EntitySetProfile{TKey,TModel}.MaxRequestBodyBytes"/> overrides this value. Must be
    /// a positive value or <c>null</c>.
    /// </summary>
    public long? MaxRequestBodyBytes
    {
        get => _maxRequestBodyBytes;
        set
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes), value, "MaxRequestBodyBytes must be a positive value or null.");
            _maxRequestBodyBytes = value;
        }
    }

    private int _maxExpansionDepth = 3;
    private int _maxFilterNodeCount = 10000;
    private int _maxOrderByNodeCount = 1000;
    private int _maxAnyAllExpressionDepth = 1000;

    /// <summary>
    /// #202/#206: maximum nested <c>$expand</c> depth accepted on the collection read paths, and the
    /// ceiling <c>$levels</c> is resolved and capped to (<c>$levels=max</c> becomes exactly this
    /// value; a numeric <c>$levels=N</c> is clamped to it). A request nesting <c>$expand</c> deeper —
    /// or requesting more <c>$levels</c> — than this is rejected with <c>400</c> before any handler
    /// runs. Defaults to <c>3</c>. Advertised in <c>$metadata</c> as the
    /// <c>Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels</c> annotation on each entity set so
    /// clients can discover it. Raise it to allow deeper graph queries, or lower it to harden against
    /// them; must be a positive integer. Profile-level
    /// <see cref="EntitySetProfile{TKey,TModel}.MaxExpansionDepth"/> overrides this value.
    /// </summary>
    public int MaxExpansionDepth
    {
        get => _maxExpansionDepth;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxExpansionDepth), value, "MaxExpansionDepth must be a positive integer.");
            _maxExpansionDepth = value;
        }
    }

    /// <summary>
    /// #202: maximum node count in a <c>$filter</c> expression tree (OData's
    /// <c>MaxNodeCount</c>). Defaults to <c>10000</c>. Lower it to reject pathologically large
    /// filter expressions sooner. Must be a positive integer. Profile-level override available.
    /// </summary>
    public int MaxFilterNodeCount
    {
        get => _maxFilterNodeCount;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxFilterNodeCount), value, "MaxFilterNodeCount must be a positive integer.");
            _maxFilterNodeCount = value;
        }
    }

    /// <summary>
    /// #202: maximum node count in an <c>$orderby</c> expression. Defaults to <c>1000</c>. Must be a
    /// positive integer. Profile-level override available.
    /// </summary>
    public int MaxOrderByNodeCount
    {
        get => _maxOrderByNodeCount;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxOrderByNodeCount), value, "MaxOrderByNodeCount must be a positive integer.");
            _maxOrderByNodeCount = value;
        }
    }

    /// <summary>
    /// #202: maximum nesting depth of <c>any()</c>/<c>all()</c> lambda expressions in a
    /// <c>$filter</c>. Defaults to <c>1000</c>. Must be a positive integer. Profile-level override
    /// available.
    /// </summary>
    public int MaxAnyAllExpressionDepth
    {
        get => _maxAnyAllExpressionDepth;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAnyAllExpressionDepth), value, "MaxAnyAllExpressionDepth must be a positive integer.");
            _maxAnyAllExpressionDepth = value;
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
    /// Whether <c>$select</c> projection pushdown is enabled by default on all entity sets
    /// (#206). When <c>true</c> (the default) and a request's <c>$select</c> is eligible, the
    /// <c>GetQueryable</c> path composes a member-init projection onto the profile's queryable
    /// so LINQ providers emit a column-pruned <c>SELECT</c>. Wire output is byte-identical
    /// either way; disable per profile (or here) for <c>IQueryable</c> providers that cannot
    /// translate member-init projections.
    /// </summary>
    public bool SelectPushdownEnabled { get; set; } = true;

    /// <summary>
    /// Whether <c>$expand</c> Include pushdown is enabled by default on all entity sets (#206
    /// phase 2). When <c>true</c> (the default) and a request's top-level
    /// <c>$expand</c> names a navigation that was declared <b>without</b> a custom expand delegate
    /// (a bare <c>HasMany</c>/<c>HasOptional</c>/<c>HasRequired</c>), the framework folds that
    /// navigation into the <c>GetQueryable</c> collection query's member-init projection so an
    /// EF Core-backed source loads the related rows via a single JOIN'd query (SQL pushdown)
    /// instead of leaving the navigation unexpandable. The expand's nested
    /// <c>$filter</c>/<c>$orderby</c>/<c>$top</c>/<c>$skip</c> push to SQL as a filtered/ordered/paged
    /// <c>Include</c>, and <c>$count</c>/<c>$select</c> shape the result. A navigation declared
    /// <b>with</b> a delegate (<c>getAll</c>/<c>get</c>/<c>batchGetAll</c>/<c>batchGet</c>) is NEVER
    /// pushed down — it always expands through its delegate, which may filter/order/authorize.
    /// Pushdown is skipped silently (the delegate-less navigation stays EDM-only for that request)
    /// whenever it is ineligible: a non-EF provider, a nested <c>$expand</c> (multi-level) or
    /// <c>$levels</c>, a cyclic navigation, or a projection/translation failure. Disable per profile
    /// (or here) to keep every delegate-less navigation unexpandable.
    /// </summary>
    public bool ExpandPushdownEnabled { get; set; } = true;

    /// <summary>
    /// Whether individual structural property routes appear in the generated API documentation
    /// (Swagger/OpenAPI): the two property reads
    /// (<c>GET /{EntitySet}({key})/{Property}</c> and <c>.../{Property}/$value</c>) and the
    /// property writes (<c>PUT</c>/<c>PATCH</c>/<c>DELETE /{EntitySet}({key})/{Property}</c>,
    /// including the immutable-key stubs). Defaults to <c>false</c>: these routes number four per
    /// property, per entity set, and would otherwise dominate the docs. They remain fully
    /// functional at runtime regardless of this flag — it only controls documentation visibility
    /// (via <c>ExcludeFromDescription</c>), and only matters when the routes are actually
    /// registered (i.e. <see cref="PropertyAccessEnabled"/> resolves <c>true</c> and the required
    /// handler is configured). Set to <c>true</c> to include them. Profile-level
    /// <c>PropertyRouteDocsEnabled</c> overrides this value.
    /// </summary>
    public bool PropertyRouteDocsEnabled { get; set; } = false;

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
    /// <c>OhData.RoundingMode.SpecCompliant</c> (round-half-away-from-zero, e.g.
    /// <c>2.5 → 3</c>) — see that type's XML doc for the EF Core provider-translation caveat
    /// that motivates <c>OhData.RoundingMode.BankersRounding</c>.
    /// Profile-level <c>RoundingMode</c> overrides this value.
    /// </summary>
    public RoundingMode RoundingMode { get; set; } = RoundingMode.SpecCompliant;
}
