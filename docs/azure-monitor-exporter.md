# The Azure Monitor exporter

`UseAzureMonitorWhenConfigured` adds the Application Insights exporter for traces, metrics and logs when
`APPLICATIONINSIGHTS_CONNECTION_STRING` is set, and does nothing otherwise. Locally and in tests there is no
exporter and no error. It then sets every default that changes cost or visibility explicitly.

Code: [AzureMonitorObservabilityExtensions.cs](../src/Observability.AzureMonitor/AzureMonitorObservabilityExtensions.cs),
[AzureMonitorDefaults.cs](../src/Observability.AzureMonitor/AzureMonitorDefaults.cs).

## Why pin the defaults

The exporter's defaults have changed between minor versions before. One upgrade switched the default
from "keep a ratio of traces" to "keep at most five traces per second". No code changed, no build broke;
the number of traces dropped by an order of magnitude and nobody noticed until an investigation found
nothing. A pinned value turns that into a visible diff.

| Option | Exporter default | Pinned to | Why |
|---|---|---|---|
| `SamplingRatio` | 1.0 | `Observability:SamplingRatio` | One setting for every exporter. |
| `TracesPerSecond` | 5 | `null` | When set, it wins over the ratio. |
| `EnableTraceBasedLogsSampler` | `true` | `false` | Otherwise every log line of a sampled-out trace is dropped. |
| `EnableStandardMetrics` | `true` | `true` | Exact request and dependency counts, pre-aggregated before sampling. |
| `EnablePerformanceCounters` | `true` | `false` | The platform already measures CPU and memory for an App Service or function app. |
| `EnableLiveMetrics` | `true` | `true` | Costs nothing while nobody watches. |
| `Transport` | default handler | a shared handler with a 5-minute connection lifetime | See below. |

## The sampler is replaced

`UseAzureMonitorExporter` replaces the tracer's sampler with its own. The `ParentBased(TraceIdRatioBased)`
sampler that `AddObservability` sets is gone. The exporter's sampler also decides on the trace id, so
services with the same ratio agree with each other. But a ratio set anywhere other than the exporter
options is ignored, which is why `UseAzureMonitorWhenConfigured` reads `Observability:SamplingRatio` and
passes it on.

## Keep the logs

With `EnableTraceBasedLogsSampler` on, the exporter drops the log records of every trace it did not
sample. At a ratio of 0.1, nine out of ten requests have no logs at all — including the error log of a
request that failed. Logs are the record of what the system decided; sample traces, keep logs, and control
log volume with levels. See [sampling and cost](sampling-and-cost.md).

## The connection pool

The exporter sends over HTTPS on a long-lived connection. With the default handler a pooled connection
lives for ever. When a network device drops it without closing it — a NAT timeout, a firewall failover —
the connection is half-open: writes seem to succeed, nothing arrives, and the exporter's own failure
logging goes through the same pipeline. A host can run for days sending nothing.

`AzureMonitorDefaults` gives the exporter one shared `SocketsHttpHandler` with a pooled connection lifetime
of five minutes and an idle timeout of one minute, so a dead connection is replaced within minutes. The
[heartbeat](telemetry-heartbeat.md) is the alarm for when it happens anyway.

## Sign in with a managed identity

The connection string identifies the resource; it is not a secret. With local authentication enabled,
anyone who has it can write to your telemetry. Disable local authentication on the Application Insights
resource and let the exporter sign in with Entra ID:

| `APPLICATIONINSIGHTS_AUTHENTICATION_STRING` | Credential |
|---|---|
| empty or missing | none: instrumentation-key ingestion |
| `Authorization=AAD` | the system-assigned managed identity |
| `Authorization=AAD;ClientId=<guid>` | the user-assigned managed identity with that client id |

The identity needs the **Monitoring Metrics Publisher** role on the Application Insights resource. Any
other value fails at start-up with a `FormatException`, not with silently missing telemetry. Code:
[AzureMonitorCredential.cs](../src/Observability.AzureMonitor/AzureMonitorCredential.cs).

For assigning roles to identities as code, see the
[Entra app registrations as code playbook](https://github.com/iverburgh/entra-app-registrations-as-code-playbook).

## Workspace-based resources

Create Application Insights as a workspace-based resource: the telemetry lands in a Log Analytics
workspace, in the `App*` tables, with the workspace's retention and access control. Point every service of
one environment at the same resource, so the application map and end-to-end transactions span services.
Thirty days of retention is a sensible start; archive longer only for what you must keep.

## How OpenTelemetry maps to Application Insights

| OpenTelemetry | Application Insights | Workspace table |
|---|---|---|
| Span, kind Server or Consumer | request | `AppRequests` |
| Span, kind Client, Producer or Internal | dependency | `AppDependencies` |
| Exception event on a span | exception | `AppExceptions` |
| Log record | trace | `AppTraces` (or `AppExceptions` with an exception) |
| Metric | custom metric | `AppMetrics` |
| Trace id | operation id | `OperationId` |
| `service.namespace` + `service.name` | cloud role name, `[namespace]/name` | `AppRoleName` |
| Span and log attributes | custom dimensions | `Properties` |

See [KQL queries](kql-queries.md).
