using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using OhData;
using OhData.Server.Benchmarks.Model;
using OhData.Server.Benchmarks.MsODataHost;
using OhData.Server.Benchmarks.OhDataHost;

namespace OhData.Server.Benchmarks;

/// <summary>
/// Builds the two in-process TestServer hosts under comparison. Both serve the identical
/// deterministic 1000-widget dataset from a List&lt;T&gt;-backed store (no EF, no database — the
/// benchmark isolates the HTTP + OData pipeline) and both are addressed at <c>/odata</c>.
/// </summary>
internal static class BenchmarkHosts
{
    public const string Prefix = "/odata";
    public const string EntitySet = "BenchWidgets";

    /// <summary>OhData minimal-API pipeline host.</summary>
    public static async Task<(WebApplication App, HttpClient Client)> StartOhDataAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(Prefix);
            o.AddEntitySetProfile<BenchWidgetProfile>();
        });

        var app = builder.Build();
        app.MapOhData();
        await app.StartAsync();

        var client = ((IHost)app).GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "odata/");
        return (app, client);
    }

    /// <summary>Microsoft.AspNetCore.OData ODataController + [EnableQuery] pipeline host.</summary>
    public static async Task<(WebApplication App, HttpClient Client)> StartMsODataAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging(b => b.ClearProviders());
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(BenchWidgetsController).Assembly)
            .AddOData(options => options
                .EnableQueryFeatures(maxTopValue: BenchmarkData.PageSize)
                .AddRouteComponents(Prefix.TrimStart('/'), BuildEdmModel()));

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        var client = ((IHost)app).GetTestClient();
        client.BaseAddress = new Uri(client.BaseAddress!, "odata/");
        return (app, client);
    }

    private static IEdmModel BuildEdmModel()
    {
        var modelBuilder = new ODataConventionModelBuilder();
        // camelCase wire format to match OhData's default JSON casing — keeps the two
        // servers' payloads and query-option property names symmetric.
        modelBuilder.EnableLowerCamelCase();
        modelBuilder.EntitySet<BenchWidget>(EntitySet);
        return modelBuilder.GetEdmModel();
    }
}
