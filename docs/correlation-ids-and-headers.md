# Correlation ids and headers

A user reports a problem. Support needs one value that finds every log line, span and exception that
request caused, in every service, including the asynchronous work that followed. That value is the trace
id.

## The trace id is the correlation id

Many systems generate a separate correlation id, store it in a column, pass it in a custom header and add
it to every log scope. That is a second, hand-built trace context that drifts from the real one the first
time someone forgets to forward it.

Use the trace id instead:

- every backend already indexes it (`OperationId` in Application Insights);
- `HttpClient`, ASP.NET Core and the Azure SDK forward it without code;
- every log record written inside a span carries it.

## `x-correlation-id` on every response

`CorrelationIdMiddleware` writes the trace id of the request to the `x-correlation-id` response header,
on every response — success, client error, server error. The client shows it with an error, the support
desk searches on it.

| Rule | Why |
|---|---|
| The value is always the server's trace id | One id that also finds the telemetry. |
| An inbound `x-correlation-id` is ignored | A caller-chosen id finds nothing, or worse, someone else's request. |
| An inbound `traceparent` is honoured | An internal caller's trace continues; see [tracing](tracing-and-w3c-context.md#do-not-trust-inbound-context) for the edge. |
| Written in `OnStarting` | Set just before the headers are sent, so later middleware cannot lose it. |

Code: [CorrelationIdMiddleware.cs](../src/Observability.AspNetCore/CorrelationIdMiddleware.cs).

### Keep it on a 500

An exception that reaches the server makes Kestrel clear the response, headers included, and send a bare
500. The one response where support needs the id would not have it. Handle exceptions before they reach
the server:

```csharp
builder.Services.AddProblemDetails();
app.UseExceptionHandler();
app.UseObservability();
```

The exception handler clears the response too, but `OnStarting` callbacks survive that, so the header is
written on the error response. A test proves it:
[AspNetCoreTests.cs](../tests/Observability.Tests/AspNetCoreTests.cs).

## The gateway transaction id

An application gateway or load balancer can add its own request id, for example `x-appgw-trace-id`. Its
access log has that id; your telemetry has the trace id. `GatewayTransactionIdMiddleware` copies it to the
request span as `gateway.transaction_id`, so one query joins the two.

The value comes from outside, so it is normalised: a GUID becomes its 32-digit form, an id of up to 64
hex digits and dashes is lower-cased, anything else is dropped. The header name is
`GatewayTransactionIdHeader`.

## Asynchronous work

The id only helps if the work a request starts carries the same trace:

| Hop | How the trace id gets there |
|---|---|
| Outgoing HTTP | `traceparent`, automatically |
| Service Bus, sent directly | `traceparent` in application properties: [messaging](outbox-and-messaging-correlation.md#messages-without-an-outbox) |
| Outbox, then a broker | stored with the message and restored at publish: [outbox](outbox-and-messaging-correlation.md) |
| Azure Functions, Service Bus trigger | reparented by middleware: [Azure Functions](azure-functions.md) |
| Stored payloads | the trace id in the blob path: [raw payload archive](raw-payload-archive.md) |

An end-to-end test can then do what support does: take `x-correlation-id` from the synchronous response
and find the consumer's span with it. See [testing](testing.md#the-end-to-end-correlation-test).

## Business identifiers

The trace id finds a request. An order id, a case number or a file name finds a business object, which may
have been touched by many requests. Put those in a log scope, not in the trace context:

```csharp
using (logger.BeginScope(new Dictionary<string, object?> { ["OrderId"] = orderId }))
{
    // every log line in here has OrderId as a column
}
```

Then query on `OrderId` to find the traces, and on `OperationId` to follow each one. See
[structured logging and scopes](structured-logging-and-scopes.md).
