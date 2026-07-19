using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData.AspNetCore;

namespace OhData.AspNetCore.Swashbuckle.Tests;

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
    /// Fetches and parses the Swashbuckle-generated OpenAPI document (Swashbuckle's default
    /// document name is "v1", served at <c>/swagger/{documentName}/swagger.json</c>).
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
/// Builds an in-process OhData + Swashbuckle host per test, mirroring the pattern used by
/// OhData.AspNetCore.OpenApi.Tests/OhData.AspNetCore.NSwag.Tests (not reusable directly since
/// those are internal to different assemblies with no InternalsVisibleTo grant to this project).
/// </summary>
internal static class SwashbuckleTestHostBuilder
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        string prefix = "/odata")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.OperationFilter<OhDataSwaggerOperationFilter>();
            c.SchemaFilter<OhDataSwaggerSchemaFilter>();
        });

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            configure(o);
        });

        var app = builder.Build();
        app.MapOhData();
        app.UseSwagger();

        await app.StartAsync();
        return new TestFixture(app);
    }
}
