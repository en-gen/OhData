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
}
