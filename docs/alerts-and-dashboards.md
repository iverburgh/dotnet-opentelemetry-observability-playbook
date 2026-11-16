# Alerts and dashboards

Telemetry nobody looks at is a cost, not a safeguard. This page covers turning it into alerts that reach
the right people with enough context to act, and dashboards that lead from "something is wrong" to the
one trace that explains it.

## Alert on metrics, explain with logs

| Alert on | Signal | Why |
|---|---|---|
| A process fails too often | `process.completed` with `process.result = error`, per `process.name` | Exact at any sampling ratio; named in business terms. |
| A process is too slow | `process.duration` | Same. |
| Messages stop flowing | `outbox.message.age`, `outbox.messages.failed` | Delay is what a consumer feels. |
| A service goes silent | `telemetry.heartbeat` missing | The only alert that notices a broken pipeline; see [heartbeat](telemetry-heartbeat.md). |
| Unexpected exceptions | `AppExceptions` by `ProblemId`, new or rising | Logs, because the exception type and message are the point. |

Avoid alerts on raw failure counts of HTTP dependencies until expected answers are excluded; see
[expected dependency responses](metrics.md#expected-dependency-responses).

## Log search alerts

A scheduled query rule runs KQL every few minutes and fires when the result meets a condition. Write the
query so it returns one row per thing that is wrong:

```kusto
AppMetrics
| where Name == "process.completed"
| extend Process = tostring(Properties["process.name"]), Result = tostring(Properties["process.result"])
| summarize Errors = sumif(Sum, Result == "error"), Total = sum(Sum) by AppRoleName, Process
| where Total >= 20 and Errors * 1.0 / Total > 0.05
| project AppRoleName, Process, Errors, Total
```

Evaluate over 15 minutes, every 5 minutes; fire when the number of rows is greater than zero. The
`Total >= 20` guard stops one failure out of two calls at night from paging anyone.

## What the notification shows

A notification shows the rule's name, its description and its **dimensions** — not the query result. A
column becomes a dimension only if you split by it in the rule. Two consequences:

- Project the columns a responder needs — the service, the process, a count, an example `OperationId` —
  and split by them. A column you computed with an alias (`Example = any(OperationId)`) can be a dimension
  too.
- Keep the dimension count low. Each combination is evaluated as its own alert instance.

Put the investigation query, with the dimension values filled in, in the rule's description or a link, so
the responder starts at the right place.

## Action groups

Route alerts through an action group: e-mail for the owning team, and a webhook to the team's chat channel
for anything that needs eyes during the day. Keep severity meaningful — `Sev1` wakes someone up, `Sev3`
waits for the morning — and define rules and action groups as code, next to the service.

## Summary rules

Log Analytics summary rules run a KQL aggregation on a schedule and write the result to a custom table.
They turn expensive detail tables into small ones you can keep for a year and query in a second: process
outcomes per hour, exceptions per problem per day, ingestion per table.

| Setting | Use | Why |
|---|---|---|
| Bin size | 60 minutes | Hourly is enough for trends and cheap to keep. |
| Bin delay | 10 minutes | Telemetry arrives late; a bin that runs at once misses part of it. |

A summary rule's query cannot use functions defined in the workspace, `union *` or its own time filter:
the rule sets the time window. Alert on the rule's own failures, or a broken rule silently leaves gaps.

## Dashboards

Build dashboards as a funnel, from overview to one trace:

1. **Overview per service.** Heartbeat, request rate, failure rate, process outcomes, outbox age. Green or
   not.
2. **Failures.** Exceptions by `ProblemId` and process, each with an example `OperationId`.
3. **One trace.** The [timeline query](kql-queries.md#everything-for-one-correlation-id) for the
   `OperationId` clicked in step 2, or the end-to-end transaction view of Application Insights.

Workbooks and Grafana both work on the workspace. Whatever you use, keep dashboards as code — exported
JSON in Git, with stable ids and the data source as a variable — so a dashboard survives a deleted
resource and changes go through review.

## The application map

The Application Insights application map draws services and their dependencies from requests and
dependencies with matching trace context. It is only as good as the propagation: a hop without
`traceparent` is a missing edge. A missing edge on the map is a quick way to find one.
