using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using OhData;
using Xunit;

namespace OhData.AspNetCore.Tests;

/// <summary>
/// #200: OhData emits one <c>ActivitySource("OhData")</c> span per request (tagged with
/// <c>odata.entity_set</c>/<c>http.route</c>/<c>odata.operation</c>/status) and records the
/// <c>ohdata.server.request.duration</c> histogram + <c>ohdata.server.active_requests</c> up/down
/// counter on the <c>Meter("OhData")</c>. The BCL listeners are process-global, so these tests scope
/// their assertions to a uniquely-named entity set (<c>ObsWidgets</c>) — concurrent tests hitting
/// other sets are filtered out by the <c>odata.entity_set</c> tag.
/// </summary>
public class ObservabilityTests
{
    private const string Url = "/odata/ObsWidgets";
    private const string Set = "ObsWidgets";

    private static bool ForOurSet(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var t in tags)
        {
            if (t.Key == "odata.entity_set" && (t.Value as string) == Set) return true;
        }
        return false;
    }

    [Fact]
    public async Task Request_EmitsActivity_WithODataTags()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OhData",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if ((a.GetTagItem("odata.entity_set") as string) == Set)
                {
                    lock (stopped) stopped.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsWidgetProfile>());
        var resp = await fx.Client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var activity = Assert.Single(stopped);
        Assert.Equal("read-collection", activity.GetTagItem("odata.operation"));
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
        Assert.NotNull(activity.GetTagItem("http.route"));
    }

    [Fact]
    public async Task Request_RecordsDurationHistogram()
    {
        var durations = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "OhData" && inst.Name == "ohdata.server.request.duration")
                    l.EnableMeasurementEvents(inst);
            },
        };
        meterListener.SetMeasurementEventCallback<double>((inst, val, tags, state) =>
        {
            if (ForOurSet(tags)) lock (durations) durations.Add(val);
        });
        meterListener.Start();

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsWidgetProfile>());
        await fx.Client.GetAsync(Url);
        meterListener.Dispose(); // flush

        Assert.Single(durations);
        Assert.True(durations[0] >= 0);
    }

    [Fact]
    public async Task Request_RecordsActiveRequestUpDownCounter()
    {
        long net = 0;
        int measurements = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "OhData" && inst.Name == "ohdata.server.active_requests")
                    l.EnableMeasurementEvents(inst);
            },
        };
        meterListener.SetMeasurementEventCallback<long>((inst, val, tags, state) =>
        {
            if (ForOurSet(tags))
            {
                Interlocked.Add(ref net, val);
                Interlocked.Increment(ref measurements);
            }
        });
        meterListener.Start();

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsWidgetProfile>());
        await fx.Client.GetAsync(Url);
        meterListener.Dispose();

        // A +1 on entry and a -1 on completion → two measurements netting to zero.
        Assert.Equal(2, measurements);
        Assert.Equal(0, net);
    }

    [Fact]
    public async Task NoListenerAttached_RequestStillSucceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsWidgetProfile>());
        var resp = await fx.Client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Operation_IsClassified_PerRouteShape()
    {
        var ops = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OhData",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if ((a.GetTagItem("odata.entity_set") as string) == "ObsRich")
                {
                    lock (ops) ops.Add((string)a.GetTagItem("odata.operation")!);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsRichProfile>());
        await fx.Client.GetAsync("/odata/ObsRich(1)");                 // read-entity
        await fx.Client.PostAsJsonAsync("/odata/ObsRich", new { name = "n" }); // create
        await fx.Client.PutAsJsonAsync("/odata/ObsRich(1)", new { id = 1, name = "n" }); // update-entity
        await fx.Client.PatchAsync("/odata/ObsRich(1)", JsonContent("{\"name\":\"n\"}"));  // update-entity
        await fx.Client.DeleteAsync("/odata/ObsRich(1)");             // delete-entity
        await fx.Client.GetAsync("/odata/ObsRich(1)/Children");        // read-navigation
        await fx.Client.GetAsync("/odata/ObsRich/$count");            // read-count

        // An activity is stopped in a middleware finally block that can run just after the HTTP
        // response has flushed, so the last request's classification may not be recorded yet when
        // we reach the asserts. Poll for the full set (bounded) rather than asserting immediately
        // (pre-existing race — see #257).
        string[] expected = { "read-entity", "create", "update-entity", "delete-entity", "read-navigation", "read-count" };
        for (int i = 0; i < 200; i++)
        {
            bool all = true;
            lock (ops)
            {
                foreach (string e in expected)
                {
                    if (!ops.Contains(e)) { all = false; break; }
                }
            }
            if (all) break;
            await Task.Delay(25);
        }

        lock (ops)
        {
            Assert.Contains("read-entity", ops);
            Assert.Contains("create", ops);
            Assert.Contains("update-entity", ops);
            Assert.Contains("delete-entity", ops);
            Assert.Contains("read-navigation", ops);
            Assert.Contains("read-count", ops);
        }
    }

    [Fact]
    public async Task Metadata_Operation_IsTagged()
    {
        var seen = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OhData",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if ((a.GetTagItem("http.route") as string)?.EndsWith("/$metadata", StringComparison.Ordinal) == true)
                {
                    lock (seen) seen.Add((string)a.GetTagItem("odata.operation")!);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ObsWidgetProfile>());
        await fx.Client.GetAsync("/odata/$metadata");
        Assert.Contains("metadata", seen);
    }

    [Fact]
    public async Task ServerError_SetsSpanStatusError()
    {
        Activity? errorActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "OhData",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if ((a.GetTagItem("odata.entity_set") as string) == "ObsThrow") errorActivity = a;
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowingObsProfile>());
        var resp = await fx.Client.GetAsync("/odata/ObsThrow");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        Assert.NotNull(errorActivity);
        Assert.Equal(ActivityStatusCode.Error, errorActivity!.Status);
        Assert.Equal(500, errorActivity.GetTagItem("http.response.status_code"));
    }

    private static System.Net.Http.StringContent JsonContent(string s) =>
        new(s, System.Text.Encoding.UTF8, "application/json");
}

internal class ObsWidgetProfile : EntitySetProfile<int, Widget>
{
    public ObsWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "ObsWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
    }
}

internal class ObsNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public IEnumerable<ObsNode>? Children { get; set; }
}

internal class ObsRichProfile : EntitySetProfile<int, ObsNode>
{
    private readonly List<ObsNode> _store = new() { new() { Id = 1, Name = "a" } };

    public ObsRichProfile() : base(x => x.Id)
    {
        EntitySetName = "ObsRich";
        CountEnabled = true;
        GetAll = (ct) => Task.FromResult<IEnumerable<ObsNode>>(_store);
        GetById = (id, ct) => Task.FromResult(_store.FirstOrDefault(n => n.Id == id));
        Post = (n, ct) => { n.Id = 99; _store.Add(n); return Task.FromResult<ObsNode?>(n); };
        Put = (id, n, ct) => { n.Id = id; return Task.FromResult(n); };
        Patch = (id, delta, ct) =>
        {
            var n = _store.FirstOrDefault(x => x.Id == id);
            if (n is not null) delta.Patch(n);
            return Task.FromResult(n);
        };
        Delete = (id, ct) => Task.FromResult(true);
        HasMany(
            navigation: x => x.Children!,
            getAll: (id, ct) => Task.FromResult<IEnumerable<ObsNode>>(Array.Empty<ObsNode>()));
    }
}

internal class ThrowingObsProfile : EntitySetProfile<int, Widget>
{
    public ThrowingObsProfile() : base(x => x.Id)
    {
        EntitySetName = "ObsThrow";
        GetAll = (ct) => throw new System.InvalidOperationException("boom");
    }
}
