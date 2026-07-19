using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OhData.AspNetCore;

namespace OhData.AspNetCore.OpenApi.Tests;

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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Builds an in-process <see cref="WebApplication"/> per test that registers an OhData
/// registration plus Microsoft.AspNetCore.OpenApi with <see cref="OhDataOpenApiOperationTransformer"/>,
/// mirroring the pattern OhData.AspNetCore.Tests uses (see TestHostBuilder there) but adapted to
/// exercise the OpenAPI document pipeline instead of OData request handling directly.
/// </summary>
internal static class TestHostBuilder
{
    public static async Task<TestFixture> BuildAsync(
        Action<OhDataBuilder> configure,
        string prefix = "/odata",
        Action<OpenApiOptions>? configureOpenApi = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            configure(o);
        });

        builder.Services.AddOpenApi(configureOpenApi ?? (o =>
        {
            o.AddOperationTransformer<OhDataOpenApiOperationTransformer>();
            o.AddSchemaTransformer<OhDataOpenApiSchemaTransformer>();
        }));

        var app = builder.Build();

        app.MapOhData();
        app.MapOpenApi();
        configureApp?.Invoke(app);

        await app.StartAsync();
        return new TestFixture(app);
    }
}
