using System;
using System.Collections.Generic;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// Shared entity type used by both the OhData host and the Microsoft.AspNetCore.OData host so
/// the two pipelines are exercised against an identical EDM shape and an identical dataset.
/// Must be public so Microsoft.AspNetCore.OData's model builder and controller action binder can
/// materialise it via reflection from the same assembly.
/// </summary>
public sealed class BenchWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Nav-shaped collection property (complex-type collection under EDM conventions, since
    /// <see cref="BenchTag"/> has no key). Included so the dataset shape matches a realistic
    /// entity with a related collection, per the benchmark brief.
    /// </summary>
    public List<BenchTag> Tags { get; set; } = new();

    public BenchWidget Clone() => new()
    {
        Id = Id,
        Name = Name,
        Category = Category,
        Price = Price,
        IsActive = IsActive,
        CreatedAt = CreatedAt,
        Tags = new List<BenchTag>(Tags),
    };
}

/// <summary>Related record hung off <see cref="BenchWidget.Tags"/>. No key — modelled as a complex type.</summary>
public sealed class BenchTag
{
    public string Label { get; set; } = "";
    public int Weight { get; set; }
}
