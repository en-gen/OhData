namespace OhData.AspNetCore;

/// <summary>
/// Endpoint metadata that describes which OData query options are supported
/// by a collection GET endpoint. Used by OpenAPI operation filters to document
/// the available query parameters.
/// </summary>
public sealed record OhDataQueryOptionsMetadata(
    bool FilterEnabled,
    bool OrderByEnabled,
    bool SelectEnabled,
    bool ExpandEnabled,
    bool CountEnabled,
    bool SearchEnabled,
    int? MaxTop);
