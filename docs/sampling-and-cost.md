# Sampling and cost

Telemetry is billed per gigabyte ingested and retained. Most of it is never read. Sampling and filtering
decide what you keep; this page is about keeping the right part.

## What to cut first

In order of value for money:

1. **Health probes.** A probe every five seconds per instance is most of a quiet service's requests and
   none of its insight. `ExcludedRequestPaths` (default `/health`, `/alive`) is not traced at all.
2. **Framework log noise.** ASP.NET Core and `HttpClient` write several `Information` lines per request
   that repeat what the spans say. Raise them to `Warning`; see
   [structured logging](structured-logging-and-scopes.md#log-levels-in-configuration).
3. **Duplicate dependencies.** The Azure SDK traces every call, and the HttpClient instrumentation traces
   it again. `AddObservability` drops the HttpClient span below an Azure SDK span.
4. **Per-item logs in loops.** `Debug` inside the loop, `Information` before and after.
5. **Empty polls.** A background loop that finds nothing to do should not write a trace or a log line
   every few seconds. The outbox publisher starts no span for an empty poll.
6. **Performance counters** that duplicate platform metrics. Off; see
   [the exporter](azure-monitor-exporter.md).

Only then sample traces.

## Sampling traces

`Observability:SamplingRatio` is the fraction of traces kept: `1.0` keeps all, `0.1` keeps one in ten.

- **Head sampling, on the trace id.** The decision is made when the trace starts, and every service makes
  the same decision for the same trace id. You see a whole trace or none of it.
- **Parent-based.** A service that receives a sampled `traceparent` records; an unsampled one does not.
- **One ratio everywhere.** Two services with different ratios still agree for traces both keep, but the
  service with the lower ratio leaves holes in the other's traces.

A ratio of 1.0 is right for most services at low volume. Lower it per environment when volume grows, and
look at the bill before and after.

## What sampling does to numbers

| Question | Sampled traces | Metrics |
|---|---|---|
| How many orders failed today? | wrong by the ratio, and noisy | exact |
| What is the p95 latency? | an estimate | exact for the histogram buckets |
| What happened in order 4711? | there, or not | — |

Application Insights stores the sampling rate with each item (`itemCount`) and its portal multiplies by it.
Your own KQL must too: `summarize sum(ItemCount)`, not `count()`. Better: count with
[metrics](metrics.md), which are measured before sampling. `process.completed` is exact at any ratio.

## What sampling must not touch

- **Logs.** The exporter's trace-based log sampler is off; log records are kept whatever happens to their
  trace. Control log volume with levels. See [the exporter](azure-monitor-exporter.md#keep-the-logs).
- **Exceptions.** An exception logged with `LogError` is a log record and is kept. An exception recorded
  only as a span event disappears with its span. Log failures.
- **Metrics.** Always exact.

## Errors are not sampled in

Head sampling decides before the outcome is known: a sampled-out request that fails has no trace. Keeping
all failed traces needs tail sampling in an OpenTelemetry Collector, which buffers whole traces and decides
at the end. It costs a collector deployment. Most teams find that error logs with their trace id, plus
exact metrics, are enough.

## Daily cap

Set a daily cap on the workspace as a fuse, not as a budget. When it trips, ingestion stops until the next
day — including the telemetry you need to find out why it tripped. Alert well before it: an alert on the
workspace's ingestion volume, per table.

## Retention

Thirty days in the interactive tier covers most investigations. Keep longer only what you must: raw
payloads in blob storage are far cheaper than logs. See [raw payload archive](raw-payload-archive.md).
Summary rules aggregate detail tables into small ones that you can keep longer; see
[alerts and dashboards](alerts-and-dashboards.md#summary-rules).
