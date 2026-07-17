using System.Net.Http;
using BenchmarkDotNet.Attributes;
using OhData.Client;

namespace OhData.Client.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="EntitySetClient{T}.BuildCollectionUrl"/>.
/// Measures URL construction overhead for different combinations of query options.
/// </summary>
[MemoryDiagnoser]
public class BuildCollectionUrlBenchmarks
{
    private EntitySetClient<BenchWidget> _noOptions = null!;
    private EntitySetClient<BenchWidget> _filterOnly = null!;
    private EntitySetClient<BenchWidget> _allOptions = null!;

    [GlobalSetup]
    public void Setup()
    {
        var client = new OhDataClient(new HttpClient { BaseAddress = new System.Uri("http://localhost/") });

        _noOptions = client.For<BenchWidget>("Widgets");

        _filterOnly = client.For<BenchWidget>("Widgets")
            .Filter(x => x.Price > 10);

        _allOptions = client.For<BenchWidget>("Widgets")
            .Filter(x => x.Price > 10 && x.Name.StartsWith("W"))
            .Select(x => new { x.Id, x.Name, x.Price })
            .OrderByDescending(x => x.Price)
            .Top(20)
            .Skip(40)
            .IncludeCount();
    }

    /// <summary>No options — exercises the fast-path early return.</summary>
    [Benchmark(Baseline = true)]
    public string NoOptions() => _noOptions.BuildCollectionUrl();

    /// <summary>Single $filter — common case.</summary>
    [Benchmark]
    public string FilterOnly() => _filterOnly.BuildCollectionUrl();

    /// <summary>All 7 query options set — maximum URL-building work.</summary>
    [Benchmark]
    public string AllOptions() => _allOptions.BuildCollectionUrl();
}
