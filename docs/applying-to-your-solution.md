# Applying it to your solution

You probably have telemetry already: the classic Application Insights SDK, a home-made correlation id
middleware, `Serilog` sinks, or a mix. This page is an order of work that keeps the telemetry useful at
every step.

## 1. Inventory

For each deployable unit, write down:

- the role name it reports under today, and which Application Insights resource;
- the SDK — classic Application Insights, the Azure Monitor OpenTelemetry distro, the exporter, or
  something else;
- how it correlates: a custom correlation id header? stored in the database? passed in messages?
- every hop that is not HTTP: queues, topics, an outbox, files, scheduled jobs;
- the alerts and dashboards that depend on it, and the queries they use.

The last item decides what you must keep working while you migrate.

## 2. One pipeline per process

Move each service to OpenTelemetry with `AddObservability` and one exporter. Remove the classic SDK in the
same change: two pipelines double the cost and split the data. In function apps, set `telemetryMode` to
`OpenTelemetry` in `host.json`. Keep the role names: set `ServiceName` and `ServiceNamespace` so
`AppRoleName` stays the same, or update the queries in the same release.

Pin the exporter defaults on day one; see [the exporter](azure-monitor-exporter.md).

## 3. Replace the correlation id with the trace id

- Return the trace id as `x-correlation-id` (`UseObservability`).
- Stop generating, storing and forwarding the old id. Keep reading an inbound one into a log scope for a
  transition period if clients send it, so old support tickets stay findable.
- Change support's runbook: search `OperationId`.

See [correlation ids and headers](correlation-ids-and-headers.md).

## 4. Close the non-HTTP hops

In order of how often support needs them:

1. **Messages sent directly.** Inject `traceparent` on send, start a consumer span from it on receive.
2. **The outbox.** Add `TraceParent` and `TraceState` columns, store them on write, restore them on
   publish. Existing rows have none; they publish in the run's trace until they drain.
3. **Function triggers.** `UseObservability()` on the worker.
4. **Scheduled work.** A new trace per run, with a process name.

After each, run the end-to-end correlation test; see [testing](testing.md#the-end-to-end-correlation-test).

## 5. Name the processes

Take the business processes from the functional design, not from the code. Add `[Process]` to the entry
points — endpoints, functions, consumers — and let the breadcrumb show how they chain. Agree on the names
with the people who will read the dashboards. See [process logging](process-logging.md).

## 6. Clean up the logs

- Replace interpolated messages with templates and `[LoggerMessage]` methods.
- Put business identifiers in scopes at the entry point.
- Move per-item logs in loops to `Debug`.
- Move log levels to configuration.
- Move raw payloads out of logs into a [raw payload archive](raw-payload-archive.md): `AddPayloadArchive`
  and `UsePayloadArchive` for inbound requests, `AddPayloadArchive("container")` on the `HttpClient` of each
  partner. Start with `WriteAfterResponse` on busy endpoints, and give the managed identity a role on each
  archive container, not on the account.

Run the [ingestion query](kql-queries.md#ingestion-by-table-and-service) before and after.

## 7. Alerts on metrics

Replace alerts that count log lines or sampled requests with alerts on `process.completed`,
`outbox.message.age` and the heartbeat. Keep the old alert until the new one has fired correctly once. See
[alerts and dashboards](alerts-and-dashboards.md).

## 8. Then sample

Only when the volume is clean and the alerts are on metrics, lower `SamplingRatio` — per environment,
starting where volume is highest. See [sampling and cost](sampling-and-cost.md).

## Checklist

- [ ] One OpenTelemetry pipeline per process; classic SDK removed
- [ ] Exporter defaults pinned; managed identity sign-in; local authentication disabled
- [ ] `x-correlation-id` = trace id on every response, a 500 included
- [ ] Messages, outbox, triggers and scheduled work keep or link the trace
- [ ] Functional process names on every entry point
- [ ] Health probes not traced; framework logs at `Warning`
- [ ] Structured logs with scopes; no payloads in logs
- [ ] Raw partner payloads archived by trace id, best effort, with container-scoped access
- [ ] Heartbeat alert per service
- [ ] Alerts on metrics, with dimensions a responder can act on
- [ ] End-to-end correlation test in the pipeline
