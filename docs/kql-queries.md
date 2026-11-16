# KQL queries

Application Insights stores telemetry in a Log Analytics workspace, in the `App*` tables with PascalCase
columns. These queries assume that schema. In the classic Application Insights query blade, the tables are
`requests`, `dependencies`, `traces`, `exceptions` and `customMetrics`, with camelCase columns and
`customDimensions` instead of `Properties`.

## Everything for one correlation id

The support desk has an `x-correlation-id`. It is the trace id, stored as `OperationId`:

```kusto
let id = "1c72f1a0b2c34d5e6f708192a3b4c5d6";
union AppRequests, AppDependencies, AppTraces, AppExceptions
| where OperationId == id
| extend Item = coalesce(Name, Message, OuterMessage)
| project TimeGenerated, AppRoleName, Type, Item, DurationMs, Success, ResultCode, SeverityLevel,
          Process = tostring(Properties["process.name"])
| order by TimeGenerated asc
```

Every service, the outbox publish and the consumer, in order. Narrow the time range first: `OperationId` is
indexed, but a 30-day union is still a big scan.

## From a business id to its traces

```kusto
AppTraces
| where TimeGenerated > ago(1d)
| where tostring(Properties["OrderId"]) == "f4b1…"
| distinct OperationId
```

Then run the first query for each.

## Failures by process

```kusto
AppDependencies
| where TimeGenerated > ago(1d)
| where Type == "InProc" and isnotempty(Properties["process.name"])
| summarize Failed = sumif(ItemCount, Success == false), Total = sum(ItemCount)
    by Process = tostring(Properties["process.name"]), AppRoleName
| order by Failed desc
```

Process spans are internal spans: `AppDependencies` with type `InProc`. For exact counts, use the metric
instead:

```kusto
AppMetrics
| where TimeGenerated > ago(1d) and Name == "process.completed"
| extend Process = tostring(Properties["process.name"]), Result = tostring(Properties["process.result"])
| summarize Count = sum(Sum) by Process, Result
```

Custom metrics arrive pre-aggregated per export interval; for a counter, `Sum` holds the increase.

## Exceptions, grouped

```kusto
AppExceptions
| where TimeGenerated > ago(1d)
| summarize Count = sum(ItemCount), Example = any(OperationId) by AppRoleName, ProblemId, OuterMessage
| order by Count desc
```

`Example` is an `OperationId` to start the timeline query with.

## The gateway and the trace

```kusto
AppRequests
| where Properties["gateway.transaction_id"] == "2f7c…"
| project TimeGenerated, OperationId, Name, ResultCode
```

The id from the gateway's access log finds the request and its trace. See
[correlation ids](correlation-ids-and-headers.md#the-gateway-transaction-id).

## Messages that left the request's trace

```kusto
AppRequests
| where TimeGenerated > ago(1d)
| where Properties["messaging.trace.reparented"] == "true"
| summarize count() by AppRoleName, Name
```

A consumer span that was reparented joined the producer's trace — expected for triggers. A consumer that
should appear here and does not is starting traces of its own: its producer does not send `traceparent`.

## Outbox delay

```kusto
AppMetrics
| where TimeGenerated > ago(1d) and Name == "outbox.message.age"
| summarize AvgSeconds = sum(Sum) / sum(ItemCount), MaxSeconds = max(Max)
    by bin(TimeGenerated, 5m), MessageType = tostring(Properties["outbox.message_type"])
| render timechart
```

## Ingestion by table and service

```kusto
union withsource = Table App*
| where TimeGenerated > ago(1d)
| summarize GB = sum(_BilledSize) / 1e9 by Table, AppRoleName
| order by GB desc
```

Run this before lowering a sampling ratio: the biggest line is often a log statement in a loop. See
[sampling and cost](sampling-and-cost.md).

## Tips

- `Properties` is dynamic. Cast with `tostring()` before comparing or grouping.
- Use `sum(ItemCount)`, not `count()`, on sampled tables.
- Save queries in a query pack or as functions in the workspace, and keep their source in Git next to the
  alerts that use them. See [alerts and dashboards](alerts-and-dashboards.md).
