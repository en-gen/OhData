using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OhData.Abstractions;
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

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ObsWidgetProfile>());
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

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ObsWidgetProfile>());
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

        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ObsWidgetProfile>());
        await fx.Client.GetAsync(Url);
        meterListener.Dispose();

        // A +1 on entry and a -1 on completion → two measurements netting to zero.
        Assert.Equal(2, measurements);
        Assert.Equal(0, net);
    }

    [Fact]
    public async Task NoListenerAttached_RequestStillSucceeds()
    {
        await using var fx = await TestHostBuilder.BuildAsync(o => o.AddProfile<ObsWidgetProfile>());
        var resp = await fx.Client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}

internal class ObsWidgetProfile : EntitySetProfile<int, Widget>
{
    public ObsWidgetProfile() : base(x => x.Id)
    {
        EntitySetName = "ObsWidgets";
        GetAll = (ct) => Task.FromResult<IEnumerable<Widget>>(Array.Empty<Widget>());
    }
}
