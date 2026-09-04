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
using OhData;
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
                OhDataResult.Success(_store.Items.TryGetValue(id, out var w) ? w : null);

            Post = (widget, ct) =>
            {
                _store.Items[widget.Id] = widget;
                return OhDataResult.Success<Widget>(widget);
            };

            Put = (id, widget, ct) =>
            {
                widget.Id = id;
                _store.Items[id] = widget;
                return OhDataResult.Success(widget);
            };

            Patch = (id, delta, ct) =>
            {
                if (!_store.Items.TryGetValue(id, out var existing)) return OhDataResult.Success<Widget>(null);
                delta.Patch(existing);
                return OhDataResult.Success<Widget>(existing);
            };

            Delete = (id, ct) => OhDataResult.Success(_store.Items.TryRemove(id, out _));
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
            GetById = (id, ct) => OhDataResult.Success(_store.Items.TryGetValue(id, out var w) ? w : null);
            // Deliberately does NOT upsert: returns null (not found) when the key is absent,
            // so wildcard If-Match against a missing key surfaces the handler's 404, not a create.
            Put = (id, widget, ct) =>
            {
                if (!_store.Items.ContainsKey(id)) return OhDataResult.Success<Widget>(null!);
                widget.Id = id;
                _store.Items[id] = widget;
                return OhDataResult.Success(widget);
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
            GetAll = (ct) => OhDataResult.Success<IEnumerable<Widget>>(_store.Items);
            Post = (widget, ct) =>
            {
                widget.Id = _store.Items.Count > 0 ? _store.Items.Max(w => w.Id) + 1 : 1;
                _store.Items.Add(widget);
                return OhDataResult.Success<Widget>(widget);
            };
        }
    }

    private sealed class ScopedTrackerProfile : EntitySetProfile<int, Widget>
    {
        public ScopedTrackerProfile(ScopedTracker tracker) : base(x => x.Id)
        {
            EntitySetName = "ScopedTrackerWidgets";
            GetAll = (ct) => OhDataResult.Success<IEnumerable<Widget>>(
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
            GetById = (id, ct) => OhDataResult.Success(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return OhDataResult.Success<Widget>(w); };
            UseETag(x => x.Name);
        }
    }

    private sealed class CacheRaceProfileB : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new();
        public CacheRaceProfileB() : base(x => x.Id)
        {
            EntitySetName = "CacheRaceWidgetsB";
            GetById = (id, ct) => OhDataResult.Success(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return OhDataResult.Success<Widget>(w); };
            UseETag(x => x.Name);
        }
    }

    private sealed class CacheRaceProfileC : EntitySetProfile<int, Widget>
    {
        private readonly List<Widget> _store = new();
        public CacheRaceProfileC() : base(x => x.Id)
        {
            EntitySetName = "CacheRaceWidgetsC";
            GetById = (id, ct) => OhDataResult.Success(_store.FirstOrDefault(w => w.Id == id));
            Post = (w, ct) => { w.Id = _store.Count + 1; _store.Add(w); return OhDataResult.Success<Widget>(w); };
            UseETag(x => x.Name);
        }
    }

    // ── 1. Parallel read smoke ─────────────────────────────────────────────────

    [Fact]
    public async Task ParallelReads_100Concurrent_AllSucceedWithWellFormedPayloads()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<WidgetProfile>());

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
                Assert.True(root.TryGetProperty("Id", out _));
                Assert.True(root.TryGetProperty("Name", out _));
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
            o => o.AddEntitySetProfile<ConcurrentWriteProfile>(),
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
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        // 1. GET current etag.
        var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        string staleEtag = getResp.Headers.ETag!.Tag;

        // 2. An intervening PUT with the (still current) etag succeeds and changes the resource,
        //    which makes the etag captured in step 1 stale.
        using var interveningReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "Intervening" })
        };
        interveningReq.Headers.TryAddWithoutValidation("If-Match", staleEtag);
        var interveningResp = await fx.Client.SendAsync(interveningReq);
        Assert.Equal(HttpStatusCode.OK, interveningResp.StatusCode);
        string currentEtag = interveningResp.Headers.ETag!.Tag;
        Assert.NotEqual(staleEtag, currentEtag);

        // 3. PUT using the now-stale etag from step 1 -> 412.
        using var staleReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "ShouldNotApply" })
        };
        staleReq.Headers.TryAddWithoutValidation("If-Match", staleEtag);
        var staleResp = await fx.Client.SendAsync(staleReq);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResp.StatusCode);

        // 4. PUT using the current etag -> 2xx.
        using var currentReq = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
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
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)")
        {
            Content = JsonContent.Create(new Widget { Id = 1, Name = "WildcardApplied" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task IfMatch_Wildcard_MissingEntity_Returns412()
    {
        // m6: RFC 7232 §3.1 / Protocol §11.4.1.1 — If-Match (including the wildcard) fails with
        // 412 Precondition Failed when no current representation exists. The existence check now
        // happens before the wildcard short-circuit, so this must not fall through to the
        // underlying Put handler's own "not found" -> 404.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var req = new HttpRequestMessage(HttpMethod.Put, "/odata/EtagSequenceWidgets(999)")
        {
            Content = JsonContent.Create(new Widget { Id = 999, Name = "Nope" })
        };
        req.Headers.TryAddWithoutValidation("If-Match", "*");
        using var resp = await fx.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    // ── 4. Profile-scoped-service isolation under concurrency ──────────────────

    [Fact]
    public async Task ConcurrentRequests_ResolveDistinctScopedServiceInstances()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<ScopedTrackerProfile>(),
            configureServices: s => s.AddScoped<ScopedTracker>());

        const int concurrency = 30;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => fx.Client.GetFromJsonAsync<JsonElement>("/odata/ScopedTrackerWidgets"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var instanceIds = results
            .Select(json => json.GetProperty("value")[0].GetProperty("Name").GetString()!)
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
            .AddEntitySetProfile<CacheRaceProfileA>()
            .AddEntitySetProfile<CacheRaceProfileB>()
            .AddEntitySetProfile<CacheRaceProfileC>());

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
                o => o.AddEntitySetProfile<HostIsolationWidgetProfile>(),
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

    // ── 6. #478: If-Match coverage on the link-management / navigation-create routes ─────
    //
    // Every route below performs a write the framework owns. Before #478 the four of them
    // silently DISCARDED a received If-Match and answered 204/201 — RFC 9110 §13.1.1 says the
    // origin server MUST NOT perform the method when the precondition evaluates false. Each test
    // asserts BOTH the status code and that the handler delegate never ran: a route that refuses
    // must not first mutate anything, and a status-only assertion cannot tell those two apart.
    //
    // The fixture is the existing $ref/navigation-POST shape (the same Parent/Child models and
    // the same HasMany(addRef/removeRef) / HasOptional(setRef) wiring NavQueryProfile uses) with
    // UseETag added — an existing feature's fixture plus the new thing, not a model built around
    // conditional writes.

    /// <summary>Singleton-backed store for the #478 link-management fixture. <c>Ran</c> records
    /// every handler delegate that executed, which is what proves a refused precondition
    /// short-circuited BEFORE the write rather than after it.</summary>
    private sealed class EtagLinkStore
    {
        public readonly Dictionary<int, Parent> Parents = new()
        {
            [1] = new Parent { Id = 1, Name = "Sprocket" },
        };
        public readonly List<Child> Children = new()
        {
            new Child { Id = 10, ParentId = 1, Name = "Child10" },
        };
        public readonly List<string> Ran = new();
    }

    private sealed class EtagLinkProfile : EntitySetProfile<int, Parent>
    {
        // Instance field, not static: BindEntityAction accepts an instance method group (as
        // AuthorizationMatrixTests' Rename does), so the action handler reaches the per-host store
        // through the profile instance. Nothing here is shared between hosts.
        private readonly EtagLinkStore _store;

        public EtagLinkProfile(EtagLinkStore store) : base(x => x.Id)
        {
            _store = store;
            EntitySetName = "EtagLinkParents";
            UseETag(x => x.Name);

            GetById = (id, ct) => OhDataResult.Success(store.Parents.TryGetValue(id, out var p) ? p : null);

            HasMany(
                navigation: x => x.Children!,
                getAll: (parentId, ct) =>
                    Task.FromResult<IEnumerable<Child>>(store.Children.Where(c => c.ParentId == parentId)),
                post: (parentId, child, ct) =>
                {
                    store.Ran.Add($"post:{parentId}:{child.Name}");
                    child.ParentId = parentId;
                    store.Children.Add(child);
                    return Task.FromResult<Child?>(child);
                },
                addRef: (parentId, relatedId, ct) =>
                {
                    store.Ran.Add($"addRef:{parentId}:{relatedId}");
                    return Task.CompletedTask;
                },
                removeRef: (parentId, relatedId, ct) =>
                {
                    store.Ran.Add($"removeRef:{parentId}:{relatedId}");
                    return Task.CompletedTask;
                });

            HasOptional(
                navigation: x => x.PrimaryChild!,
                get: (parentId, ct) => Task.FromResult<Child?>(null),
                setRef: (parentId, relatedId, ct) =>
                {
                    store.Ran.Add($"setRef:{parentId}:{relatedId}");
                    return Task.CompletedTask;
                });

            BindEntityAction(Touch);
        }

        // Entity-level bound action. Deliberately NOT under the precondition gate — see the
        // exclusion comment in OhDataEndpointFactory at the entity-level bound action route.
        private Task Touch(int key)
        {
            _store.Ran.Add($"action:{key}");
            return Task.CompletedTask;
        }
    }

    private static async Task<(TestFixture Fx, EtagLinkStore Store, string StaleETag)>
        BuildLinkFixtureWithStaleETagAsync()
    {
        var store = new EtagLinkStore();
        var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var getResp = await fx.Client.GetAsync("/odata/EtagLinkParents(1)");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        string etag = getResp.Headers.ETag!.Tag;

        // A concurrent writer changes the entity out of band, which is what makes the captured
        // ETag stale. (UseETag hashes Name, so touching Name is the whole mutation.)
        store.Parents[1].Name = "ChangedByAnotherWriter";
        return (fx, store, etag);
    }

    private static HttpRequestMessage Conditional(
        HttpMethod method, string url, string? ifMatch = null, string? ifNoneMatch = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static Dictionary<string, string> RefBody(int childId) =>
        new() { ["@odata.id"] = $"http://localhost/odata/Children({childId})" };

    [Fact]
    public async Task IfMatch_Stale_AddRefOnCollectionNav_Returns412_AndAddRefNeverRuns()
    {
        var (fx, store, stale) = await BuildLinkFixtureWithStaleETagAsync();
        await using var _ = fx;

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children/$ref",
            ifMatch: stale, body: RefBody(77));
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task IfMatch_Stale_RemoveRefOnCollectionNav_Returns412_AndRemoveRefNeverRuns()
    {
        var (fx, store, stale) = await BuildLinkFixtureWithStaleETagAsync();
        await using var _ = fx;

        using var req = Conditional(HttpMethod.Delete,
            "/odata/EtagLinkParents(1)/Children/$ref?$id=http://localhost/odata/Children(10)",
            ifMatch: stale);
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task IfMatch_Stale_SetRefOnSingleValuedNav_Returns412_AndSetRefNeverRuns()
    {
        var (fx, store, stale) = await BuildLinkFixtureWithStaleETagAsync();
        await using var _ = fx;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagLinkParents(1)/PrimaryChild/$ref",
            ifMatch: stale, body: RefBody(78));
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task IfMatch_Stale_NavigationPostCreate_Returns412_AndPostNeverRuns()
    {
        var (fx, store, stale) = await BuildLinkFixtureWithStaleETagAsync();
        await using var _ = fx;

        int childCountBefore = store.Children.Count;
        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children",
            ifMatch: stale, body: new Child { Id = 11, Name = "NewChild" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
        Assert.Equal(childCountBefore, store.Children.Count);
    }

    [Fact]
    public async Task IfMatch_Current_OnLinkRoutes_StillSucceedsAndDelegatesRun()
    {
        // The positive control for the four tests above: enforcement must be a precondition
        // check, not a blanket refusal of conditional requests on these routes.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var getResp = await fx.Client.GetAsync("/odata/EtagLinkParents(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var addReq = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children/$ref",
            ifMatch: current, body: RefBody(77));
        using var addResp = await fx.Client.SendAsync(addReq);
        Assert.Equal(HttpStatusCode.NoContent, addResp.StatusCode);

        using var delReq = Conditional(HttpMethod.Delete,
            "/odata/EtagLinkParents(1)/Children/$ref?$id=http://localhost/odata/Children(10)",
            ifMatch: current);
        using var delResp = await fx.Client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        using var setReq = Conditional(HttpMethod.Put, "/odata/EtagLinkParents(1)/PrimaryChild/$ref",
            ifMatch: current, body: RefBody(78));
        using var setResp = await fx.Client.SendAsync(setReq);
        Assert.Equal(HttpStatusCode.NoContent, setResp.StatusCode);

        using var postReq = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children",
            ifMatch: current, body: new Child { Id = 11, Name = "NewChild" });
        using var postResp = await fx.Client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        Assert.Equal(
            new[]
            {
                "addRef:1:http://localhost/odata/Children(77)",
                "removeRef:1:http://localhost/odata/Children(10)",
                "setRef:1:http://localhost/odata/Children(78)",
                "post:1:NewChild",
            },
            store.Ran);
    }

    [Fact]
    public async Task NoConditionalHeader_OnLinkRoutes_IsUnaffected()
    {
        // Regression guard: the precondition gate is a no-op when neither header is present, so
        // an unconditional $ref write keeps its pre-#478 behaviour exactly.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/EtagLinkParents(1)/Children/$ref", RefBody(77));

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(new[] { "addRef:1:http://localhost/odata/Children(77)" }, store.Ran);
    }

    [Fact]
    public async Task IfMatch_Wildcard_OnLinkRoute_ExistingEntity_Succeeds()
    {
        // Consistency with the five pre-existing CheckETagAsync sites: "*" matches any EXISTING
        // representation.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children/$ref",
            ifMatch: "*", body: RefBody(77));
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Single(store.Ran);
    }

    [Fact]
    public async Task IfMatch_Wildcard_OnLinkRoute_MissingEntity_Returns412_AndDelegateNeverRuns()
    {
        // ...and, exactly as on PUT (IfMatch_Wildcard_MissingEntity_Returns412 above), "*" against
        // a key with no current representation is 412, never the handler's own outcome.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(999)/Children/$ref",
            ifMatch: "*", body: RefBody(77));
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task EntityBoundAction_StaleIfMatch_Returns412_AndDoesNotInvokeTheAction()
    {
        // #566. §11.4.1.1 is a MUST whose subject is "a Data Modification Request OR ACTION
        // REQUEST", and §11.5.4.1 tells the client to send If-Match for exactly this case. Until
        // 2.0.0 this route answered 204 and RAN the action — #478 excluded it, defended by the
        // assertion that an action-invocation resource "has no representation and therefore no
        // entity tag" (§11.5.4), a phrase that appears nowhere in Part 1.
        //
        // Assert.Empty(store.Ran) is the load-bearing half, not the status code: the gate is
        // placed before the parameter body is read and before the handler delegate runs, so a
        // refused invocation must provably mutate nothing. A status-only assertion would pass
        // even if the action ran and the 412 were written afterwards.
        var (fx, store, stale) = await BuildLinkFixtureWithStaleETagAsync();
        await using var _ = fx;

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Touch",
            ifMatch: stale, body: new Dictionary<string, string>());
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task EntityBoundAction_CurrentIfMatch_Succeeds_AndInvokesTheAction()
    {
        // #566's positive control, and the reason the fix is a precondition check rather than a
        // blanket refusal of conditional requests on the action route. Without this, the test
        // above would also pass if the route simply started rejecting every If-Match it saw.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var getResp = await fx.Client.GetAsync("/odata/EtagLinkParents(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Touch",
            ifMatch: current, body: new Dictionary<string, string>());
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(new[] { "action:1" }, store.Ran);
    }

    [Fact]
    public async Task EntityBoundAction_NoConditionalHeader_IsUnaffected()
    {
        // The no-header path must not change: CheckETagAsync returns null before touching
        // GetById when neither If-Match nor If-None-Match is present, so an ordinary invocation
        // costs nothing new and behaves exactly as it did in 1.7.0.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var resp = await fx.Client.PostAsJsonAsync(
            "/odata/EtagLinkParents(1)/Touch", new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(new[] { "action:1" }, store.Ran);
    }

    [Fact]
    public async Task EntityBoundAction_IfNoneMatchOfCurrentETag_Returns412_AndDoesNotInvoke()
    {
        // §11.4.1.1's MUST names If-None-Match alongside If-Match, and RFC 9110 §13.2.2 makes
        // If-None-Match the one evaluated when If-Match is absent. Comparison there is WEAK
        // (§13.1.2), which is why CheckETagAsync reads the two headers with different parsers;
        // this pins that the action route inherits both halves rather than only the If-Match one.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var getResp = await fx.Client.GetAsync("/odata/EtagLinkParents(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Touch",
            ifNoneMatch: current, body: new Dictionary<string, string>());
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    // ── 7. #478: strong comparison for If-Match, weak comparison for If-None-Match ────────

    [Fact]
    public async Task IfMatch_WeakValidatorOfCurrentETag_Returns412()
    {
        // RFC 9110 §13.1.1 requires STRONG comparison for If-Match, and §8.8.3.2 says a weak
        // validator never participates in one. ParseETagList used to strip the W/ prefix for both
        // headers, so `If-Match: W/"<current>"` matched and the write was performed with a 200.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifMatch: "W/" + current, body: new Widget { Id = 1, Name = "WeakWrite" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);

        // And the refusal really was a refusal — the entity still carries its original value.
        var after = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/EtagSequenceWidgets(1)");
        Assert.Equal("Sprocket", after.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task IfMatch_WeakValidatorAlongsideStrongMatch_StillSucceeds()
    {
        // Dropping weak entries must not poison the rest of the list: a strong entry that matches
        // still satisfies the precondition.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifMatch: "W/\"someothervalue\", " + current,
            body: new Widget { Id = 1, Name = "Applied" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_MatchingCurrentETag_OnWrite_Returns412()
    {
        // RFC 9110 §13.1.2: on a state-changing method the condition is FALSE when a listed
        // validator matches, so the method must not be performed. Only `If-None-Match: *` was
        // honoured before #478 (as an upsert create-guard); a specific matching ETag was ignored
        // and the write returned 200.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifNoneMatch: current, body: new Widget { Id = 1, Name = "ShouldNotApply" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        var after = await fx.Client.GetFromJsonAsync<JsonElement>("/odata/EtagSequenceWidgets(1)");
        Assert.Equal("Sprocket", after.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task IfNoneMatch_WeakValidatorOfCurrentETag_OnWrite_Returns412()
    {
        // If-None-Match uses WEAK comparison (§13.1.2), so W/"<current>" DOES match here — the
        // opposite of the If-Match case above. The two readers are not interchangeable.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifNoneMatch: "W/" + current, body: new Widget { Id = 1, Name = "ShouldNotApply" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_NonMatchingETag_OnWrite_Proceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifNoneMatch: "\"not-the-current-etag\"", body: new Widget { Id = 1, Name = "Applied" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_OnLinkRoute_MatchingCurrentETag_Returns412_AndDelegateNeverRuns()
    {
        // The If-None-Match arm rides the same gate, so it reaches the link routes too.
        var store = new EtagLinkStore();
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagLinkProfile>(),
            configureServices: s => s.AddSingleton(store));

        using var getResp = await fx.Client.GetAsync("/odata/EtagLinkParents(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Post, "/odata/EtagLinkParents(1)/Children/$ref",
            ifNoneMatch: current, body: RefBody(77));
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
        Assert.Empty(store.Ran);
    }

    [Fact]
    public async Task IfMatch_TakesPrecedenceOver_IfNoneMatch_WhenBothPresent()
    {
        // RFC 9110 §13.2.2 fixes the evaluation order: If-None-Match is evaluated only when
        // If-Match is absent. Both headers naming the CURRENT ETag must therefore succeed —
        // If-Match matches and wins — rather than being AND-ed into a 412.
        await using var fx = await TestHostBuilder.BuildAsync(
            o => o.AddEntitySetProfile<EtagSequenceProfile>(),
            configureServices: s => s.AddSingleton(new EtagSequenceStore()));

        using var getResp = await fx.Client.GetAsync("/odata/EtagSequenceWidgets(1)");
        string current = getResp.Headers.ETag!.Tag;

        using var req = Conditional(HttpMethod.Put, "/odata/EtagSequenceWidgets(1)",
            ifMatch: current, ifNoneMatch: current, body: new Widget { Id = 1, Name = "Applied" });
        using var resp = await fx.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
