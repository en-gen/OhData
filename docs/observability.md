# Observability (#200)

OhData emits distributed-tracing spans and metrics using only the BCL
`System.Diagnostics` primitives — an `ActivitySource` and a `Meter`, both named **`OhData`**. There is
**no `OpenTelemetry.*` package dependency** in `EnGen.OhData.AspNetCore`; you opt in from your own
OpenTelemetry pipeline. When nothing is listening, the instrumentation is near-free (the span
creation returns `null` and the metric instruments no-op).

## Enabling it

Register the `OhData` source and meter with your OpenTelemetry providers:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddSource("OhData")          // OhData request spans
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddMeter("OhData")           // OhData request metrics
        .AddOtlpExporter());
```

## Tracing

One span is created per OData request (child of the ASP.NET Core request activity), named
`{method} {route}` (e.g. `GET /Widgets({key})`). Tags:

| Tag | Example | Notes |
|---|---|---|
| `odata.entity_set` | `Widgets` | The entity set the route belongs to (absent for `$metadata`/service-document). |
| `http.route` | `/Widgets({key})` | The route template — the precise operation identity (mirrors ASP.NET Core's `http.route`). |
| `odata.operation` | `read-entity` | A coarse method/shape label for convenient grouping — `read-collection`, `read-entity`, `create`, `update-entity`, `delete-entity`, `read-navigation`, `read-ref`, `metadata`, etc. |
| `http.request.method` | `GET` | |
| `http.response.status_code` | `200` | Set on completion. |

The span status is set to `Error` for `5xx` responses. The standard `http.*` server tags are already
emitted by ASP.NET Core's own instrumentation on the parent request span, so OhData's span carries
only the OData-specific detail rather than duplicating them.

## Metrics

On the `OhData` meter:

| Instrument | Kind | Unit | Tags |
|---|---|---|---|
| `ohdata.server.request.duration` | Histogram\<double\> | `s` | `odata.entity_set`, `odata.operation`, `http.response.status_code` |
| `ohdata.server.active_requests` | UpDownCounter\<long\> | — | `odata.entity_set`, `odata.operation` |

The names are `ohdata.*`-prefixed (rather than reusing the standard `http.server.*` names) so they stay
distinct from — and don't double-count against — the HTTP metrics ASP.NET Core already emits.

> A per-response result-size histogram (`ohdata.server.result.size`) is a planned follow-up; it needs
> the collection handlers to surface the returned item count to the metrics filter.
