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
/// <remarks>
/// <para>
/// #394: every assertion in this class must go through <see cref="WaitForAsync"/> rather than read
/// its captured collection the instant <c>GetAsync</c> returns. Awaiting the HTTP response does
/// <b>not</b> order the observability callbacks before the test's next statement.
/// </para>
/// <para>
/// The reason is in the product and is deliberate: the span is stopped and the duration histogram /
/// active-request counter are recorded inside <c>HttpResponse.OnCompleted</c> (see the observability
/// group filter in <c>OhDataEndpointFactory.MapAll</c>), because the final HTTP status code is not
/// knowable from an endpoint filter after <c>next()</c> — the <c>IResult</c> executes later. Nothing
/// orders that callback ahead of the client's response task, so the capture is genuinely
/// asynchronous with respect to the assertion.
/// </para>
/// <para>
/// <b>Measured</b> (20,000 request/assert cycles against one host, polling for up to 300 ms after
/// each miss): the callback fired late but <b>never</b> failed to fire — <c>never-arrived = 0</c> in
/// all four arms. Rate of "not yet captured at assert time": <b>4/6000</b> (0.07%) with no
/// concurrency at all; <b>29/6000</b> with concurrent requests to the same host; <b>22/6000</b> with
/// concurrent <c>ActivityListener</c> add/dispose churn on the process-global
/// <c>ActivitySource</c>; <b>45/6000</b> (0.75%) with both. So solution-wide parallel load is an
/// amplifier, not the cause — the unloaded rate alone is enough to flake a CI suite regularly, which
/// is exactly the history #394 records. Observed lateness was always well under the 300 ms probe
/// window, so the 10 s ceiling below is pure headroom, not a tuned timeout.
/// </para>
/// <para>
/// This is a missing happens-before in the <b>test</b>, not a defect in the emission: no span, no
/// measurement and no tag was ever lost. Do not "fix" it by moving the product's <c>OnCompleted</c>
/// work earlier — that would report the wrong <c>http.response.status_code</c>.
/// </para>
/// </remarks>
public class ObservabilityTests
{
    private const string Url = "/odata/ObsWidgets";
    private const string Set = "ObsWidgets";

    /// <summary>
    /// Waits (bounded) for an observability callback that fires from <c>HttpResponse.OnCompleted</c>,
    /// i.e. after the client's response task has already completed. Returns as soon as
    /// <paramref name="captured"/> is true; on timeout it simply returns and lets the caller's own
    /// assertion produce its normal failure message.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> captured)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(10 * Stopwatch.Frequency);
        while (!captured())
        {
            if (Stopwatch.GetTimestamp() > deadline) return;
            await Task.Delay(5);
        }
    }

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

        await WaitForAsync(() => { lock (stopped) return stopped.Count > 0; });

        Activity activity;
        lock (stopped) activity = Assert.Single(stopped);
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
        // #394: the histogram is recorded from Response.OnCompleted; Dispose() does not flush an
        // in-flight measurement, it only unsubscribes. Wait for the measurement, then unsubscribe.
        await WaitForAsync(() => { lock (durations) return durations.Count > 0; });
        meterListener.Dispose();

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
        // #394: the -1 is recorded from Response.OnCompleted, so it can land after GetAsync returns.
        await WaitForAsync(() => Volatile.Read(ref measurements) >= 2);
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

        // #257/#394: each span is stopped from Response.OnCompleted, which can run after the client's
        // response task has completed, so the last request's classification may not be recorded yet.
        // Same bounded wait as every other test in this class — see the class remarks for the
        // measured rates and for why the product cannot close the span any earlier.
        string[] expected = { "read-entity", "create", "update-entity", "delete-entity", "read-navigation", "read-count" };
        await WaitForAsync(() =>
        {
            lock (ops) return expected.All(ops.Contains);
        });

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
        await WaitForAsync(() => { lock (seen) return seen.Count > 0; });
        lock (seen) Assert.Contains("metadata", seen);
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
                if ((a.GetTagItem("odata.entity_set") as string) == "ObsThrow") Volatile.Write(ref errorActivity, a);
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddEntitySetProfile<ThrowingObsProfile>());
        var resp = await fx.Client.GetAsync("/odata/ObsThrow");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        await WaitForAsync(() => Volatile.Read(ref errorActivity) is not null);

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
