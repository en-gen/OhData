using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace OhData.Client.Benchmarks;

public class BenchPet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class BenchDog : BenchPet
{
    public string Breed { get; set; } = "";
}

public class BenchPlainPet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Reads the same 1,000-row page as a polymorphic base type (every other row is a derived row naming
/// its <c>@odata.type</c>, last in the object) and as a type with no derived types. Isolates the JSON
/// path: the response is served from memory.
/// </summary>
[MemoryDiagnoser]
public class PolymorphicReadBenchmarks
{
    private OhDataClient _client = null!;
    private HttpClient _http = null!;

    [GlobalSetup]
    public void Setup()
    {
        var body = new StringBuilder("{\"value\":[");
        for (int i = 0; i < 1000; i++)
        {
            if (i > 0) body.Append(',');
            body.Append("{\"Id\":").Append(i).Append(",\"Name\":\"name").Append(i).Append('"');
            if (i % 2 == 0)
                body.Append(",\"Breed\":\"b").Append(i).Append("\",\"@odata.type\":\"#").Append(typeof(BenchDog).FullName).Append('"');
            body.Append('}');
        }
        body.Append("]}");

        _http = new HttpClient(new Stub(body.ToString())) { BaseAddress = new Uri("http://localhost/odata/") };
        _client = new OhDataClient(_http);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _http.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Task<System.Collections.Generic.List<BenchPlainPet>> NonPolymorphic() =>
        _client.For<BenchPlainPet>("Pets").ToListAsync();

    [Benchmark]
    public Task<System.Collections.Generic.List<BenchPet>> PolymorphicBase() =>
        _client.For<BenchPet>("Pets").ToListAsync();

    private sealed class Stub(string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.CompletedTask;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
