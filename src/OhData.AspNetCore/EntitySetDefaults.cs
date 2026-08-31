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
    /// Profile-level <c>ExpandEnabled</c> overrides this value. When it resolves to <c>false</c>,
    /// <c>$metadata</c> advertises <c>Org.OData.Capabilities.V1.ExpandRestrictions/Expandable</c>
    /// as <c>false</c> on that entity set (#303), so a client discovers the gate rather than
    /// learning about it from a <c>400</c>.
    /// </summary>
    public bool ExpandEnabled { get; set; }

    /// <summary>
    /// Whether <c>$filter</c> is enabled by default on all entity sets (OData §11.2.5.1).
    /// Profile-level <c>FilterEnabled</c> overrides this value.
    /// </summary>
    public bool FilterEnabled { get; set; }

    /// <summary>
    /// Whether <c>$orderby</c> is enabled by default on all entity sets (OData §11.2.5.2).
    /// Profile-level <c>OrderByEnabled</c> overrides this value.
    /// </summary>
    public bool OrderByEnabled { get; set; }

    /// <summary>
    /// Whether <c>$count</c> is enabled by default on all entity sets (OData §11.2.5.5).
    /// Profile-level <c>CountEnabled</c> overrides this value.
    /// </summary>
    public bool CountEnabled { get; set; }

    private int? _maxTop = 1000;

    /// <summary>
    /// Default maximum value for <c>$top</c> across all entity sets (OData §11.2.5.3).
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

    private int? _maxExpandTop;

    /// <summary>
    /// #254: default per-navigation ceiling on a <b>nested</b> <c>$top</c> inside a <c>$expand</c>
    /// (<c>?$expand=Children($top=N)</c>), and the bound on how many related entities a nested
    /// <c>$count</c> may materialize. Defaults to <c>null</c> — <b>no ceiling</b> (#313): the
    /// framework cannot know how large a child collection is, so it ships the control rather than a
    /// guess. Both protections are therefore opt-in; set this to bound them.
    /// Profile-level <c>MaxExpandTop</c> overrides this value; the <b>root</b> entity set's resolved
    /// value governs at every nesting depth (the same rule as <see cref="MaxExpansionDepth"/>).
    /// A nested <c>$top</c> greater than the ceiling is rejected with <c>400 Bad Request</c>
    /// (<c>InvalidQueryOption</c>) before any handler runs, at any depth and on any read path.
    /// A nested <c>$count</c> whose related collection exceeds the ceiling is also rejected with
    /// <c>400</c> rather than silently truncated, because OData §11.2.5.5 requires
    /// <c>Nav@odata.count</c> to report the FULL filtered collection, not the returned page.
    /// #313 widened what the value covers once it is set: it now bounds <b>every</b> collection
    /// <c>$expand</c> level — including a bare <c>?$expand=Children</c>, one carrying only
    /// <c>$select</c>/<c>$orderby</c>/<c>$filter</c>/<c>$skip</c>, and every level of a
    /// <c>$levels=N</c> recursion — not just the two #254 shapes. Setting it also composes the
    /// child-key <c>ORDER BY</c> tiebreaker on those shapes, so it governs the nested wire order as
    /// well as the status code.
    /// <para>
    /// <b>How an over-ceiling collection is answered depends on how it was loaded (#464/#418).</b>
    /// Where the framework <i>composed</i> the child query — a delegate-less navigation folded into
    /// an EF Core projection or <c>Include</c> — it also composed the child-key order, so the
    /// response can be a trimmed page plus a <c>Nav@odata.nextLink</c> continuation when
    /// <see cref="ExpandPagingEnabled"/> is on, and a <c>400</c> otherwise. Where it did <b>not</b> —
    /// a <c>GetAll</c> source, a Priority-1 source, an <c>IQueryable</c> that is not EF Core-backed
    /// (which <c>$search</c> also produces, by swapping in an in-memory queryable), a branch the
    /// <c>$expand</c> pushdown declined, or any level of a single-entity <c>GET /Set(key)</c> — the
    /// related rows arrive already materialized inside whatever the handler returned, in that
    /// handler's own order. Page 1 and a continuation cannot be proven to agree on such an order, and
    /// a link over a disagreeing order silently skips and duplicates rows, so those are answered with
    /// <c>400</c> (<c>InvalidQueryOption</c>) and <see cref="ExpandPagingEnabled"/> buys nothing
    /// there. Either way the bound holds: the ceiling is never silently exceeded and a collection is
    /// never silently truncated.
    /// </para>
    /// <para>
    /// <b>A navigation whose delegate actually RAN is never bounded by this value</b> (#313 O6): its
    /// rows are the answer the profile's own <c>Handler</c>/<c>BatchHandler</c> returned, and the
    /// framework neither truncates nor rejects those. Bound them in the delegate.
    /// </para>
    /// <para>
    /// That exemption turns on the delegate having been <i>invoked</i>, not on the navigation being
    /// <i>declared</i> with one, and the two come apart below a raw-served parent: the expand
    /// pipeline does not recurse into a delegate-less navigation's subtree, so a delegate-backed
    /// navigation one level under it is never called and the rows present came from the parent's own
    /// handler. Those are bounded like any other raw rows. The exempt case is therefore the one the
    /// ceiling walk can actually reach — a delegate-backed navigation at the root of the expansion —
    /// and it never descends into that navigation's subtree at all.
    /// </para>
    /// <para>
    /// <b>On a raw-served (non-pushed) expansion, the nested window is still not APPLIED.</b> A
    /// nested <c>$top</c>/<c>$skip</c> within the ceiling is accepted and then ignored on those
    /// paths, so the response is the whole collection — bounded by this ceiling, but not windowed to
    /// what was asked for. That residue is tracked separately (#352/#464); this value is a DoS bound,
    /// not a promise that every nested option is honoured on every substrate.
    /// </para>
    /// Must be a positive integer or <c>null</c> (no ceiling). Use <c>null</c>, not a large sentinel:
    /// <c>int.MaxValue</c> counts as set, so it pays for every bound and tiebreaker while making the
    /// check unable to fire.
    /// <para>
    /// <b>Not advertised in <c>$metadata</c> (#303).</b> <c>Org.OData.Capabilities.V1</c> has no term
    /// for a maximum result <i>count</i> at any scope — its only numeric slot, <c>MaxLevels</c>, is a
    /// nesting <i>depth</i>, and <c>TopSupported</c>/<c>SkipSupported</c> are booleans with no numeric
    /// slot. Rather than mint a custom term no client understands, or publish a false approximation,
    /// this ceiling stays enforced at request time only. Same for <see cref="MaxExpandBreadth"/> and
    /// <see cref="MaxTop"/>. See the expressibility note on <c>OhDataBuilder.AnnotateCapabilities</c>.
    /// </para>
    /// </summary>
    public int? MaxExpandTop
    {
        get => _maxExpandTop;
        set
        {
            if (value is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxExpandTop), value, "MaxExpandTop must be a positive integer or null.");
            _maxExpandTop = value;
        }
    }

    /// <summary>
    /// #313: whether a <b>bare</b> collection <c>$expand</c> whose child collection exceeds the
    /// resolved <see cref="MaxExpandTop"/> is served as its first <c>MaxExpandTop</c> children plus a
    /// <c>Nav@odata.nextLink</c> continuation, rather than rejected with <c>400</c>. Defaults to
    /// <c>false</c>. Profile-level <c>ExpandPagingEnabled</c> overrides this value.
    /// <para>
    /// Inert on its own: it does nothing unless <see cref="MaxExpandTop"/> is also set, because with
    /// no ceiling there is no boundary at which a continuation could begin. <c>MaxExpandTop</c> is
    /// also the page size — for the first page and every continuation alike. There is deliberately no
    /// second page-size knob.
    /// </para>
    /// <para>
    /// It is a separate opt-in from the ceiling because a continuation link is <i>worse</i> than a
    /// <c>400</c> for a client that does not read nested annotations: the client sees a complete-looking
    /// collection that is silently truncated. Only a deployment that knows its clients follow
    /// <c>Nav@odata.nextLink</c> should turn this on.
    /// </para>
    /// </summary>
    public bool ExpandPagingEnabled { get; set; }

    /// <summary>
    /// #474: the framework's own default ceiling on a write body, in bytes — <b>30,000,000</b>, which
    /// is Kestrel's own documented default <c>MaxRequestBodySize</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The number is <b>not invented</b>, and that is the whole point of choosing it. Before #474 a
    /// registration that never set <see cref="MaxRequestBodyBytes"/> had no OhData-level ceiling at
    /// all: #203's filter does both of its jobs — the <c>Content-Length</c> fast-reject and setting
    /// the per-request <c>MaxRequestBodySize</c> — only when the limit resolves non-null, and it
    /// defaulted to <c>null</c> at both levels. The only thing bounding a materialised body was the
    /// host's Kestrel limit, which a host that also accepts uploads routinely raises or disables.
    /// </para>
    /// <para>
    /// Adopting Kestrel's number means a <b>default</b> host sees no behaviour change — the same
    /// byte count was already rejected, one layer down (now with the OData <c>413</c> envelope
    /// rather than Kestrel's). The behaviour change is confined to exactly the exposed population:
    /// a host that raised or removed its own limit. That is a breaking change for such a host, and
    /// the remedy is one line — raise <see cref="MaxRequestBodyBytes"/>, or set it to <c>null</c>
    /// to restore "the host's limit is the only limit".
    /// </para>
    /// <para>
    /// This is deliberately <i>not</i> the same question as
    /// <c>BufferRequestBodyAsync</c>'s capacity-hint clamp. That clamp is about a <i>declared</i>
    /// length driving an allocation before any byte arrives; this is a ceiling on the bytes actually
    /// received. Both exist, and neither substitutes for the other.
    /// </para>
    /// </remarks>
    public const long DefaultMaxRequestBodyBytes = 30_000_000;

    private long? _maxRequestBodyBytes = DefaultMaxRequestBodyBytes;

    /// <summary>
    /// Default maximum request-body size, in bytes, for write operations (POST/PUT/PATCH and their
    /// navigation/<c>$ref</c>/property/action variants) across all entity sets. Defaults to
    /// <see cref="DefaultMaxRequestBodyBytes"/> (#474). Setting it to <c>null</c> applies no
    /// OhData-level limit — only the host's Kestrel <c>MaxRequestBodySize</c> then bounds a body.
    /// A request whose body exceeds the limit is rejected with <c>413 Payload Too Large</c> before
    /// the body is deserialized. Profile-level
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
    /// #328: the hard upper bound on <see cref="MaxExpansionDepth"/> — server-wide and per profile.
    /// Configuring a deeper value throws <see cref="ArgumentOutOfRangeException"/> at startup.
    /// <para>
    /// <b>Why a ceiling exists at all.</b> Relational query translation for a pushed nested
    /// projection is <c>Θ(3ⁿ)</c> in the nesting depth: EF Core re-translates each nested-collection
    /// subtree three times with no memoization, so every additional level triples the CPU spent
    /// building the query — before a single row is read. Measured on a 16-node chain returning a
    /// ~6 KB body, one navigation per level, no database round trip needed to reproduce it:
    /// </para>
    /// <code>
    /// depth  5 →     0.09 s
    /// depth  6 →     0.24 s   ← this ceiling
    /// depth  8 →     3.8  s
    /// depth 10 →    32    s
    /// depth 12 →   291    s   (4.9 minutes of single-core CPU, unauthenticated, one request)
    /// </code>
    /// <para>
    /// <b>Why 6 and not 3.</b> The blow-up is at 10+, not at 5: depth 5 costs ~90 ms, and this
    /// project's own documentation and tests already use <c>MaxExpansionDepth = 5</c>. Capping at
    /// the default of 3 would invalidate a documented example for a shape that is not expensive.
    /// 6 leaves real headroom above the documented 5 while keeping the worst configurable depth
    /// under a quarter-second on the depth axis.
    /// </para>
    /// <para>
    /// <b>This is a mitigation, not a fix.</b> Nothing about <c>$levels=12</c> over a 16-node chain
    /// returning 6 KB is unreasonable; it is expensive only because of upstream re-translation. The
    /// real answer is one flat query per level instead of one nested projection
    /// (<a href="https://github.com/en-gen/OhData/issues/430">#430</a>). The ceiling bounds the
    /// damage in the meantime. Depth is also only one axis; breadth multiplies on top of it — see
    /// <see cref="MaxExpandBreadth"/>.
    /// </para>
    /// </summary>
    public const int MaxExpansionDepthCeiling = 6;

    /// <summary>
    /// #202/#206: maximum nested <c>$expand</c> depth accepted on the collection read paths, and the
    /// ceiling <c>$levels</c> is resolved and capped to (<c>$levels=max</c> becomes exactly this
    /// value; a numeric <c>$levels=N</c> is clamped to it). A request nesting <c>$expand</c> deeper —
    /// or requesting more <c>$levels</c> — than this is rejected with <c>400</c> before any handler
    /// runs. Defaults to <c>3</c>. Advertised in <c>$metadata</c> as the
    /// <c>Org.OData.Capabilities.V1.ExpandRestrictions/MaxLevels</c> annotation on each entity set so
    /// clients can discover it. Raise it to allow deeper graph queries, or lower it to harden against
    /// them; must be a positive integer <b>no greater than <see cref="MaxExpansionDepthCeiling"/></b>
    /// (#328). Profile-level <see cref="EntitySetProfile{TKey,TModel}.MaxExpansionDepth"/> overrides
    /// this value.
    /// </summary>
    public int MaxExpansionDepth
    {
        get => _maxExpansionDepth;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxExpansionDepth), value, "MaxExpansionDepth must be a positive integer.");
            if (value > MaxExpansionDepthCeiling)
                throw new ArgumentOutOfRangeException(nameof(MaxExpansionDepth), value, ExpansionDepthCeilingMessage(value));
            _maxExpansionDepth = value;
        }
    }

    // #328: the message says WHY, not just what. An implementor who set 15 did so on purpose and is
    // owed the reason it is now refused — otherwise the obvious reading is "arbitrary new limit" and
    // the obvious response is to go looking for a way around it.
    internal static string ExpansionDepthCeilingMessage(int value) =>
        $"MaxExpansionDepth must be no greater than {MaxExpansionDepthCeiling} (requested {value}). " +
        "Relational query translation for a pushed nested $expand is O(3^depth) — EF Core " +
        "re-translates each nested-collection subtree three times with no memoization — so each " +
        "extra level triples the CPU spent building the query before any row is read. Measured on " +
        "a 16-node chain returning ~6 KB: depth 6 = 0.24 s, depth 8 = 3.8 s, depth 10 = 32 s, " +
        "depth 12 = 291 s of single-core CPU for ONE unauthenticated request. If you need a deeper " +
        "graph, fetch it as separate requests, or expand a delegate-backed navigation (which is " +
        "loaded per level rather than as one nested projection) instead of raising this limit.";

    private int _maxExpandBreadth = 50;

    /// <summary>
    /// #429/#202: maximum number of navigation expansions a single request's <c>$expand</c> may
    /// contain, counted across <b>every level of the tree</b>. Defaults to <c>50</c>. A request over
    /// the limit is rejected with <c>400</c> (<c>InvalidQueryOption</c>) before any handler runs.
    /// Must be a positive integer. Profile-level
    /// <see cref="EntitySetProfile{TKey,TModel}.MaxExpandBreadth"/> overrides this value; the
    /// <b>root</b> entity set's resolved value governs the whole request (the same rule
    /// <see cref="MaxExpansionDepth"/> follows).
    /// <para>
    /// <b>Why breadth needs its own guard.</b> <see cref="MaxExpansionDepth"/> bounds one axis only.
    /// Translation cost for a pushed nested projection multiplies by ~3 per level <i>and</i> by the
    /// number of navigations expanded at each level, so depth alone does not bound it. Measured at
    /// the <b>default</b> depth of 3 on a model with six collection navigations, before this guard
    /// existed: 6 navigations per level cost <b>4.1 s of single-core CPU for a 1,952-byte
    /// response</b>, unauthenticated. Nor does the query cache help — each distinct navigation
    /// <i>subset</i> is a distinct EF compiled-query cache key, so an attacker cycling subsets pays
    /// full translation cost every time.
    /// </para>
    /// <para>
    /// <b>Why the count is over the whole tree, not per level.</b> A per-level cap of <c>B</c> with a
    /// depth ceiling of <c>D</c> still admits <c>B^D</c> expansions — at <c>B</c>=6, <c>D</c>=6 that
    /// is 55,986 nodes, which is not a bound in any useful sense. Counting every node in the tree
    /// bounds the two axes together. Counting <i>distinct navigation names</i> would be weaker still:
    /// the most expensive shapes measured reuse six names over six levels.
    /// </para>
    /// <para>
    /// <b>Why 50.</b> It is far above any realistic request — a three-level chain expanding three
    /// navigations at every level is 39 nodes and is already an unusual shape; typical rich requests
    /// are under 15 — and it keeps the worst legal request measurable. Measured on this project's
    /// calibration harness (warm host, SQLite in-memory): at the default depth of 3, a 50-node
    /// <c>$expand</c> costs ~0.4 s (interpolated between 39 nodes = 308 ms and 84 nodes = 699 ms).
    /// At the <i>maximum legal</i> depth of 6, a systematic sweep of every branching vector within
    /// the budget put the worst legal request at <b>1.0-1.4 s</b> (shape <c>[1,1,1,1,2,6]</c>, only
    /// 18 nodes — deep-and-narrow is more expensive per node than flat-and-wide). Unguarded, the same
    /// model reaches 2,850 nodes and <b>36 s</b> for a 111-byte error response; that request now
    /// returns <c>400</c> in <b>56 ms</b>, essentially all of it parsing the URL.
    /// Raise it if your model genuinely needs more; it is a knob precisely because 50 is a judgement
    /// call, not a law.
    /// </para>
    /// <para>
    /// A <c>$levels=N</c> item counts as <c>N</c> (its resolved level count, after clamping),
    /// because that is what it costs — one nested projection level each, exactly like the equivalent
    /// explicit chain. Every other expansion counts as one.
    /// </para>
    /// </summary>
    public int MaxExpandBreadth
    {
        get => _maxExpandBreadth;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxExpandBreadth), value, "MaxExpandBreadth must be a positive integer.");
            _maxExpandBreadth = value;
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
    /// #355: whether a write body is checked against the EDM's own <c>Nullable="false"</c>
    /// annotations before the handler is invoked. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework publishes the nullability of every structural property in its own
    /// <c>$metadata</c>. Before #355 nothing enforced it: a <c>null</c> for a property the CSDL
    /// declares <c>Nullable="false"</c> reached the handler, and the persistence layer's rejection
    /// surfaced as a generic <c>500</c> — a violation the framework could see at its own boundary
    /// reported as a server fault. With this on, the violation is a <c>400</c> and no handler runs.
    /// </para>
    /// <para>
    /// #544: the check fires only on a property the body NAMES with an explicit <c>null</c>. An
    /// omitted property is not a violation on any verb, so the rule is derivable from the wire
    /// alone and does not depend on a CLR initializer the client cannot see. See
    /// <see cref="EntitySetProfile{TKey,TModel}.RequestBodyNullabilityValidationEnabled"/>
    /// for the full statement and the four properties the rule cannot reach.
    /// </para>
    /// <para>
    /// Turn it off for an entity set whose handler legitimately supplies a value the client is not
    /// expected to send (a server-stamped audit column, say).
    /// </para>
    /// </remarks>
    public bool RequestBodyNullabilityValidationEnabled { get; set; } = true;

    /// <summary>
    /// Whether the entity write routes pass nested navigation-property values through to the
    /// handler by default — a "deep insert" graph on <c>POST /{EntitySet}</c> (OData §11.4.2.2)
    /// or a "deep update" graph on <c>PUT</c>/<c>PATCH /{EntitySet}({key})</c> (OData 4.01
    /// §11.4.3.1). Defaults to <c>false</c>: nested navigation values are stripped before
    /// <c>Post</c>/<c>Put</c> is invoked and never enter the <c>Delta&lt;TModel&gt;</c> handed
    /// to <c>Patch</c>, so a handler that doesn't expect a graph never silently persists only
    /// part of it. Set to <c>true</c> to opt every entity set in, or override per profile via
    /// <c>AllowDeepWrites</c>. When enabled, the handler owns atomic persistence of the whole
    /// graph (e.g. a single EF Core <c>SaveChanges</c>).
    /// </summary>
    public bool AllowDeepWrites { get; set; }

    /// <summary>
    /// Renamed to <see cref="AllowDeepWrites"/> in 1.6.0. Kept as a forwarding property so an
    /// assembly compiled against 1.5.0 keeps binding; it reads and writes
    /// <see cref="AllowDeepWrites"/>, so the two can never disagree.
    /// </summary>
    // #457: see EntitySetProfile.AllowDeepInsert for why the name changed and why this member
    // stays. One storage location, two names -- never two fields.
    [Obsolete("Renamed to AllowDeepWrites: the flag governs nested-graph handling on every write " +
              "verb -- deep insert (POST, OData §11.4.2.2) and deep update (PUT/PATCH, OData 4.01 " +
              "§11.4.3.1) -- not deep insert alone. Forwards to AllowDeepWrites.")]
    public bool AllowDeepInsert
    {
        get => AllowDeepWrites;
        set => AllowDeepWrites = value;
    }

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
