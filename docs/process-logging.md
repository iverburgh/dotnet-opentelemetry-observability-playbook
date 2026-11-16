# Process logging

A span named `POST /orders/{id}/cancel` tells an engineer which endpoint ran. It does not tell anyone
which business process that was, which process started it three services earlier, or how often that
process fails. A *process span* does.

## A process span

`ProcessTracer.StartProcess("Cancel order")` starts an internal span that stands for a business process,
and puts these keys in its baggage — so every span, log record and downstream service below it knows
them:

| Key | Value | Example |
|---|---|---|
| `process.name` | this process | `Reserve stock` |
| `process.step` | an optional step within it | `Validate` |
| `process.breadcrumb` | the chain of processes so far | `Place order > Reserve stock` |
| `process.origin` | the service and process that started the chain | `order-service/Place order` |
| `process.domain`, `process.subdomain` | from configuration | `ordering` |

The breadcrumb crosses services: the baggage arrives with the request, and the next process appends to
it. It is cut to 256 characters, keeping the most recent part. A step of an outer process is cleared for
an inner one.

Code: [ProcessTracer.cs](../src/Observability/ProcessTracer.cs),
[ProcessBaggage.cs](../src/Observability/ProcessBaggage.cs).

## Declaring a process

On an endpoint:

```csharp
app.MapPost("/orders", PlaceOrder).WithMetadata(new ProcessAttribute("Place order"));
```

On a controller action, a function or their class — the method wins:

```csharp
[Process("Import orders")]
public sealed class ImportFunction
{
    [Function("ImportOrders")]
    public Task Run(...) { }

    [Function("RetryImport"), Process("Import orders", Step = "Retry")]
    public Task Retry(...) { }
}
```

The ASP.NET Core and Functions middleware start the span. In other code, start it yourself:

```csharp
using var process = tracer.StartProcess("Reconcile payments", baggage: [new("tenant.id", tenantId)]);
```

Extra baggage keys are lower-cased and tagged on the process span.

## Names are functional

The name is a metric dimension and a filter in every dashboard. Two rules:

- **Name the business process**, in words from the functional design: "Place order", "Cancel order",
  "Reserve stock". Not the class, not the route. A product owner can read the dashboard; the name survives
  a refactoring.
- **Never put an id in it.** "Cancel order 4711" creates one metric series per order. The id belongs in a
  log scope.

## When a process fails

The status of the process span is the outcome of the process.

| Situation | Status |
|---|---|
| The code throws | `Error`, with the exception recorded as an event |
| HTTP status 400 or above, except `NonFailureStatusCodes` (default 401, 404) | `Error`, description `HTTP 409` |
| The code calls `ProcessTracer.SetResult(...)` or `ProcessTracer.Fail(exception)` | what it says |
| Otherwise | `Unset`, which means ok |

401 and 404 are usually the caller's problem — an expired token, a lookup of something that does not
exist — and counting them as failures makes every failure rate useless. Adjust the list per service.

`SetResult` and `Fail` act on the *nearest process span*, not on the current span: code deep inside a
repository call can fail the process it serves.

## Baggage becomes columns

Baggage is not exported. It travels with the trace, but no backend stores it. `BaggageToTagsProcessor`
copies it to every span when the span ends, so `process.name` is a column on every dependency, every
exception, every request — and, through the log scope, every log line.

Only allow-listed keys are copied: the process keys plus `BaggageTagKeys`. A caller can send any
`baggage` header; copying all of it would let anyone add columns, and personal data, to your telemetry.
The nearest value wins; a tag the span already has is never overwritten.

## Process metrics

Every process span that ends is counted, by name and result, in `process.completed` and
`process.duration`. The listener sees the span even when the trace is sampled out, so the counts are
exact at any sampling ratio. See [metrics](metrics.md#process-metrics).

## A new trace for a unit of work

A timer or a batch job should not be one endless trace. `StartRootProcess` starts the process in a new
trace with a link to the current span, and its scope restores the current span when it is disposed. See
[tracing](tracing-and-w3c-context.md#starting-a-new-trace-on-purpose).
