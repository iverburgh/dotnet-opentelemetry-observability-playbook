# Azure Functions

The isolated worker model runs your code in a separate process from the Functions host. The host receives
the trigger and creates the invocation span; your process does the work. OpenTelemetry works in the
worker, with two things to get right: one exporter, and the trace of a Service Bus message.

## Set up

```csharp
var builder = FunctionsApplication.CreateBuilder(args);

builder.UseObservability();                     // Service Bus trace context, process spans
builder.Services
    .AddObservability(builder.Configuration)
    .UseAzureMonitorWhenConfigured(builder.Configuration);

builder.Build().Run();
```

And in `host.json`, let the worker own the telemetry:

```json
{
  "version": "2.0",
  "telemetryMode": "OpenTelemetry",
  "logging": {
    "logLevel": { "default": "Warning", "Function": "Information" }
  }
}
```

With `telemetryMode` set to `OpenTelemetry`, the host emits OpenTelemetry too and passes the invocation's
trace context to the worker. Without it, the host writes its own Application Insights telemetry with the
classic SDK and the worker writes another set: two request rows per invocation and two bills.

Code: [FunctionsObservabilityExtensions.cs](../src/Observability.Functions/FunctionsObservabilityExtensions.cs).

## Service Bus triggers

A message sent from a request carries that request's `traceparent` in its application properties. The
invocation, however, starts in a trace the host created for the trigger. If the worker does nothing, the
consumer's spans and logs are in the trigger's trace, and the request's `x-correlation-id` does not find
them.

`ServiceBusTraceContextMiddleware` runs first for every function with a `serviceBusTrigger` binding. It
reads `traceparent` — or `Diagnostic-Id`, which older senders write — from the `ApplicationProperties`
binding data and starts a consumer span:

| The message's trace | Consumer span |
|---|---|
| Same as the invocation's | an ordinary child |
| Different | in the message's trace, with a link to the invocation span, tagged `messaging.trace.reparented = true` |
| None | an ordinary child of the invocation |

Everything the function does — process spans, HTTP calls, log lines — is then in the request's trace.
Code: [ServiceBusTraceContextMiddleware.cs](../src/Observability.Functions/ServiceBusTraceContextMiddleware.cs).

The binding data arrives as JSON or as a dictionary depending on the extension version;
[ServiceBusApplicationProperties.cs](../src/Observability.Functions/ServiceBusApplicationProperties.cs)
reads both.

## Process spans

`ProcessFunctionMiddleware` runs next. A function whose method or class has `[Process]` runs inside a
process span:

```csharp
[Process("Reserve stock")]
public sealed class ReserveStockFunction(ILogger<ReserveStockFunction> logger)
{
    [Function("ReserveStock")]
    public Task Run([ServiceBusTrigger("order-placed", "stock")] ServiceBusReceivedMessage message) { … }
}
```

An exception fails the process span and is rethrown, so the host's retry and dead-letter behaviour does
not change. The method's attribute wins over the class's. The middleware finds the method from the
function's entry point; see [FunctionEntryPoint.cs](../src/Observability.Functions/FunctionEntryPoint.cs).

## Timer triggers

A timer is a new unit of work every time. The host already starts a new trace per invocation; give it a
process name with `[Process("Reconcile payments")]` and it is countable like any other process.

## The heartbeat in a function app

The heartbeat is a hosted service and runs while the worker process runs. On a consumption plan the worker
stops when idle, so its heartbeat stops too. Alert on the heartbeat for always-on plans; for consumption,
alert on missing invocations of a timer function instead.

## Deployment slots

A slot swap moves the code, not the setting values marked as slot settings. Mark
`APPLICATIONINSIGHTS_CONNECTION_STRING` as a slot setting if the staging slot should report to a different
resource; otherwise both slots report under the same role name and a smoke test on staging looks like
production traffic.
