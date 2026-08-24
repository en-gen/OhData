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
    bool TopSkipSupported);
