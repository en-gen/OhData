using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace OhData.Server.Benchmarks.Streaming;

// SPIKE #867 -- fixture for the buffered-vs-streamed collection measurements. Flat, ~200 bytes of
// JSON per row, so row count (not row shape) is the variable.
public sealed class StreamBenchRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class StreamBenchDbContext : DbContext
{
    public StreamBenchDbContext(DbContextOptions<StreamBenchDbContext> options) : base(options) { }

    public DbSet<StreamBenchRow> Rows => Set<StreamBenchRow>();
}

internal static class StreamBenchData
{
    internal const int MaxRows = 10_000;

    internal static List<StreamBenchRow> Create(int count = MaxRows)
    {
        var epoch = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(1, count).Select(i => new StreamBenchRow
        {
            Id = i,
            Name = $"Row number {i}",
            Category = $"category-{i % 17}",
            Price = (i % 997) * 1.07m,
            IsActive = i % 3 != 0,
            CreatedAt = epoch.AddMinutes(i),
            Notes = $"Deterministic note text for row {i}, long enough to make the payload realistic.",
        }).ToList();
    }
}
