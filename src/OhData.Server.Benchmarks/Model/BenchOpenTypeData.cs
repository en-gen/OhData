using System;
using System.Collections.Generic;

namespace OhData.Server.Benchmarks.Model;

// ── Models for the open-type serialize-path measurement (#389) ───────────────────────────────────
//
// These are deliberately NOT the BenchWidget/BenchOrg models the server-comparison suite uses. That
// suite measures a whole HTTP pipeline against Microsoft.AspNetCore.OData; this one measures a
// single System.Text.Json call, and needs a model shaped by exactly one thing: whether
// ODataConventionModelBuilder infers a dynamic-property container on it.

/// <summary>
/// Open complex type: <see cref="Bag"/> is an <see cref="IDictionary{TKey,TValue}"/> member, which
/// is the only thing <c>ODataConventionModelBuilder</c> needs to mark this type
/// <c>OpenType="true"</c> and record a <c>DynamicPropertyDictionaryAnnotation</c> naming that member
/// — the annotation <c>OpenTypeJsonOptions</c> reads. No attribute is involved, here or in the
/// shipped path.
/// </summary>
/// <remarks>
/// <see cref="Region"/> and <see cref="Tier"/> exist so the type has DECLARED properties. That is
/// load-bearing for arm A: the pre-<c>cab1de7</c> wrapper bailed out entirely when
/// <c>declaredNames.Count == 0</c>, so a bag-only type would have measured arm A with no getter
/// wrapper at all and made the comparison meaningless.
/// </remarks>
public sealed class BenchOpenMeta
{
    public string? Region { get; set; }
    public int Tier { get; set; }
    public IDictionary<string, object?>? Bag { get; set; }
}

/// <summary>Entity root carrying the open complex type. Sealed: keeps the convention builder's
/// assembly-wide derived-type discovery from pulling anything else into the model.</summary>
public sealed class BenchOpenRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Amount { get; set; }
    public BenchOpenMeta? Meta { get; set; }
}

/// <summary>
/// The control model: identical declared shape to <see cref="BenchOpenMeta"/> minus the dictionary
/// member, so the EDM marks nothing open, <c>BuildOpenComplexTypeContainerMap</c> returns
/// <c>Empty</c>, and <c>OpenTypeJsonOptions.Build</c> hands the base options straight back
/// reference-equal. Every arm therefore serializes through the SAME options instance.
/// </summary>
public sealed class BenchClosedMeta
{
    public string? Region { get; set; }
    public int Tier { get; set; }
}

public sealed class BenchClosedRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Amount { get; set; }
    public BenchClosedMeta? Meta { get; set; }
}

/// <summary>
/// Deterministic page builders for the four key shapes. No <c>Bogus</c> and no seed: every value
/// here is a pure function of its indices, so all four arms of a scenario serialize byte-identical
/// data and the only thing that varies between them is the per-key check.
/// </summary>
public static class BenchOpenTypeData
{
    public const int RowCount = 1000;
    public const int KeysPerRow = 20;

    /// <summary>
    /// The common case: a fixed dynamic-property vocabulary repeated on every row. 20 names, all
    /// valid <c>odataIdentifier</c>s, all ASCII — so the shipped validator takes its
    /// <c>SearchValues</c> fast path and, by design, never consults the validated-key cache.
    /// Average length 7.3 characters, chosen to sit close to the distinct-key shapes below so the
    /// scenarios differ in key IDENTITY, not in how many characters the scan has to read.
    /// </summary>
    private static readonly string[] RepeatingVocabulary =
    {
        "tier", "region", "channel", "segment", "campaign",
        "cohort", "source", "medium", "variant", "bucket",
        "plan", "currency", "locale", "timezone", "partner",
        "tenant", "program", "tranche", "priority", "score",
    };

    /// <summary>Repeating ASCII keys — 20 distinct names across the whole page.</summary>
    public static IReadOnlyList<object?> BuildRepeatingAscii() =>
        BuildOpenPage(static (row, index) => RepeatingVocabulary[index]);

    /// <summary>
    /// 20,000 distinct ASCII keys. Every key is still a valid identifier (leading <c>k</c>), so the
    /// fast path decides all of them — this measures the fast path with zero string reuse and
    /// hostile cache locality, NOT the validated-key cache, which the shipped code scopes to the
    /// non-ASCII fallback and which an all-ASCII page never touches.
    /// </summary>
    public static IReadOnlyList<object?> BuildDistinctAscii() =>
        BuildOpenPage(static (row, index) => $"k{row}_{index}");

    /// <summary>
    /// 20,000 distinct NON-ASCII keys — the shape that actually exercises the 1024-entry cache, and
    /// the true worst case for it: the table fills during the first operation and then freezes, so
    /// every later key pays a failed ordinal lookup (which hashes the whole string) AND the full
    /// rune-and-category walk. U+03BA GREEK SMALL LETTER KAPPA is <c>Ll</c>, so it is a legal
    /// leading character and each key is a valid identifier — an invalid one would throw and abort
    /// the serialize rather than being measured.
    /// </summary>
    public static IReadOnlyList<object?> BuildDistinctNonAscii() =>
        BuildOpenPage(static (row, index) => $"κ{row}_{index}");

    /// <summary>The control page: no open complex type anywhere in the graph.</summary>
    public static IReadOnlyList<object?> BuildClosed()
    {
        object?[] rows = new object?[RowCount];
        for (int row = 0; row < RowCount; row++)
        {
            rows[row] = new BenchClosedRow
            {
                Id = row,
                Name = RowName(row),
                CreatedAt = RowCreatedAt(row),
                Amount = RowAmount(row),
                Meta = new BenchClosedMeta { Region = RowRegion(row), Tier = row % 5 },
            };
        }
        return rows;
    }

    // Typed as IReadOnlyList<object?> and populated with BOXED rows on purpose: that is the exact
    // shape OhDataEndpointFactory.SerializeBoundedCollection hands System.Text.Json (see its
    // remarks — element types are resolved per element by runtime type), so the denominator this
    // measurement divides its delta into is the framework's real serialize cost rather than a
    // cheaper strongly-typed-array approximation of it.
    private static IReadOnlyList<object?> BuildOpenPage(Func<int, int, string> keyFor)
    {
        object?[] rows = new object?[RowCount];
        for (int row = 0; row < RowCount; row++)
        {
            Dictionary<string, object?> bag = new Dictionary<string, object?>(KeysPerRow, StringComparer.Ordinal);
            for (int index = 0; index < KeysPerRow; index++)
            {
                bag[keyFor(row, index)] = BagValue(row, index);
            }
            rows[row] = new BenchOpenRow
            {
                Id = row,
                Name = RowName(row),
                CreatedAt = RowCreatedAt(row),
                Amount = RowAmount(row),
                Meta = new BenchOpenMeta { Region = RowRegion(row), Tier = row % 5, Bag = bag },
            };
        }
        return rows;
    }

    // A rotating mix of the scalar kinds a dynamic bag realistically holds. Identical across all
    // three open scenarios, so the value-writing half of the serialize cost is a constant and the
    // only difference between scenarios is the keys.
    private static object? BagValue(int row, int index) => (index % 4) switch
    {
        0 => $"value-{row}-{index}",
        1 => row * 31 + index,
        2 => (row + index) % 2 == 0,
        _ => (row + index) * 1.5d,
    };

    private static string RowName(int row) => $"Row {row}";
    private static DateTimeOffset RowCreatedAt(int row) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(row);
    private static decimal RowAmount(int row) => 10m + row * 0.25m;
    private static string RowRegion(int row) => (row % 3) switch
    {
        0 => "us-east",
        1 => "eu-west",
        _ => "ap-south",
    };
}
