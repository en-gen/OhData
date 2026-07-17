using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OhData.Abstractions;

namespace OhData.AspNetCore.Tests;

// ── Shared fixtures for FilterExpressionTests / QueryOptionCompositionTests ────
//
// A single ~20-row dataset with varied strings (mixed case, unicode), decimals,
// doubles, dates, nullable properties and a primitive collection — used to give
// full $filter expression-language and $orderby/$select/$expand composition
// coverage an end-to-end HTTP contract test against the GetQueryable path.

internal class QueryOptionItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public decimal Price { get; set; }
    public double Weight { get; set; }
    public int Quantity { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int? Rank { get; set; }
    public List<string> Tags { get; set; } = new();
}

internal static class QueryOptionData
{
    public static readonly List<QueryOptionItem> Items = new()
    {
        new() { Id = 1, Name = "Alpha Widget", Category = "Tools", Price = 10.25m, Weight = 2.5, Quantity = 4, IsActive = true, CreatedUtc = new DateTime(2023, 1, 15, 8, 30, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 1, 15, 8, 30, 0, TimeSpan.Zero), Rank = 1, Tags = new() { "red", "large" } },
        new() { Id = 2, Name = "Beta Gadget", Category = "Tools", Price = 15.75m, Weight = 3.1, Quantity = 7, IsActive = false, CreatedUtc = new DateTime(2023, 2, 20, 14, 45, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 2, 20, 14, 45, 0, TimeSpan.Zero), Rank = 2, Tags = new() { "blue" } },
        new() { Id = 3, Name = "alpha widget", Category = "Parts", Price = 8.00m, Weight = 1.2, Quantity = 2, IsActive = true, CreatedUtc = new DateTime(2023, 3, 10, 9, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 3, 10, 9, 0, 0, TimeSpan.Zero), Rank = null, Tags = new() },
        new() { Id = 4, Name = "GAMMA TOOL", Category = "Parts", Price = 22.499m, Weight = 5.75, Quantity = 10, IsActive = true, CreatedUtc = new DateTime(2023, 4, 5, 23, 59, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 4, 5, 23, 59, 0, TimeSpan.Zero), Rank = 4, Tags = new() { "green", "small" } },
        new() { Id = 5, Name = "Delta Item", Category = null, Price = 3.14m, Weight = 0.9, Quantity = 1, IsActive = false, CreatedUtc = new DateTime(2023, 5, 18, 0, 15, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 5, 18, 0, 15, 0, TimeSpan.Zero), Rank = 5, Tags = new() },
        new() { Id = 6, Name = "Café Table", Category = "Electronics", Price = 99.99m, Weight = 12.4, Quantity = 3, IsActive = true, CreatedUtc = new DateTime(2023, 6, 25, 11, 11, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 6, 25, 11, 11, 0, TimeSpan.Zero), Rank = null, Tags = new() { "red" } },
        new() { Id = 7, Name = "Epsilon Widget", Category = "Electronics", Price = 45.5m, Weight = 7.25, Quantity = 6, IsActive = true, CreatedUtc = new DateTime(2023, 7, 30, 18, 20, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 7, 30, 18, 20, 0, TimeSpan.Zero), Rank = 7, Tags = new() { "blue", "large" } },
        new() { Id = 8, Name = "Zeta Part", Category = null, Price = 12.60m, Weight = 2.05, Quantity = 8, IsActive = false, CreatedUtc = new DateTime(2023, 8, 12, 6, 5, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 8, 12, 6, 5, 0, TimeSpan.Zero), Rank = 8, Tags = new() },
        new() { Id = 9, Name = "Eta Gadget", Category = "Office", Price = 7.49m, Weight = 1.75, Quantity = 0, IsActive = true, CreatedUtc = new DateTime(2023, 9, 1, 12, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 9, 1, 12, 0, 0, TimeSpan.Zero), Rank = 9, Tags = new() { "green" } },
        new() { Id = 10, Name = "Theta Tool", Category = "Office", Price = 18.20m, Weight = 4.4, Quantity = 5, IsActive = true, CreatedUtc = new DateTime(2023, 10, 22, 3, 45, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 10, 22, 3, 45, 0, TimeSpan.Zero), Rank = null, Tags = new() { "small" } },
        new() { Id = 11, Name = "Ω-Beam", Category = "Electronics", Price = 33.33m, Weight = 9.9, Quantity = 11, IsActive = false, CreatedUtc = new DateTime(2023, 11, 11, 21, 21, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 11, 11, 21, 21, 0, TimeSpan.Zero), Rank = 11, Tags = new() { "red", "blue" } },
        new() { Id = 12, Name = "naïve Item", Category = "Parts", Price = 5.55m, Weight = 0.5, Quantity = 2, IsActive = true, CreatedUtc = new DateTime(2023, 12, 31, 23, 59, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2023, 12, 31, 23, 59, 0, TimeSpan.Zero), Rank = 12, Tags = new() },
        new() { Id = 13, Name = "Iota Widget", Category = "Tools", Price = 27.80m, Weight = 6.6, Quantity = 9, IsActive = true, CreatedUtc = new DateTime(2024, 1, 5, 4, 4, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 1, 5, 4, 4, 0, TimeSpan.Zero), Rank = 13, Tags = new() { "large" } },
        new() { Id = 14, Name = "Kappa Gadget", Category = "Tools", Price = 14.15m, Weight = 3.3, Quantity = 4, IsActive = false, CreatedUtc = new DateTime(2024, 2, 14, 15, 30, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 2, 14, 15, 30, 0, TimeSpan.Zero), Rank = 14, Tags = new() },
        new() { Id = 15, Name = "Lambda Part", Category = null, Price = 60.00m, Weight = 15.0, Quantity = 20, IsActive = true, CreatedUtc = new DateTime(2024, 3, 19, 10, 10, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 3, 19, 10, 10, 0, TimeSpan.Zero), Rank = null, Tags = new() { "green", "large" } },
        new() { Id = 16, Name = "Mu Item", Category = "Office", Price = 2.99m, Weight = 0.25, Quantity = 1, IsActive = false, CreatedUtc = new DateTime(2024, 4, 8, 2, 2, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 4, 8, 2, 2, 0, TimeSpan.Zero), Rank = 16, Tags = new() },
        new() { Id = 17, Name = "Nu Widget", Category = "Electronics", Price = 88.88m, Weight = 22.2, Quantity = 15, IsActive = true, CreatedUtc = new DateTime(2024, 5, 27, 19, 19, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 5, 27, 19, 19, 0, TimeSpan.Zero), Rank = 17, Tags = new() { "blue" } },
        new() { Id = 18, Name = "Xi Tool", Category = "Tools", Price = 41.41m, Weight = 8.8, Quantity = 6, IsActive = true, CreatedUtc = new DateTime(2024, 6, 16, 7, 7, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 6, 16, 7, 7, 0, TimeSpan.Zero), Rank = 18, Tags = new() { "red", "small" } },
        new() { Id = 19, Name = "Omicron Gadget", Category = null, Price = 9.09m, Weight = 1.1, Quantity = 3, IsActive = false, CreatedUtc = new DateTime(2024, 7, 4, 13, 13, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 7, 4, 13, 13, 0, TimeSpan.Zero), Rank = null, Tags = new() },
        new() { Id = 20, Name = "Pi Part", Category = "Parts", Price = 100.00m, Weight = 25.5, Quantity = 12, IsActive = true, CreatedUtc = new DateTime(2024, 8, 23, 16, 16, 0, DateTimeKind.Utc), UpdatedAt = new DateTimeOffset(2024, 8, 23, 16, 16, 0, TimeSpan.Zero), Rank = 20, Tags = new() { "green" } },
    };
}

/// <summary>
/// GetQueryable-backed profile over <see cref="QueryOptionData.Items"/> with all
/// query-option categories enabled — the "full expression language" test target.
/// </summary>
internal class QueryOptionProfile : EntitySetProfile<int, QueryOptionItem>
{
    public QueryOptionProfile() : base(x => x.Id)
    {
        EntitySetName = "QueryOptionItems";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        CountEnabled = true;

        GetQueryable = (ct) => Task.FromResult(QueryOptionData.Items.AsQueryable());
        GetById = (id, ct) => Task.FromResult(QueryOptionData.Items.FirstOrDefault(x => x.Id == id));
    }
}

// ── $expand + $select + $filter composition fixtures ───────────────────────────

internal class QueryOptionParent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public IEnumerable<QueryOptionChild>? Children { get; set; }
}

internal class QueryOptionChild
{
    public int Id { get; set; }
    public int ParentId { get; set; }
    public string Name { get; set; } = "";
}

internal class QueryOptionExpandProfile : EntitySetProfile<int, QueryOptionParent>
{
    private static readonly List<QueryOptionParent> _parents = new()
    {
        new() { Id = 1, Name = "Parent Alpha", IsActive = true },
        new() { Id = 2, Name = "Parent Beta", IsActive = false },
        new() { Id = 3, Name = "Parent Gamma", IsActive = true },
    };

    private static readonly List<QueryOptionChild> _children = new()
    {
        new() { Id = 1, ParentId = 1, Name = "Child1a" },
        new() { Id = 2, ParentId = 1, Name = "Child1b" },
        new() { Id = 3, ParentId = 2, Name = "Child2a" },
    };

    public QueryOptionExpandProfile() : base(x => x.Id)
    {
        EntitySetName = "QueryOptionExpandParents";
        FilterEnabled = true;
        OrderByEnabled = true;
        SelectEnabled = true;
        ExpandEnabled = true;
        CountEnabled = true;

        GetQueryable = (ct) => Task.FromResult(_parents.AsQueryable());
        GetById = (id, ct) => Task.FromResult(_parents.FirstOrDefault(p => p.Id == id));

        HasMany(x => x.Children!,
            getAll: (parentId, ct) =>
                Task.FromResult<IEnumerable<QueryOptionChild>>(_children.Where(c => c.ParentId == parentId)));
    }
}

// ── round() midpoint (RoundingMode) fixtures ───────────────────────────────────
//
// A dedicated dataset/profiles rather than reusing QueryOptionData.Items: none of the
// existing Price/Weight values happen to land on a midpoint where round-half-away-from-zero
// and round-half-to-even (banker's) actually disagree (see FilterExpressionTests for why -
// e.g. 45.5 rounds to 46 either way since 46 is even). Two profiles expose the same data:
// one at the framework default (RoundingMode.SpecCompliant) and one opted into
// RoundingMode.BankersRounding, so both branches of the setting get end-to-end HTTP coverage.

internal class RoundingModeItem
{
    public int Id { get; set; }
    public double Value { get; set; }
    public decimal Amount { get; set; }
}

internal static class RoundingModeData
{
    // Value/Amount pairs deliberately sit on integer midpoints where the two rounding modes
    // disagree: away-from-zero rounds .5 away from zero unconditionally, banker's rounds to
    // the nearest even integer. That happens whenever the integer part closer to zero is even
    // (2.5, -2.5, 4.5, -4.5 below) - see FilterExpressionTests for the full explanation.
    public static readonly List<RoundingModeItem> Items = new()
    {
        new() { Id = 1, Value = 2.5, Amount = 2.5m },
        new() { Id = 2, Value = -2.5, Amount = -2.5m },
        new() { Id = 3, Value = 4.5, Amount = 4.5m },
        new() { Id = 4, Value = -4.5, Amount = -4.5m },
    };
}

internal class RoundingModeProfile : EntitySetProfile<int, RoundingModeItem>
{
    public RoundingModeProfile() : base(x => x.Id)
    {
        EntitySetName = "RoundingModeItems";
        FilterEnabled = true;
        // RoundingMode left null -> inherits EntitySetDefaults.RoundingMode (SpecCompliant).

        GetQueryable = (ct) => Task.FromResult(RoundingModeData.Items.AsQueryable());
        GetById = (id, ct) => Task.FromResult(RoundingModeData.Items.FirstOrDefault(x => x.Id == id));
    }
}

internal class RoundingModeBankersProfile : EntitySetProfile<int, RoundingModeItem>
{
    public RoundingModeBankersProfile() : base(x => x.Id)
    {
        EntitySetName = "RoundingModeBankersItems";
        FilterEnabled = true;
        RoundingMode = OhData.Abstractions.RoundingMode.BankersRounding;

        GetQueryable = (ct) => Task.FromResult(RoundingModeData.Items.AsQueryable());
        GetById = (id, ct) => Task.FromResult(RoundingModeData.Items.FirstOrDefault(x => x.Id == id));
    }
}
