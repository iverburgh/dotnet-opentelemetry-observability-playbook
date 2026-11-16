# Telemetry heartbeat

No telemetry looks exactly like no problems. A dashboard with no errors, an alert that never fires, a
failure rate of zero — they are also what a broken exporter looks like.

## Why

A background worker ran for weeks, processing messages, while it sent no telemetry at all. The host was
healthy, the platform's metrics showed CPU and memory, the queue drained. Its traces, logs and custom
metrics were simply gone: a half-open connection in the exporter (see
[the exporter](azure-monitor-exporter.md#the-connection-pool)), then later a meter that was created but
never subscribed to. Nobody noticed until someone needed the logs.

You cannot alert on the absence of errors. You can alert on the absence of a signal that should always be
there.

## The heartbeat

`TelemetryHeartbeat` runs in every host that calls `AddObservability`. Every `HeartbeatInterval` (default
30 seconds) it:

- adds 1 to `telemetry.heartbeat` **twice**: once on a meter exported through the `{service.name}.*`
  wildcard (`telemetry.path = wildcard`) and once on a meter subscribed by its fixed name
  (`telemetry.path = explicit`);
- writes log event 1001, `Telemetry heartbeat from {ServiceName}`.

Code: [TelemetryHeartbeat.cs](../src/Observability/Heartbeat/TelemetryHeartbeat.cs).

## Reading it

| What you see | What is broken |
|---|---|
| Both series and the log line arrive | nothing |
| `explicit` arrives, `wildcard` does not | the wildcard meter subscription: your own metrics bundles are not exported either |
| Metrics arrive, the log line does not | log export, or a log level filter that drops `Observability` |
| Nothing arrives | the export itself: connection string, network, credential, or the host is down |

## The alert

Alert per service when the heartbeat stops:

```kusto
AppMetrics
| where TimeGenerated > ago(15m)
| where Name == "telemetry.heartbeat"
| extend Path = tostring(Properties["telemetry.path"])
| summarize Beats = sum(Sum) by AppRoleName, Path
```

A counter arrives pre-aggregated per export interval: `Sum` holds the increase.

Combine it with the list of services you expect, so a service that sends nothing at all appears with zero
beats instead of not at all:

```kusto
let expected = datatable(AppRoleName: string) ["[shop]/order-service", "[shop]/stock-worker"];
let beats = AppMetrics
    | where TimeGenerated > ago(15m) and Name == "telemetry.heartbeat"
    | summarize Beats = sum(Sum) by AppRoleName;
expected
| join kind=leftouter beats on AppRoleName
| where isnull(Beats) or Beats == 0
| project AppRoleName
```

Fire when the result has rows. `AppRoleName` is a column, so it becomes a dimension and appears in the
notification; see [alerts and dashboards](alerts-and-dashboards.md#what-the-notification-shows).

Fifteen minutes is long enough to ride out a deployment or a scale-in, and short enough to matter.
Exclude a service that scales to zero, or alert on its function invocations instead.

## Cost

Two metric points and one log line per instance every 30 seconds: about 3 000 log lines per instance per
day. Raise `HeartbeatInterval` to a few minutes if that matters, or set the `Observability.Heartbeat`
category to `Warning` and rely on the metrics.

## Turning it off

`EnableHeartbeat: false` — in tests that count telemetry, for example. The service keeps running; nothing
else depends on it.

## Start-up order matters

Hosted services start in registration order. The heartbeat beats as soon as it starts; registered before
the OpenTelemetry meter provider, its first beat goes to a meter nobody listens to yet and is lost.
`AddObservability` registers it after `AddOpenTelemetry`. See [pitfalls](pitfalls.md).
