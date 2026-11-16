# Testing

Telemetry is behaviour. A correlation id that is missing on a 500, a consumer that starts its own trace, a
metric nobody subscribed to — each is a bug, found much later by someone who needed it. Test it like any
other behaviour: run the real host, export to memory, assert on what came out.

## The harness

[TelemetryHarness.cs](../tests/Observability.Tests/TelemetryHarness.cs) starts a host with
`AddObservability` and the in-memory exporters from `OpenTelemetry.Exporter.InMemory`:

```csharp
await using var harness = await TelemetryHarness.StartAsync(
    new Dictionary<string, string?> { ["Observability:SamplingRatio"] = "0" },
    services: (services, configuration) => services.AddOutbox(configuration, runPublisher: false),
    telemetry: otel => otel.WithOutboxInstrumentation());

// … run the code under test …

var publish = harness.Span("publish OrderPlaced");
harness.FlushMetrics();
Assert.That(harness.Sum("process.completed", ("process.result", "error")), Is.EqualTo(1));
```

- Spans are exported when they end, so they are in `Spans` as soon as the code returns.
- Metrics are exported on flush: call `FlushMetrics()` before asserting.
- The heartbeat is off in the defaults, so it does not add metrics or log lines to every test.

For ASP.NET Core, [AspNetCoreTests.cs](../tests/Observability.Tests/AspNetCoreTests.cs) uses `TestServer`
with the real middleware and an in-memory exporter; requests go through `HttpClient`, and the assertions
read headers and spans.

## What to test

| Behaviour | Test |
|---|---|
| Every response, including a 500, carries `x-correlation-id` = trace id | `An_unhandled_exception_fails_the_process_and_the_500_still_has_a_correlation_id` |
| An inbound correlation id is ignored | `Every_response_carries_the_trace_id_as_correlation_id_and_ignores_an_inbound_one` |
| The outbox keeps the request's trace | `In_parent_mode_the_request_publish_and_consumer_share_one_trace` |
| The context survives without a recorded span | `A_message_still_carries_the_request_context_when_the_publish_span_is_not_created` |
| Process metrics are exact when traces are sampled out | `ProcessorAndMetricsTests` |
| Baggage only reaches internal hosts | `PropagationTests` |
| Configuration errors stop the host | `OptionsTests` in [HostingTests.cs](../tests/Observability.Tests/HostingTests.cs) |

## The end-to-end correlation test

In a deployed environment, one test does what support does:

1. Call the API and read `x-correlation-id` from the response.
2. Wait for the asynchronous work — the outbox, the consumer.
3. Query the workspace for `OperationId == correlationId` and assert that the consumer's request, or its
   process span, is there.

It fails when anyone breaks propagation anywhere along the way: a consumer that drops `traceparent`, a
publisher that starts its own trace, a host setting that splits the telemetry. Ingestion takes a few
minutes; poll with a timeout instead of sleeping.

## Lessons

- **Sampling ratio 0 is a test case.** With nothing recorded, metrics must still be exact and the
  `traceparent` must still travel. Both are easy to break and invisible at ratio 1.
- **Listen to the source yourself when the exporter will not.** A span that no listener samples is never
  created: `StartActivity` returns `null`. A test that needs the span regardless adds an
  `ActivityListener` with `AllDataAndRecorded`.
- **Background services start after `StartAsync` returns.** Since .NET 10, `BackgroundService.ExecuteAsync`
  runs on the thread pool, not synchronously up to its first `await`. Wait for a signal — a log event via
  `FakeLogCollector`, an item in a channel — instead of asserting right after the host starts.
- **Use `FakeTimeProvider`** for ages, durations and timers. The outbox age test advances the clock by
  42 seconds and asserts 42.
- **Use `IMeterFactory`.** Meters from the service provider are per host, so parallel tests do not see each
  other's measurements. A static `Meter` is shared by every test in the process.
- **Restore global state.** `DistributedContextPropagator.Current` and `Activity.Current` are process-wide or
  flow with the execution context. A test that changes them restores them in `finally`.
