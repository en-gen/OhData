using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace OhData.Server.Benchmarks.Benchmarks;

/// <summary>
/// Shared TestServer host lifecycle for the two server-comparison benchmark classes
/// (<see cref="ServerComparisonBenchmarks"/> for the <c>List&lt;T&gt;</c>-backed <c>BenchWidget</c>
/// scenarios, <see cref="ExpandComparisonBenchmarks"/> for the EF Core/Sqlite-backed <c>$expand</c>/
/// <c>$levels</c> scenarios). Both hosts serve every entity set regardless of which concrete class is
/// running (see <c>BenchmarkHosts</c>), so factoring setup/teardown here avoids two independent
/// implementations of the same host bring-up drifting apart. Deliberately carries no <c>[SimpleJob]</c>
/// of its own — each concrete subclass declares the run config appropriate to what it measures.
/// </summary>
[MemoryDiagnoser]
public abstract class ServerComparisonBenchmarksBase
{
    private WebApplication _ohDataApp = null!;
    private WebApplication _msODataApp = null!;
    private SqliteConnection _ohDataNavConnection = null!;
    private SqliteConnection _msODataNavConnection = null!;

    protected HttpClient OhDataClient { get; private set; } = null!;
    protected HttpClient MsODataClient { get; private set; } = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_ohDataApp, HttpClient ohData, _ohDataNavConnection) = await BenchmarkHosts.StartOhDataAsync();
        (_msODataApp, HttpClient msOData, _msODataNavConnection) = await BenchmarkHosts.StartMsODataAsync();
        OhDataClient = ohData;
        MsODataClient = msOData;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        OhDataClient.Dispose();
        MsODataClient.Dispose();
        await _ohDataApp.DisposeAsync();
        await _msODataApp.DisposeAsync();
        _ohDataNavConnection.Dispose();
        _msODataNavConnection.Dispose();
    }

    protected static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    protected static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request)
        {
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }
    }
}
