# Getting started

## Run it

Requires the .NET 10 SDK.

```shell
dotnet test Observability.slnx
dotnet run --project samples/OrderDemo --urls http://localhost:5199
```

Place an order, then one that fails:

```shell
curl -i -X POST http://localhost:5199/orders -H "Content-Type: application/json" -d '{"sku":"A1","quantity":2}'
curl -i -X POST "http://localhost:5199/orders?fail=true" -H "Content-Type: application/json" -d '{"sku":"A1","quantity":2}'
```

Without an exporter configured, the demo prints one line per span:

```text
trace=1c72f1… span=Place order kind=Internal 24.6ms status=Unset process=Place order
trace=1c72f1… span=POST /orders kind=Server 28.0ms status=Unset
trace=1c72f1… span=Reserve stock kind=Internal 0.8ms status=Unset process=Reserve stock
trace=1c72f1… span=publish OrderPlaced kind=Producer 10.7ms status=Unset
trace=1c72f1… span=process OrderPlaced kind=Consumer 7.5ms status=Unset
trace=5bb897… span=Publish outbox messages kind=Internal 28.8ms status=Unset process=Publish outbox messages
```

The `x-correlation-id` of the first response is `1c72f1…`: the request, the outbox publish and the
consumer are one trace. The outbox run is a trace of its own. The failed order returns a 500 that still
carries its `x-correlation-id`, and its process span has status `Error`.

To see the traces in a real backend:

| Backend | Set |
|---|---|
| Application Insights | `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| Any OTLP backend, for example the .NET Aspire dashboard or Jaeger | `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` |

## Add it to a service

Reference `Observability` and the packages your host needs, then:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddObservability(builder.Configuration)          // tracing, metrics, logging, conventions
    .WithAspNetCoreObservability()                    // request spans, no health probes
    .UseAzureMonitorWhenConfigured(builder.Configuration);

builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();                            // keeps x-correlation-id on a 500
app.UseObservability();                               // correlation id, gateway id, process spans

app.MapPost("/orders", PlaceOrder).WithMetadata(new ProcessAttribute("Place order"));
```

And the configuration:

```json
{
  "Observability": {
    "ServiceName": "order-service",
    "ServiceNamespace": "shop",
    "Domain": "ordering",
    "SamplingRatio": 0.25,
    "InternalHosts": [ ".internal.example" ]
  }
}
```

`ServiceName` is required: without it the host does not start. Every option:

| Option | Default | Read |
|---|---|---|
| `ServiceName`, `ServiceNamespace` | — | [tracing](tracing-and-w3c-context.md#the-resource) |
| `Domain`, `Subdomain` | — | [process logging](process-logging.md) |
| `SamplingRatio` | `1.0` | [sampling and cost](sampling-and-cost.md) |
| `BaggageTagKeys` | process keys only | [process logging](process-logging.md#baggage-becomes-columns) |
| `InternalHosts` | none | [tracing](tracing-and-w3c-context.md#baggage-stays-inside) |
| `ExcludedRequestPaths` | `/health`, `/alive` | [sampling and cost](sampling-and-cost.md) |
| `NonFailureStatusCodes` | `401`, `404` | [process logging](process-logging.md#when-a-process-fails) |
| `ExpectedDependencyResponses` | none | [metrics](metrics.md#expected-dependency-responses) |
| `GatewayTransactionIdHeader` | `x-appgw-trace-id` | [correlation ids](correlation-ids-and-headers.md#the-gateway-transaction-id) |
| `EnableHeartbeat`, `HeartbeatInterval` | `true`, 30 s | [telemetry heartbeat](telemetry-heartbeat.md) |

A list in configuration replaces its default; it is not added to it.

## Next

- An outbox or a broker: [outbox and messaging correlation](outbox-and-messaging-correlation.md).
- Azure Functions: [Azure Functions](azure-functions.md).
- Application Insights: [the exporter](azure-monitor-exporter.md), then [KQL queries](kql-queries.md).
- An existing solution: [apply it to your solution](applying-to-your-solution.md).
