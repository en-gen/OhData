using System;

namespace OhData;

/// <summary>
/// Endpoint metadata that describes which OData query options are supported
/// by an OhData endpoint. Used by OpenAPI operation filters to document
/// the available query parameters.
/// </summary>
/// <remarks>
/// #467: every field is read as <em>"this route honours this option"</em>, never as
/// "this route is of kind X". The metadata is attached to five route shapes -- the three
/// collection GET paths (Priority-1, GetQueryable, GetAll), <c>/$count</c>, and GetById --
/// so a field that means one thing on a collection and another on <c>/$count</c> produces
/// a document that advertises options the server ignores or rejects. In particular
/// <see cref="CountEnabled"/> means "the <c>$count</c> <em>option</em> is honoured here",
/// NOT "this route is the <c>/$count</c> route", and <see cref="TopSkipSupported"/> exists
/// because <c>$top</c>/<c>$skip</c> used to be documented unconditionally.
/// </remarks>
/// <param name="FilterEnabled">The <c>$filter</c> option is honoured on this route.</param>
/// <param name="OrderByEnabled">The <c>$orderby</c> option is honoured on this route.</param>
/// <param name="SelectEnabled">The <c>$select</c> option is honoured on this route.</param>
/// <param name="ExpandEnabled">The <c>$expand</c> option is honoured on this route.</param>
/// <param name="CountEnabled">
/// The <c>$count</c> <em>query option</em> (<c>$count=true</c>, inline count in the response
/// envelope) is honoured on this route. The <c>/$count</c> route itself sets this to
/// <see langword="false"/>: it has no envelope to put an inline count in and ignores the option.
/// </param>
/// <param name="SearchEnabled">The <c>$search</c> option is honoured on this route.</param>
/// <param name="MaxTop">The server-side <c>$top</c> ceiling, when one is configured.</param>
/// <param name="TopSkipSupported">
/// The <c>$top</c> and <c>$skip</c> options are honoured on this route. <see langword="false"/>
/// for the single-entity GetById route and for <c>/$count</c>, both of which ignore them outright.
/// </param>
public sealed record OhDataQueryOptionsMetadata(
    bool FilterEnabled,
    bool OrderByEnabled,
    bool SelectEnabled,
    bool ExpandEnabled,
    bool CountEnabled,
    bool SearchEnabled,
    int? MaxTop,
    bool TopSkipSupported)
{
    // #467: TopSkipSupported was ADDED to the primary constructor, which removes the 7-parameter
    // ctor and Deconstruct from the public surface -- an API break the PackageValidation gate
    // against the 1.5.0 baseline correctly rejects. These two members restore it.
    //
    // The forwarded value is `true`, and that is faithful rather than convenient: before #467
    // every operation transformer added $top and $skip UNCONDITIONALLY wherever this metadata was
    // present, so "constructed with the 7-parameter ctor" meant exactly "this route documents
    // $top/$skip". An external caller who attached this metadata to their own route got that
    // behaviour, and still does.
    //
    // Deliberately NOT a defaulted parameter on the primary ctor: the correct value differs per
    // route (false for GetById and /$count, true for the three collection GETs), so a default
    // would let an OhData call site omit it and silently restore the defect #467 fixes. The
    // obsolete overload cannot do that -- every in-repo call site passes all eight arguments, and
    // this pair exists solely so a 1.5.0-compiled assembly keeps binding.
    /// <summary>
    /// Binary-compatibility overload for callers compiled against 1.5.0, which had no
    /// <see cref="TopSkipSupported"/>. Forwards it as <see langword="true"/> — the behaviour every
    /// operation transformer applied unconditionally before #467.
    /// </summary>
    /// <param name="FilterEnabled">The <c>$filter</c> option is honoured on this route.</param>
    /// <param name="OrderByEnabled">The <c>$orderby</c> option is honoured on this route.</param>
    /// <param name="SelectEnabled">The <c>$select</c> option is honoured on this route.</param>
    /// <param name="ExpandEnabled">The <c>$expand</c> option is honoured on this route.</param>
    /// <param name="CountEnabled">The <c>$count</c> query option is honoured on this route.</param>
    /// <param name="SearchEnabled">The <c>$search</c> option is honoured on this route.</param>
    /// <param name="MaxTop">The server-side <c>$top</c> ceiling, when one is configured.</param>
    [Obsolete("Use the constructor that takes TopSkipSupported. This overload assumes true, " +
              "which is what the pre-#467 transformers did unconditionally.")]
    public OhDataQueryOptionsMetadata(
        bool FilterEnabled,
        bool OrderByEnabled,
        bool SelectEnabled,
        bool ExpandEnabled,
        bool CountEnabled,
        bool SearchEnabled,
        int? MaxTop)
        : this(FilterEnabled, OrderByEnabled, SelectEnabled, ExpandEnabled,
               CountEnabled, SearchEnabled, MaxTop, TopSkipSupported: true)
    {
    }

    /// <summary>
    /// Binary-compatibility overload for callers compiled against 1.5.0. Yields the seven
    /// pre-#467 members; use the compiler-generated eight-member <c>Deconstruct</c> to also
    /// obtain <see cref="TopSkipSupported"/>.
    /// </summary>
    /// <param name="FilterEnabled">The <c>$filter</c> option is honoured on this route.</param>
    /// <param name="OrderByEnabled">The <c>$orderby</c> option is honoured on this route.</param>
    /// <param name="SelectEnabled">The <c>$select</c> option is honoured on this route.</param>
    /// <param name="ExpandEnabled">The <c>$expand</c> option is honoured on this route.</param>
    /// <param name="CountEnabled">The <c>$count</c> query option is honoured on this route.</param>
    /// <param name="SearchEnabled">The <c>$search</c> option is honoured on this route.</param>
    /// <param name="MaxTop">The server-side <c>$top</c> ceiling, when one is configured.</param>
    [Obsolete("Use the Deconstruct that yields TopSkipSupported.")]
    public void Deconstruct(
        out bool FilterEnabled,
        out bool OrderByEnabled,
        out bool SelectEnabled,
        out bool ExpandEnabled,
        out bool CountEnabled,
        out bool SearchEnabled,
        out int? MaxTop)
    {
        FilterEnabled = this.FilterEnabled;
        OrderByEnabled = this.OrderByEnabled;
        SelectEnabled = this.SelectEnabled;
        ExpandEnabled = this.ExpandEnabled;
        CountEnabled = this.CountEnabled;
        SearchEnabled = this.SearchEnabled;
        MaxTop = this.MaxTop;
    }
}
