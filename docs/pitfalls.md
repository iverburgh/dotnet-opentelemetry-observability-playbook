# Pitfalls

Each of these cost someone hours or weeks. Read them before you start.

## Nothing arrives, and nothing says so

- **A half-open connection stops all exports.** A pooled connection dropped by the network keeps
  accepting writes. Give the exporter's handler a connection lifetime; see
  [the exporter](azure-monitor-exporter.md#the-connection-pool).
- **A meter nobody subscribed to.** `new Meter("Orders")` without `AddMeter("Orders")` records into the
  void. Use [metrics bundles](metrics.md#metrics-bundles).
- **A new `ActivitySource` nobody subscribed to.** Same for spans: `StartActivity` returns `null` and the
  code runs fine. Add every source with `AddSource`.
- **Silence looks like health.** Alert on a missing [heartbeat](telemetry-heartbeat.md).

## Defaults that change under you

- **The exporter replaces your sampler.** A ratio set on the tracer is ignored once
  `UseAzureMonitorExporter` runs.
- **Rate-limited sampling wins over the ratio.** A version bump made "five traces per second" the default.
  Pin `TracesPerSecond = null`.
- **The trace-based log sampler drops logs.** On by default: every log line of a sampled-out trace is
  gone, including errors. Turn it off.
- **The configuration binder appends to arrays.** A default list in a property initialiser is always in
  the result, and configuration adds to it. Use nullable arrays with the default applied when reading.

## Broken traces

- **A consumer that starts its own trace.** The broker does not carry context unless you put it in the
  message. See [messaging](outbox-and-messaging-correlation.md#messages-without-an-outbox).
- **An outbox publisher that is the parent of everything.** Every message ends up in the publisher's trace
  and the request's id finds nothing. Store and restore the context; see
  [outbox](outbox-and-messaging-correlation.md).
- **Consumer kind on the publisher.** Sends show up as incoming requests. Use `Producer`.
- **A loop as one endless trace.** Each run of a timer or poller should be a new trace with a link; see
  [tracing](tracing-and-w3c-context.md#starting-a-new-trace-on-purpose).
- **The gateway strips `traceparent`.** Tag the gateway's own id; see
  [correlation ids](correlation-ids-and-headers.md#the-gateway-transaction-id).

## .NET runtime surprises

- **`Activity.Stop` sets `Activity.Current` to the stopped span's parent.** For a span in a new trace, the
  parent is `null`: after disposing a root span in synchronous code, `Activity.Current` is `null`, not the
  span that was current before. Async methods hide it, because their execution context is restored when
  they return. `StartRootProcess` returns a scope that restores the previous span; see
  [RootProcessScope.cs](../src/Observability/RootProcessScope.cs).
- **`Activity.Baggage` lists the span's own items first, then its parents'.** When copying baggage to tags,
  the first value per key is the nearest one; a later duplicate is an outer value.
- **Hosted services start in registration order.** A service that emits telemetry on start, registered
  before `AddOpenTelemetry`, emits into a meter provider that does not exist yet. Register it after.
- **`BackgroundService.ExecuteAsync` no longer runs synchronously on start** (.NET 10). Code that relied
  on the first part of `ExecuteAsync` having run when `StartAsync` returns — tests especially — must wait.
- **An exception that reaches Kestrel clears the response headers.** The 500 goes out without
  `x-correlation-id`. Use `UseExceptionHandler` with `AddProblemDetails`; headers added in `OnStarting`
  survive it.

## Numbers that lie

- **Counting from sampled tables.** `count()` on `AppRequests` at ratio 0.1 is a tenth of the truth. Use
  `sum(ItemCount)`, or better, metrics.
- **Expected answers counted as failures.** A 409 from a lock or a 404 from a lookup is not a failed
  dependency. Configure [expected dependency responses](metrics.md#expected-dependency-responses).
- **Two spans per Azure SDK call.** The SDK and the HttpClient instrumentation both record it. Filter the
  HttpClient span below `Azure.Core.Http`.
- **401 and 404 failing processes.** Usually the caller's problem; they are non-failure codes by default.

## Cost and privacy

- **Ids in names.** A process name, span name or metric tag with an order id creates a series per order.
- **Baggage to partners.** Baggage goes with every outgoing request unless you scope it; see
  [tracing](tracing-and-w3c-context.md#baggage-stays-inside).
- **Copying all inbound baggage to tags.** Anyone can add columns, and personal data, to your telemetry.
  Allow-list the keys.
- **Payloads in logs.** Expensive, truncated, and readable by everyone with workspace access. Use a
  [raw payload archive](raw-payload-archive.md).
- **Health probes traced.** Most of the volume, none of the insight.
- **Two telemetry pipelines in a function app.** Set `telemetryMode` to `OpenTelemetry` in `host.json`; see
  [Azure Functions](azure-functions.md).
