using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OhData.AspNetCore;

namespace OhData.AspNetCore.Tests;

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

internal static class TestHostBuilder
{
    public static async Task<TestFixture> BuildAsync(Action<OhDataBuilder> configure, string prefix = "/odata", bool addAuth = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();

        if (addAuth)
        {
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, NoOpAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
        }

        builder.Services.AddOhData(o =>
        {
            o.WithPrefix(prefix);
            configure(o);
        });

        var app = builder.Build();

        if (addAuth)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.MapOhData();
        await app.StartAsync();
        return new TestFixture(app);
    }
}

/// <summary>
/// Authentication handler that always returns "no result" — i.e. the request is unauthenticated.
/// Used by auth tests to verify that protected endpoints return 401.
/// </summary>
internal sealed class NoOpAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public NoOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
