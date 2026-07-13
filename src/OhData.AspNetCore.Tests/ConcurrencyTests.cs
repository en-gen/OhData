using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.Extensions.DependencyInjection;
using OhData.Abstractions;
using OhData.AspNetCore;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// Concurrency battle-hardening tests: parallel reads, parallel writes to distinct keys,
/// deterministic (sequential) If-Match semantics, per-request scoped-service isolation,
/// static-cache thread safety, and cross-container isolation of the lazy registration build.
/// All assertions are deterministic — no sleeps-as-synchronization, no timing-dependent checks.
/// </summary>
public class ConcurrencyTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// <summary>Thread-safe backing store used for the parallel-mixed-writes test.</summary>
    private sealed class ConcurrentWidgetStore
    {
        public readonly ConcurrentDictionary<int, Widget> Items = new();
    }

    private sealed class ConcurrentWriteProfile : EntitySetProfile<int, Widget>
    {
        private readonly ConcurrentWidgetStore _store;

        public ConcurrentWriteProfile(ConcurrentWidgetStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "ConcurrentWriteWidgets";

            GetById = (id, ct) =>
                Task.FromResult(_store.Items.TryGetValue(id, out var w) ? w : null);

            Post = (widget, ct) =>
            {
                _store.Items[widget.Id] = widget;
                return Task.FromResult<Widget?>(widget);
            };

            Put = (id, widget, ct) =>
            {
                widget.Id = id;
                _store.Items[id] = widget;
                return Task.FromResult(widget);
            };

            Patch = (id, delta, ct) =>
            {
                if (!_store.Items.TryGetValue(id, out var existing)) return Task.FromResult<Widget?>(null);
                delta.Patch(existing);
                return Task.FromResult<Widget?>(existing);
            };

            Delete = (id, ct) => Task.FromResult(_store.Items.TryRemove(id, out _));
        }
    }

    /// <summary>Scoped service whose constructor mints a fresh identity per DI scope — used to prove
    /// that each concurrent request resolves its own profile/scoped-service instance.</summary>
    private sealed class ScopedTracker
    {
        public Guid InstanceId { get; } = Guid.NewGuid();
    }

    // Note: fixtures such as WidgetProfile/ETagIfMatchProfile in Fixtures.cs hold their backing
    // store in an *instance* field, which is deliberately reset every time the scoped profile is
    // re-constructed (i.e. every HTTP request). That's fine for the single-request tests they were
    // built for, but the tests below need state that survives across multiple sequential requests
    // (for a genuine stale-vs-current ETag check) or that is provably isolated per-container (for
    // the parallel-host-build test). Both need a store injected as a singleton so its lifetime is
    // tied to the DI container/host, not to a single request.

    /// <summary>Singleton-backed store so ETag state persists across multiple sequential requests
    /// within the same host, enabling a genuine stale-vs-current If-Match sequence.</summary>
    private sealed class EtagSequenceStore
    {
        public readonly Dictionary<int, Widget> Items = new() { [1] = new Widget { Id = 1, Name = "Sprocket" } };
    }

    private sealed class EtagSequenceProfile : EntitySetProfile<int, Widget>
    {
        private readonly EtagSequenceStore _store;

        public EtagSequenceProfile(EtagSequenceStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "EtagSequenceWidgets";
            GetById = (id, ct) => Task.FromResult(_store.Items.TryGetValue(id, out var w) ? w : null);
            // Deliberately does NOT upsert: returns null (not found) when the key is absent,
            // so wildcard If-Match against a missing key surfaces the handler's 404, not a create.
            Put = (id, widget, ct) =>
            {
                if (!_store.Items.ContainsKey(id)) return Task.FromResult<Widget>(null!);
                widget.Id = id;
                _store.Items[id] = widget;
                return Task.FromResult(widget);
            };
            UseETag(x => x.Name);
        }
    }

    /// <summary>Singleton-backed store so data survives across multiple requests to the *same*
    /// host, while remaining trivially isolated from any other host's own singleton instance.</summary>
    private sealed class HostIsolationWidgetStore
    {
        public readonly List<Widget> Items = new()
        {
            new Widget { Id = 1, Name = "Sprocket" },
            new Widget { Id = 2, Name = "Cog" },
        };
    }

    private sealed class HostIsolationWidgetProfile : EntitySetProfile<int, Widget>
    {
        private readonly HostIsolationWidgetStore _store;

        public HostIsolationWidgetProfile(HostIsolationWidgetStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "HostIsolationWidgets";
            GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(_store.Items);
            Post = (widget, ct) =>
            {
                widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
                _store.Items.Add(widget);
                return Task.FromResult<Widget?>(widget);
            };
        }
    }

    private sealed class ScopedTrackerProfile : EntitySetProfile<int, Widget>
    {
        public ScopedTrackerProfile(ScopedTracker tracker) : base(x => x.Id)
        {
            EntitySetName = "ScopedTrackerWidgets";
            GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(
                new[] { new Widget { Id = 1, Name = tracker.InstanceId.ToString() } });
        }
    }

    // Three distinct concrete types (each with its own compiled-delegate cache entry) so that
    // concurrent first-hit requests across entity sets exercise the static ConcurrentDictionary
    // caches (s_etagCache / s_keyToStringCache) under contention.
    private sealed class CacheRaceProfileA : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new();
        public CacheRaceProfileA() : base(x => x.Id)
        {
            EntitySetName = "CacheRaceWidgetsA";
            GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return Task.FromResult<Widget?>(w); };
            UseETag(x => x.Name);
        }
    }

    private sealed class CacheRaceProfileB : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new();
        public CacheRaceProfileB() : base(x => x.Id)
        {
            EntitySetName = "CacheRaceWidgetsB";
            GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return Task.FromResult<Widget?>(w); };
            UseETag(x => x.Name);
        }
    }

    private sealed class CacheRaceProfileC : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new();
        public CacheRaceProfileC() : base(x => x.Id)
        {
            EntitySetName = "CacheRaceWidgetsC";
            GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return Task.FromResult<Widget?>(w); };
            UseETag(x => x.Name);
        }
    }

    // ── 1. Parallel read smoke ─────────────────────────────────────────────────

    [Fact]
    public async Task ParallelReads_100Concurrent_AllSucceedWithWellFormedPayloads()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<WidgetProfile>());

        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(i % 2 == 0
                ? fx.Client.GetAsync("/odata/Widgets")
                : fx.Client.GetAsync($"/odata/Widgets({(i % 4 < 2 ? 1 : 2)})"));
        }

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        foreach (var r in responses)
        {
            string body = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body); // throws if malformed
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var value))
            {
                Assert.Equal(JsonValueKind.Array, value.ValueKind);
                Assert.True(value.GetArrayLength() >= 1);
            }
            else
            {
                Assert.True(root.TryGetProperty("id", out _));
                Assert.True(root.TryGetProperty("name", out _));
            }
        }
    }

    // ── 2. Parallel mixed writes to different keys ─────────────────────────────

    [Fact]
    public async Task ParallelWrites_DistinctKeys_EachSucceedsAndFinalStateIsConsistent()
    {
        var store = new ConcurrentWidgetStore();
        // Pre-seed keys used by PUT (1-4), PATCH (20-23), DELETE (40-43).
        for (int i = 1; i <= 4; i++) store.Items[i] = new Widget { Id = i, Name = $"Seed{i}" };
        for (int i = 20; i <= 23; i++) store.Items[i] = new Widget { Id = i, Name = $"Seed{i}" };
        for (int i = 40; i <= 43; i++) store.Items[i] = new Widget { Id = i, Name = $"Seed{i}" };

        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<ConcurrentWriteProfile>(),
            configureServices: s => s.AddSingleton(store));

        var tasks = new List<Task<HttpResponseMessage>>();

        // POST — create distinct new keys 100-103
        for (int i = 100; i <= 103; i++)
        {
            int id = i;
            tasks.Add(fx.Client.PostAsJsonAsync("/odata/ConcurrentWriteWidgets", new Widget { Id = id, Name = $"Posted{id}" }));
        }

        // PUT — update distinct existing keys 1-4
        for (int i = 1; i <= 4; i++)
        {
            int id = i;
            tasks.Add(fx.Client.PutAsJsonAsync($"/odata/ConcurrentWriteWidgets({id})", new Widget { Id = id, Name = $"Put{id}" }));
        }

        // PATCH — update distinct existing keys 20-23
        for (int i = 20; i <= 23; i++)
        {
            int id = i;
            tasks.Add(fx.Client.PatchAsync($"/odata/ConcurrentWriteWidgets({id})",
                JsonContent.Create(new { Name = $"Patched{id}" })));
        }

        // DELETE — remove distinct existing keys 40-43
        for (int i = 40; i <= 43; i++)
        {
            int id = i;
            tasks.Add(fx.Client.DeleteAsync($"/odata/ConcurrentWriteWidgets({id})"));
        }

        var responses = await Task.WhenAll(tasks);

        // First 4: POST -> 201 Created
        for (int i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, responses[i].StatusCode);
        // Next 4: PUT -> 200 OK
        for (int i = 4; i < 8; i++)
            Assert.Equal(HttpStatusCode.OK, responses[i].StatusCode);
        // Next 4: PATCH -> 200 OK
        for (int i = 8; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK, responses[i].StatusCode);
        // Next 4: DELETE -> 204 No Content
        for (int i = 12; i < 16; i++)
            Assert.Equal(HttpStatusCode.NoContent, responses[i].StatusCode);

        // Final state consistency — each entity reflects exactly its own operation.
        for (int i = 100; i <= 103; i++)
            Assert.Equal($"Posted{i}", store.Items[i].Name);
        for (int i = 1; i <= 4; i++)
            Assert.Equal($"Put{i}", store.Items[i].Name);
        for (int i = 20; i <= 23; i++)
            Assert.Equal($"Patched{i}", store.Items[i].Name);
        for (int i = 40; i <= 43; i++)
            Assert.False(store.Items.ContainsKey(i));

        Assert.Equal(12, store.Items.Count); // 4 posted + 4 put + 4 patched (deleted ones gone)
    }

    // ── 3. Sequential (deterministic) If-Match behavior ────────────────────────

    [Fact]
    public async Task IfMatch_StaleThenCurrentEtag_412ThenSuccess()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        // 1. GET current etag.
        var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        string staleEtag = getResp.Headers.ETag!.Tag;

        // 2. An intervening PUT with the (still current) etag succeeds and changes the resource,
        //    which makes the etag captured in step 1 stale.
        var interveningReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Intervening" })
        };
        interveningReq.Headers.TryAddWithoutValidation("If-Match", staleEtag);
        var interveningResp = await fx.Client.SendAsync(interveningReq);
        Assert.Equal(HttpStatusCode.OK, interveningResp.StatusCode);
        string currentEtag = interveningResp.Headers.ETag!.Tag;
        Assert.NotEqual(staleEtag, currentEtag);

        // 3. PUT using the now-stale etag from step 1 -> 412.
        var staleReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "ShouldNotApply" })
        };
        staleReq.Headers.TryAddWithoutValidation("If-Match", staleEtag);
        var staleResp = await fx.Client.SendAsync(staleReq);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);

        // 4. PUT using the current etag -> 2xx.
        var currentReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Applied" })
        };
        currentReq.Headers.TryAddWithoutValidation("If-Match", currentEtag);
        var currentResp = await fx.Client.SendAsync(currentReq);
        Assert.Equal(HttpStatusCode.OK, currentResp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_Wildcard_ExistingEntity_Succeeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        var req = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "WildcardApplied" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_Wildcard_MissingEntity_Returns404()
    {
        // Wildcard bypasses the ETag precondition check itself (it always "matches"), so the
        // resulting status code comes from the underlying Put handler, which reports "not found"
        // for a key that was never seeded.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        var req = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(999)")
        {
            Content = JsonContent.Create(new Widget { Id = 999, Name = "Nope" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── 4. Profile-scoped-service isolation under concurrency ──────────────────

    [Fact]
    public async Task ConcurrentRequests_ResolveDistinctScopedServiceInstances()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddProfile<ScopedTrackerProfile>(),
            configureServices: s => s.AddScoped<ScopedTracker>());

        const int concurrency = 30;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => fx.Client.GetFromJsonAsync<JsonElement>("/odata/ScopedTrackerWidgets"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var instanceIds = results
            .Select(json => json.GetProperty("value")[0].GetProperty("name").GetString()!)
            .ToList();

        Assert.Equal(concurrency, instanceIds.Count);
        Assert.Equal(concurrency, instanceIds.Distinct().Count());
        Assert.All(instanceIds, id => Assert.True(Guid.TryParse(id, out _)));
    }

    // ── 5. Static-cache thread-safety smoke ─────────────────────────────────────

    [Fact]
    public async Task ParallelFirstHits_AcrossMultipleEntitySets_NoExceptionsAndCorrectResults()
    {
        // Each entity-set profile type here is used for the first time only once real HTTP
        // traffic arrives (the key-to-string compiled delegate, s_keyToStringCache, is populated
        // lazily on the first InvokeGetKeyString call — unlike the ETag cache, which is warmed
        // single-threaded during EDM construction at startup). Firing many concurrent POSTs
        // across several entity-set types immediately after host start races multiple threads
        // against ConcurrentDictionary.GetOrAdd/TryAdd for the same Type key.
        await using var fx = await TestHostBuilder.BuildAsync(o => o
            .AddProfile<CacheRaceProfileA>()
            .AddProfile<CacheRaceProfileB>()
            .AddProfile<CacheRaceProfileC>());

        string[] routes = new[] { "CacheRaceWidgetsA", "CacheRaceWidgetsB", "CacheRaceWidgetsC" };

        const int perRoute = 20;
        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < perRoute; i++)
        {
            foreach (string route in routes)
            {
                tasks.Add(fx.Client.PostAsJsonAsync($"/odata/{route}", new Widget { Name = $"{route}-{i}" }));
            }
        }

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        // Every response must carry a correctly computed ETag header (proves the compiled ETag
        // delegate — shared via the static per-type cache — produced a valid result under contention).
        Assert.All(responses, r =>
        {
            Assert.NotNull(r.Headers.ETag);
            Assert.False(string.IsNullOrWhiteSpace(r.Headers.ETag!.Tag));
        });

        // Each backing store is fresh per scoped request, so the created key is always "1" —
        // verifying the Location header proves the compiled key-to-string delegate (populated
        // under contention via GetOrAdd) produced the correct value for every single response,
        // not just a subset that happened to win the race.
        for (int i = 0; i < responses.Length; i++)
        {
            string expectedRoute = routes[i % routes.Length];
            Assert.NotNull(responses[i].Headers.Location);
            Assert.Contains($"/{expectedRoute}(1)", responses[i].Headers.Location!.ToString());
        }
    }

    // ── 6. Registration/startup concurrency ─────────────────────────────────────

    [Fact]
    public async Task ParallelHostBuilds_SameProfileType_DoNotShareMutableState()
    {
        // Each parallel BuildAsync call gets its own fresh HostIsolationWidgetStore singleton,
        // scoped to that container. Reusing the same profile TYPE across all four hosts built
        // concurrently validates that the lazy OhDataRegistration build path (and DI container
        // construction generally) does not leak mutable state across containers.
        var buildTasks = Enumerable.Range(0, 4)
            .Select(_ => TestHostBuilder.BuildAsync(
                o => o.AddProfile<HostIsolationWidgetProfile>(),
                configureServices: s => s.AddSingleton(new HostIsolationWidgetStore())))
            .ToArray();

        TestFixture[] fixtures = await Task.WhenAll(buildTasks);
        try
        {
            // Each host independently exposes its own seeded 2-widget list.
            foreach (TestFixture fx in fixtures)
            {
                var json = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/HostIsolationWidgets");
                Assert.Equal(2, json.GetProperty("value").GetArrayLength());
            }

            // Mutate only the first host.
            var postResp = await fixtures[0].Client.PostAsJsonAsync("/odata/HostIsolationWidgets", new Widget { Name = "HostOnly" });
            Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

            var mutated = await fixtures[0].Client.GetFromJsonAsync<JsonElement>("/odata/HostIsolationWidgets");
            Assert.Equal(3, mutated.GetProperty("value").GetArrayLength());

            // The other hosts remain unaffected — no cross-container state bleed.
            for (int i = 1; i < fixtures.Length; i++)
            {
                var unaffected = await fixtures[i].Client.GetFromJsonAsync<JsonElement>("/odata/HostIsolationWidgets");
                Assert.Equal(2, unaffected.GetProperty("value").GetArrayLength());
            }
        }
        finally
        {
            foreach (var fx in fixtures) await fx.DisposeAsync();
        }
    }
}
