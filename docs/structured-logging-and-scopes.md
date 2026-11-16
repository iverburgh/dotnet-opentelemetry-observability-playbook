# Structured logging and scopes

Traces show the shape of a request. Logs say what the code decided and why. With OpenTelemetry, every log
record written inside a span carries its trace id and span id, so a log line is one click from its trace —
as long as the logging is structured.

## Rules

| Rule | Why |
|---|---|
| Message templates, never interpolation | `"Placed order {OrderId}"` stores `OrderId` as a column; `$"Placed order {orderId}"` stores one unique string per order — no column, no grouping, and the formatting runs even when the level is off. |
| Source-generated log methods | `[LoggerMessage]` gives an event id, a level and a template in one place, with no boxing and no template parsing per call. |
| Business identifiers in a scope | Set `OrderId` once with `BeginScope`; every line inside, including the ones libraries write, gets it. |
| At least one `Information` line per code path | A support engineer must see which branch ran — including "nothing to do" and "skipped because…". |
| Loops: `Information` before and after, `Debug` inside | One line per item at `Information` turns a batch of 10 000 into 10 000 billed rows. |
| Log levels in configuration, not code | Raise a namespace to `Debug` in one environment without a deployment. |
| Never log secrets or full payloads | Logs are kept, copied and read by more people than the database. See [raw payload archive](raw-payload-archive.md). |

## Source-generated log methods

```csharp
internal static partial class OrderLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Placed order for {Quantity} x {Sku}")]
    public static partial void OrderPlaced(ILogger logger, string sku, int quantity);
}
```

Give every event an id, unique within the service. An alert or a query on an event id survives a reworded
message. The library's own events use 1001–1004 and the outbox 2001–2003; see
[Log.cs](../src/Observability/Log.cs).

## Scopes

```csharp
using (logger.BeginScope(new Dictionary<string, object?> { ["OrderId"] = orderId, ["Sku"] = sku }))
{
    OrderLog.OrderPlaced(logger, sku, quantity);
    await repository.SaveAsync(order, cancellationToken);  // its log lines get OrderId and Sku too
}
```

Use a dictionary. A scope created from a template string (`BeginScope("Order {OrderId}", id)`) also works,
but then the scope's message becomes an attribute too, and some exporters handle it differently.

`AddObservability` turns on `IncludeScopes`, so scope values become attributes of each log record —
`Properties` columns in Application Insights. It also turns on `IncludeFormattedMessage`, so the rendered
message is stored alongside the template.

## Logs and process names

The process baggage is not a log scope; it is part of the trace. A log line written inside a process span
is still findable by process through its trace id: join `AppTraces` to `AppDependencies` or `AppRequests`
on `OperationId`, where `process.name` is a column. See [KQL queries](kql-queries.md).

If you want `process.name` on the log line itself, add a scope in the place that starts the process.

## Logging domain events

In a domain-driven design, a domain event is a fact the business cares about: "order placed",
"payment refused". A single notification handler that logs every domain event, with its type and the
aggregate id in a scope, gives a business-level timeline per request without a log call in every handler:

```csharp
internal sealed class DomainEventLogger<TEvent>(ILogger<TEvent> logger) : INotificationHandler<TEvent>
    where TEvent : IDomainEvent
{
    public Task Handle(TEvent domainEvent, CancellationToken cancellationToken)
    {
        DomainLog.Raised(logger, typeof(TEvent).Name, domainEvent.AggregateId);
        return Task.CompletedTask;
    }
}
```

See the [Domain-Driven Design playbook](https://github.com/iverburgh/ddd-dotnet-playbook) for the events
themselves.

## Counting

Do not count from logs. "How many orders failed today" from `AppTraces` is wrong as soon as anything is
sampled, and expensive when nothing is. Count with [metrics](metrics.md); use logs to explain a case.

## Log levels in configuration

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Azure.Core": "Warning",
      "System.Net.Http.HttpClient": "Warning"
    }
  }
}
```

The framework writes several `Information` lines per HTTP call; the spans already contain that. Keep the
filter in configuration so one environment can be made noisier for an investigation and quiet again
without a release. With Azure App Configuration, a refreshable key changes the level at runtime; see the
[App Configuration playbook](https://github.com/iverburgh/azure-app-configuration-playbook).
