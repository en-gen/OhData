using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OhData;

/// <summary>
/// #200: BCL-only observability primitives. A single <see cref="System.Diagnostics.ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/>, both named <c>"OhData"</c>. Consumers opt in
/// from their existing OpenTelemetry pipeline — <c>.AddSource("OhData")</c> / <c>.AddMeter("OhData")</c>
/// — so <b>no <c>OpenTelemetry.*</c> package dependency is taken by this library</b>. Both are
/// near-free when nothing is listening (the <c>StartActivity</c> returns <c>null</c> and the
/// instruments no-op).
/// </summary>
internal static class OhDataDiagnostics
{
    /// <summary>The name used for both the ActivitySource and the Meter.</summary>
    public const string Name = "OhData";

    private static readonly string? s_version =
        typeof(OhDataDiagnostics).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource ActivitySource = new(Name, s_version);

    public static readonly Meter Meter = new(Name, s_version);

    /// <summary>Duration of OhData request processing, in seconds (OTel convention).</summary>
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "ohdata.server.request.duration",
        unit: "s",
        description: "Duration of OhData OData request processing.");

    /// <summary>In-flight OhData requests.</summary>
    public static readonly UpDownCounter<long> ActiveRequests = Meter.CreateUpDownCounter<long>(
        "ohdata.server.active_requests",
        description: "Number of in-flight OhData OData requests.");
}
