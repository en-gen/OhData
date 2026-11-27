using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OhData;
using Xunit;
using Xunit.Abstractions;

namespace OhData.AspNetCore.Tests;

// SPIKE #867 -- error-after-headers probes. The row source throws at row 300 (1-based) after the
// first two 128-row chunks (256 rows) were flushed. Each mode is observed from a real client on
// Kestrel (the wire) and on TestServer (in-process, for contrast). Observations are logged and
// recorded in spike/NOTES.md; the assertions pin only what the probe established.

internal sealed class SpikeFaultProfile : EntitySetProfile<int, SpItem>
{
    internal const int FailAt = 300;

    public SpikeFaultProfile() : base(x => x.Id)
    {
        EntitySetName = "SpikeFault";
        SelectEnabled = true; FilterEnabled = true; OrderByEnabled = true; CountEnabled = true;
        var rows = SpikeData.Rows(1000);
        GetQueryable = () => rows
            .AsEnumerable()
            .Select(r => r.Id == FailAt ? throw new InvalidOperationException("boom at row 300") : r)
            .AsQueryable();
    }
}

public class StreamingSpikeErrorProbeTests
{
    private readonly ITestOutputHelper _output;

    public StreamingSpikeErrorProbeTests(ITestOutputHelper output) => _output = output;

    private sealed record Observation(
        string Host, string Mode, int Status, string Framing, int BytesRead, int RowsSeen,
        string? ReadException, bool ValidJson, bool HasErrorMember, string Tail);

    private static async Task<WebApplication> StartKestrelAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddOhData(o => { o.WithPrefix("/odata"); o.WithDefaults(d => d.MaxTop = null); o.AddEntitySetProfile<SpikeFaultProfile>(); });
        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();
        return app;
    }

    private static async Task<Observation> ObserveAsync(
        HttpClient client, string host, string mode, string url)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            return new Observation(host, mode, -1, "(no response)", 0, 0,
                $"{ex.GetType().Name}: {ex.Message}" + (ex.InnerException is null ? "" : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"),
                false, false, "");
        }

        using var respLifetime = resp;
        string framing = $"Content-Length={resp.Content.Headers.ContentLength?.ToString() ?? "(none)"} " +
                         $"Transfer-Encoding={string.Join(",", resp.Headers.TransferEncoding)}";
        var ms = new MemoryStream();
        string? readException = null;
        try
        {
            await using Stream body = await resp.Content.ReadAsStreamAsync();
            await body.CopyToAsync(ms);
        }
        catch (Exception ex)
        {
            readException = $"{ex.GetType().Name}: {ex.Message}" +
                            (ex.InnerException is null ? "" : $" <- {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        byte[] bytes = ms.ToArray();
        string text = Encoding.UTF8.GetString(bytes);
        bool valid = false;
        bool hasError = false;
        int rows = 0;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            valid = true;
            hasError = doc.RootElement.TryGetProperty("error", out _);
            if (doc.RootElement.TryGetProperty("value", out var v)) rows = v.GetArrayLength();
        }
        catch (JsonException)
        {
            rows = text.Split("\"Id\":").Length - 1;
        }

        string tail = text.Length <= 160 ? text : "..." + text[^160..];
        return new Observation(host, mode, (int)resp.StatusCode, framing, bytes.Length, rows, readException, valid, hasError, tail);
    }

    [Theory]
    [InlineData("buffered", false, "Rethrow")]
    [InlineData("rethrow", true, "Rethrow")]
    [InlineData("abort", true, "Abort")]
    [InlineData("inband", true, "InBand")]
    public async Task Kestrel_ErrorAfterFlush(string mode, bool stream, string errorModeName)
    {
        var errorMode = Enum.Parse<StreamErrorMode>(errorModeName);
        await using WebApplication app = await StartKestrelAsync();
        OhDataRegistration reg = app.Services.GetRequiredService<OhDataRegistrationCollection>().Default;
        reg.StreamCollections = stream;
        reg.StreamErrorMode = errorMode;
        reg.StreamChunkSize = 128;

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        Observation o = await ObserveAsync(client, "Kestrel", mode, "/odata/SpikeFault");
        _output.WriteLine(o.ToString());
        Console.WriteLine("PROBE " + o);

        if (!stream)
        {
            Assert.Equal(500, o.Status);
            Assert.True(o.HasErrorMember);
            return;
        }

        if (errorMode != StreamErrorMode.Abort) Assert.Equal(200, o.Status);
        switch (errorMode)
        {
            case StreamErrorMode.InBand:
                Assert.True(o.ValidJson);
                Assert.True(o.HasErrorMember);
                break;
            case StreamErrorMode.Abort:
                Assert.False(o.ValidJson);
                Assert.NotNull(o.ReadException);
                break;
            case StreamErrorMode.Rethrow:
                Assert.False(o.ValidJson);
                break;
        }
    }

    [Theory]
    [InlineData("buffered", false, "Rethrow")]
    [InlineData("rethrow", true, "Rethrow")]
    [InlineData("abort", true, "Abort")]
    [InlineData("inband", true, "InBand")]
    public async Task TestServer_ErrorAfterFlush(string mode, bool stream, string errorModeName)
    {
        var errorMode = Enum.Parse<StreamErrorMode>(errorModeName);
        await using var fx = await TestHostBuilder.BuildAsync(o => { o.WithDefaults(d => d.MaxTop = null); o.AddEntitySetProfile<SpikeFaultProfile>(); });
        OhDataRegistration reg = SpikeWireClient.Registration(fx);
        reg.StreamCollections = stream;
        reg.StreamErrorMode = errorMode;
        reg.StreamChunkSize = 128;

        Observation o;
        try
        {
            o = await ObserveAsync(fx.Client, "TestServer", mode, "/odata/SpikeFault");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"TestServer {mode}: client call threw {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"PROBE TestServer {mode}: client call threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        _output.WriteLine(o.ToString());
        Console.WriteLine("PROBE " + o);
    }
}
