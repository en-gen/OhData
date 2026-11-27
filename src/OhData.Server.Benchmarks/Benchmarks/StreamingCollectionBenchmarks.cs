using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using OhData.Server.Benchmarks.Streaming;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// SPIKE #867: buffered vs streamed top-level collection GET, {GetQueryable, GetODataQueryable} x
/// {EF SQLite, in-memory} x {100, 1000, 10000} rows, over the in-process TestServer. The body is
/// drained into a fixed buffer so the client side allocates nothing proportional to the payload.
/// Peak memory and time-to-first-byte are not what BenchmarkDotNet measures; see StreamProbe.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class StreamingCollectionBenchmarks
{
    private StreamBenchHost _host = null!;
    private readonly byte[] _drain = new byte[16 * 1024];
    private string _url = "";

    [Params(StreamBackendParam.InMemory, StreamBackendParam.EfSqlite)]
    public StreamBackendParam Backend { get; set; }

    [Params(StreamRouteParam.GetQueryable, StreamRouteParam.GetODataQueryable)]
    public StreamRouteParam Route { get; set; }

    [Params(100, 1000, 10000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = await StreamBenchHost.StartAsync(kestrel: false);
        _url = StreamBenchHost.Url((StreamBackend)(int)Backend, (StreamRoute)(int)Route, Rows);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [Benchmark(Baseline = true)]
    public async Task<long> Buffered()
    {
        _host.Registration.StreamCollections = false;
        return await GetAsync();
    }

    [Benchmark]
    public async Task<long> Streamed()
    {
        _host.Registration.StreamCollections = true;
        return await GetAsync();
    }

    private async Task<long> GetAsync()
    {
        using HttpResponseMessage response = await _host.Client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(_drain)) > 0) total += read;
        return total;
    }
}

// BenchmarkDotNet [Params] values must be public types; the host enums are internal.
public enum StreamBackendParam { InMemory = 0, EfSqlite = 1 }

public enum StreamRouteParam { GetQueryable = 0, GetODataQueryable = 1 }
