using System;
using System.Collections.Generic;
using System.Linq;

namespace OhData.Server.Benchmarks.Model;

/// <summary>
/// Deterministic, seed-free dataset generator. Both hosts call <see cref="CreateWidgets"/>
/// independently at startup so each owns its own list instance (no shared mutable state
/// between the two servers) while producing byte-for-byte identical data.
/// </summary>
internal static class BenchmarkData
{
    public const int WidgetCount = 1000;
    public const int PageSize = 100;
    public const int LookupId = 500;

    private static readonly string[] Categories = { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };

    public static List<BenchWidget> CreateWidgets()
    {
        var epoch = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return Enumerable.Range(1, WidgetCount)
            .Select(i => new BenchWidget
            {
                Id = i,
                Name = $"Widget-{i:D4}",
                Category = Categories[i % Categories.Length],
                Price = Math.Round(i * 0.99m, 2),
                IsActive = i % 3 != 0,
                CreatedAt = epoch.AddMinutes(i),
                Tags = Enumerable.Range(0, i % 4)
                    .Select(t => new BenchTag { Label = $"tag-{(i + t) % 10}", Weight = (i + t) % 7 })
                    .ToList(),
            })
            .ToList();
    }
}
