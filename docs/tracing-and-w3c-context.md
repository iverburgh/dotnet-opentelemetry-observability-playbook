# Tracing and W3C trace context

A trace is a tree of spans with one trace id. It only stays one tree if every hop — HTTP, a message, a
timer that picks up stored work — carries the context on. This page covers the rules for each hop.

## The resource

Every span, metric and log record carries the resource: who sent it. `AddObservability` sets
`service.name` and, if configured, `service.namespace`. Application Insights turns them into the *cloud
role name*, `AppRoleName` in the workspace tables, as `[namespace]/name` — or just the name without a
namespace. Choose them once and keep them:
dashboards, alerts and saved queries filter on them.

One service name per deployable unit. Two instances of the same app share it; an API and its worker do
not.

## The W3C headers

| Header | Carries | Example |
|---|---|---|
| `traceparent` | version, trace id, parent span id, flags (sampled) | `00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01` |
| `tracestate` | vendor-specific state, passed on unchanged | `vendor=1` |
| `baggage` | key-value pairs for everything downstream | `process.name=Place%20order` |

.NET writes and reads them through `DistributedContextPropagator.Current`. `HttpClient` injects them,
ASP.NET Core extracts them, and the OpenTelemetry instrumentation turns both sides into spans. You only
write code for hops that are not HTTP: messages, stored work, files.

## Sampling follows the parent

The sampler is `ParentBased(TraceIdRatioBased(SamplingRatio))`. A request that arrives with a sampled
`traceparent` is recorded; one that arrives unsampled is not; a new trace is sampled by its trace id.
Every service makes the same decision for the same trace, so you never see half a trace.

With the Application Insights exporter, its own sampler replaces this one. It also decides on the trace
id, so the result is the same as long as every service uses the same ratio. See
[the Azure Monitor exporter](azure-monitor-exporter.md#the-sampler-is-replaced).

## Baggage stays inside

Baggage travels with every outgoing request. That is the point inside your system, and a leak outside it:
a partner API has no business receiving your process names, tenant ids or anything else you put in it.

`ScopedBaggagePropagator` wraps the propagator and drops the `baggage` header for any `HttpRequestMessage`
to a host that is not in `InternalHosts`. `traceparent` still goes everywhere, so a partner that traces
can still correlate.

| Entry | Matches |
|---|---|
| `billing` | the host `billing` exactly |
| `.internal.example` | `orders.internal.example`, `a.b.internal.example` |
| `*` | every host — only for a system with no external calls |

Messages and other non-HTTP carriers keep their baggage: you decide what goes into them.

Code: [ScopedBaggagePropagator.cs](../src/Observability/ScopedBaggagePropagator.cs). The propagator is
installed by a hosted service when the host starts and the previous one is restored when it stops.

## Do not trust inbound context

A caller controls its headers. Two consequences:

- **Inbound baggage becomes columns only for allow-listed keys.** See
  [process logging](process-logging.md#baggage-becomes-columns).
- **A public entry point should not continue an external trace.** If anyone on the internet can choose
  your trace id, they can make unrelated requests share one, or reuse an id they saw in a response. Strip
  `traceparent` and `baggage` at the edge — an application gateway rewrite rule, or middleware — and start
  a new trace there. Internal callers keep theirs.

## Starting a new trace on purpose

A timer, a polling loop or a batch job is long-lived. If each run is a child of the loop, the trace never
ends and no backend shows it. `ProcessTracer.StartRootProcess` starts a span in a new trace with a link to
whatever was current:

```csharp
using var run = tracer.StartRootProcess("Publish outbox messages");
// run.Process is the span; disposing the scope restores the span that was current before.
```

A link is a pointer to another span without making it the parent. Backends show it as "related" or let
you jump to it. Use a new trace when the work is a separate unit; use a child when it is part of the
caller's work.

## Spans from the Azure SDK

The Azure SDK clients create their own spans under the `Azure.*` sources, with useful attributes such as
the queue, container or key. They also make HTTP calls through `HttpClient`, which the HttpClient
instrumentation records again: two dependencies per call. `AddObservability` keeps the SDK spans and
drops the HttpClient span when its parent comes from `Azure.Core.Http`.

## Messages

A broker does not carry headers the way HTTP does. Put `traceparent` and `tracestate` in the message's
application properties when you send it, and start a consumer span from them when you receive it.
`MessagingTraceContext` does both, and reads `Diagnostic-Id`, which older Azure SDKs write, when
`traceparent` is missing. See [outbox and messaging correlation](outbox-and-messaging-correlation.md).
