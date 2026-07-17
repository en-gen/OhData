using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSwag.Generation.AspNetCore;
using OhData.AspNetCore;

namespace OhData.AspNetCore.NSwag.Tests;

/// <summary>
/// Wraps a running <see cref="WebApplication"/> and an <see cref="HttpClient"/> pointed at its
/// in-process <see cref="TestServer"/>. Dispose to stop the server.
/// </summary>
internal sealed class TestFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }

    internal TestFixture(WebApplication app)
    {
        _app = app;
        Client = ((IHost)app).GetTestClient();
    }

    /// <summary>
    /// Fetches and parses the NSwag-generated OpenAPI document for the given document name
    /// (NSwag's default document name is "v1", served at <c>/swagger/{documentName}/swagger.json</c>).
    /// </summary>
    public async Task<JsonDocument> GetDocumentAsync(string documentName = "v1")
    {
        var response = await Client.GetAsync($"/swagger/{documentName}/swagger.json");
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Builds an in-process OhData + NSwag host per test, mirroring the pattern used by
/// OhData.AspNetCore.Tests' TestHostBuilder (not reusable directly since it's internal to a
/// different assembly with no InternalsVisibleTo grant to this project).
/// </summary>
internal static class NSwagTestHostBuilder
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        Action<AspNetCoreOpenApiDocumentGeneratorSettings>? configureDocument = null,
        Action<IEndpointRouteBuilder>? configureExtraRoutes = null,
        string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApiDocument(s =>
        {
            s.OperationProcessors.Add(new OhDataNSwagOperationProcessor());
            configureDocument?.Invoke(s);
        });

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            configure(o);
        });

        var app = builder.Build();
        app.MapOhData();
        configureExtraRoutes?.Invoke(app);
        app.UseOpenApi();

        await app.StartAsync();
        return new TestFixture(app);
    }
}
