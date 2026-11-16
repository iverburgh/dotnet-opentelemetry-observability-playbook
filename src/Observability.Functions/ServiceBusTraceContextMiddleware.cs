using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Observability.Messaging;

namespace Observability.Functions;

/// <summary>
/// Joins a Service Bus–triggered invocation to the trace of the message. The host does not always pass the
/// message's trace context on to the worker, so the invocation would start a trace of its own; this
/// middleware reads <c>traceparent</c> (or <c>Diagnostic-Id</c>) from the application properties and starts
/// a consumer span in the producer's trace, linked to the invocation span.
/// </summary>
internal sealed class ServiceBusTraceContextMiddleware : IFunctionsWorkerMiddleware
{
    public const string TriggerType = "serviceBusTrigger";

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!IsServiceBusTriggered(context)
            || !context.BindingContext.BindingData.TryGetValue(ServiceBusApplicationProperties.BindingDataKey, out var raw))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var properties = ServiceBusApplicationProperties.Parse(raw);
        using var activity = MessagingTraceContext.StartConsumerActivity(
            Telemetry.MessagingSource,
            $"process {context.FunctionDefinition.Name}",
            properties,
            [new("messaging.system", "servicebus"), new("faas.trigger", "pubsub")]);
        await next(context).ConfigureAwait(false);
    }

    internal static bool IsServiceBusTriggered(FunctionContext context) =>
        context.FunctionDefinition.InputBindings.Values.Any(b => string.Equals(b.Type, TriggerType, StringComparison.OrdinalIgnoreCase));
}
