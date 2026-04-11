using System;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using OhData.Client.Internal;

namespace OhData.Client.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="FilterTranslator.Translate{T}"/>.
/// Measures the cost of translating LINQ expression trees to OData $filter strings.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class FilterTranslatorBenchmarks
{
    // Pre-compiled expressions — the same expression object is reused each iteration
    // to isolate the translation cost from expression construction overhead.
    private static readonly Expression<Func<BenchWidget, bool>> _simple =
        x => x.Price > 10;

    private static readonly Expression<Func<BenchWidget, bool>> _medium =
        x => x.Price > 10 && x.Name.StartsWith("W");

    private static readonly Expression<Func<BenchWidget, bool>> _complex =
        x => (x.Price > 10 && x.Name.StartsWith("W")) || (x.Price < 5 && x.IsActive);

    private static readonly Expression<Func<BenchWidget, bool>> _withCapture;

    static FilterTranslatorBenchmarks()
    {
        decimal threshold = 99.9m;
        string  prefix    = "Widget";
        _withCapture = x => x.Price > threshold && x.Name.StartsWith(prefix);
    }

    /// <summary>Simple single-comparison filter: <c>Price gt 10</c></summary>
    [Benchmark(Baseline = true)]
    public string Simple() => FilterTranslator.Translate(_simple);

    /// <summary>Two conditions with AND: <c>(Price gt 10) and (startswith(Name,'W'))</c></summary>
    [Benchmark]
    public string Medium() => FilterTranslator.Translate(_medium);

    /// <summary>Four conditions with AND/OR nesting.</summary>
    [Benchmark]
    public string Complex() => FilterTranslator.Translate(_complex);

    /// <summary>Filter with two captured variables — exercises closure evaluation.</summary>
    [Benchmark]
    public string WithCapturedVariables() => FilterTranslator.Translate(_withCapture);
}

/// <summary>
/// Model type used in filter benchmarks.
/// </summary>
public sealed class BenchWidget
{
    public int     Id       { get; set; }
    public string  Name     { get; set; } = "";
    public decimal Price    { get; set; }
    public bool    IsActive { get; set; }
}
