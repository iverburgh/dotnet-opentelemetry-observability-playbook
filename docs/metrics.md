# Metrics

Metrics are measured for every request, before any sampling, and aggregated in the process before they
are exported. They are the right source for every number: rates, counts, latencies, alerts. Traces and
logs explain one case; metrics say how many cases there were.

## What is exported

| Meter | Instruments | Added by |
|---|---|---|
| `Observability.Processes` | `process.completed`, `process.duration` | `AddObservability` |
| `Observability.Heartbeat` and `{service}.heartbeatmetrics` | `telemetry.heartbeat` | `AddObservability` |
| `{service.name}.*` | every [metrics bundle](#metrics-bundles) of your own | `AddObservability` |
| ASP.NET Core, HttpClient | `http.server.request.duration`, `http.client.request.duration`, … | the host packages |
| `Observability.Outbox` | `outbox.messages.published`, `outbox.messages.failed`, `outbox.message.age` | `WithOutboxInstrumentation` |

## Process metrics

| Instrument | Unit | Tags |
|---|---|---|
| `process.completed` | `{process}` | `process.name`, `process.result` = `ok` or `error` |
| `process.duration` | `s` | `process.name`, `process.result` |

A dedicated `ActivityListener` records them when a process span stops. It asks for every process span to be
*created* — not recorded — so the counts are exact at a sampling ratio of 0.25, of 0.01, even of 0, and in
a host that exports no traces at all. Code:
[ProcessMetricsListener.cs](../src/Observability/Metrics/ProcessMetricsListener.cs).

This is why process names must not contain ids: each distinct name is a new time series in every backend,
and most backends charge per series.

## Metrics bundles

A metric that never shows up is almost always a meter nobody subscribed to: the code creates
`new Meter("Orders")`, and nobody calls `AddMeter("Orders")`. A `MetricsBundle` removes that step:

```csharp
public sealed class OrderMetrics : MetricsBundle
{
    private readonly Counter<long> _placed;

    public OrderMetrics(IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
        : base(meterFactory, options)
    {
        _placed = Meter.CreateCounter<long>("orders.placed", "{order}", "Orders placed, by channel.");
    }

    public void Placed(string channel) => _placed.Add(1, new KeyValuePair<string, object?>("order.channel", channel));
}

services.AddMetricsBundle<OrderMetrics>();
```

Its meter is named `{service.name}.{type name}` in lower case — `order-service.ordermetrics` — and
`AddObservability` subscribes to `order-service.*`. A fixed name needs `[MeterName("…")]` and its own
`AddMeter`. Code: [MetricsBundle.cs](../src/Observability/Metrics/MetricsBundle.cs).

Use `IMeterFactory`, not `new Meter(...)`: meters from the factory belong to the service provider, so a
test host gets its own and parallel tests do not see each other's measurements.

## Naming

Follow the OpenTelemetry semantic conventions where one exists, and their style where not:

- lower case, dots between namespaces: `orders.placed`, `outbox.message.age`;
- the unit in the unit field, not the name: `s`, `By`, `{order}`;
- seconds for durations, as a `double` histogram;
- tags with a small, fixed set of values. A customer id, an order id or a URL with ids in it is not a tag.

## Expected dependency responses

Some error statuses are answers. A distributed lock that is already taken returns 409; a lookup that may
miss returns 404. Recorded as failed dependencies, they make the failure rate noise and real failures hide
in it.

```json
"ExpectedDependencyResponses": [
  { "PathContains": "/locks/", "StatusCodes": [ 404, 409 ] }
]
```

`ExpectedDependencyStatusProcessor` clears the error status of a matching HTTP client span and tags it
`dependency.expected_status = true`. Application Insights then records it as successful. Code:
[ExpectedDependencyStatusProcessor.cs](../src/Observability/ExpectedDependencyStatusProcessor.cs).

The HTTP client *metric* still carries the status code; filter on it there.

## In Application Insights

Custom metrics arrive in the `customMetrics` table (`AppMetrics` in the workspace), pre-aggregated per
export interval. Histograms arrive as count, sum, min and max — no percentiles. For latency percentiles use
the request and dependency tables, or export to a backend with native histograms.

Standard metrics — request and dependency counts and durations, pre-aggregated by the exporter before
sampling — are on. Their names and dimensions are fixed by Application Insights; they power the
Performance and Failures blades.

See [alerts and dashboards](alerts-and-dashboards.md) for alerts on these metrics.
