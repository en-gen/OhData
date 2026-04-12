using System;

namespace OhData.Abstractions;

public class EntitySetDefaults
{
    public bool SelectEnabled { get; set; }
    public bool ExpandEnabled { get; set; }
    public bool FilterEnabled { get; set; }
    public bool OrderByEnabled { get; set; }
    public bool CountEnabled { get; set; }
    private int? _maxTop = 1000;
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
