using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #474: the second half of #203's filter — the one that assigns the per-request Kestrel
/// <c>MaxRequestBodySize</c>, which is what bounds a chunked / no-<c>Content-Length</c> body.
/// <para>
/// <b>Why this file exists at all.</b> <c>Microsoft.AspNetCore.TestHost</c> supplies no
/// <see cref="IHttpMaxRequestBodySizeFeature"/> — measured, it is simply absent from
/// <c>HttpContext.Features</c> — so every test in <c>RequestBodySizeLimitTests</c> exercises the
/// <c>Content-Length</c> fast-reject arm and nothing else. That was tolerable while the limit could
/// only come from the adopter; it is not now that the framework supplies a default, because an
/// unconditional assignment would <b>raise</b> the ceiling on a host that had deliberately lowered
/// Kestrel's below 30 MB. The tests below install a controllable feature so that arm has coverage.
/// </para>
/// </summary>
public class RequestBodySizeFeatureTests
{
    [Fact]
    public async Task FrameworkDefaultLimit_NeverRaisesAHostCeilingThatIsAlreadyLower()
    {
        var probe = new SizeFeatureProbe(hostLimit: 1_000_000);
        await using var fx = await BuildAsync(probe, o => o.AddEntitySetProfile<WidgetProfile>());

        using var content = new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/Widgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // The host's own, stricter ceiling survives. Before the clamp this was 30,000,000 — the
        // framework's default silently undoing a deliberate hardening step.
        Assert.Equal(1_000_000, probe.ObservedAfterPipeline);
    }

    [Fact]
    public async Task FrameworkDefaultLimit_AppliesWhenTheHostDisabledItsOwn()
    {
        var probe = new SizeFeatureProbe(hostLimit: null);
        await using var fx = await BuildAsync(probe, o => o.AddEntitySetProfile<WidgetProfile>());

        using var content = new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/Widgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // This is #474's whole point: a host with no ceiling of its own now gets the framework's.
        Assert.Equal(EntitySetDefaults.DefaultMaxRequestBodyBytes, probe.ObservedAfterPipeline);
    }

    [Fact]
    public async Task ExplicitProfileLimit_StillOverridesTheHostInBothDirections()
    {
        // BOUNDING: #203's documented behaviour ("this set accepts up to 4 MB") is a deliberate
        // per-route override and must keep raising a lower host ceiling. Only a limit the FRAMEWORK
        // chose is clamped.
        var probe = new SizeFeatureProbe(hostLimit: 100);
        await using var fx = await BuildAsync(probe, o => o.AddEntitySetProfile<BodyLimitProfile>());

        using var content = new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/BodyLimitWidgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(200, probe.ObservedAfterPipeline); // BodyLimitProfile.MaxRequestBodyBytes
    }

    [Fact]
    public async Task DefaultsClearedToNull_LeavesTheHostCeilingUntouched()
    {
        var probe = new SizeFeatureProbe(hostLimit: 1_000_000);
        await using var fx = await BuildAsync(probe, o => o
            .WithDefaults(d => d.MaxRequestBodyBytes = null)
            .AddEntitySetProfile<WidgetProfile>());

        using var content = new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json");
        var response = await fx.Client.PostAsync("/odata/Widgets", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1_000_000, probe.ObservedAfterPipeline);
    }

    private static Task<TestFixture> BuildAsync(SizeFeatureProbe probe, Action<OhDataBuilder> configure) =>
        TestHostBuilder.BuildAsync(
            configure,
            configureServices: services => services.AddSingleton<IStartupFilter>(probe));

    /// <summary>
    /// Installs a settable <see cref="IHttpMaxRequestBodySizeFeature"/> on every request and records
    /// what the OhData filter left on it. An <see cref="IStartupFilter"/> rather than a middleware
    /// registration because <c>TestHostBuilder</c> composes the pipeline itself.
    /// </summary>
    private sealed class SizeFeatureProbe : IStartupFilter
    {
        private readonly long? _hostLimit;

        internal SizeFeatureProbe(long? hostLimit) => _hostLimit = hostLimit;

        internal long? ObservedAfterPipeline { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = _hostLimit };
                ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
                await nxt();
                ObservedAfterPipeline = feature.MaxRequestBodySize;
            });
            next(app);
        };
    }

    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
