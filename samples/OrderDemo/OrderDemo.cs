using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Observability;
using Observability.Messaging;
using Observability.Outbox;
using OpenTelemetry;

namespace OrderDemo;

internal static class OrderDemoTelemetry
{
    public const string SourceName = "OrderDemo";

    public static readonly ActivitySource Source = new(SourceName);
}

internal static partial class OrderDemoLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Placed order for {Quantity} x {Sku}")]
    public static partial void OrderPlaced(ILogger logger, string sku, int quantity);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Reserved stock for order {OrderId}")]
    public static partial void StockReserved(ILogger logger, string orderId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Handling {MessageType} {MessageId} failed")]
    public static partial void HandlingFailed(ILogger logger, Exception exception, string messageType, Guid messageId);
}

/// <summary>A broker in a channel: enough to show trace context crossing a queue.</summary>
internal sealed class InMemoryBus : IMessageSender
{
    private readonly Channel<OutgoingMessage> _channel = Channel.CreateUnbounded<OutgoingMessage>();

    public ChannelReader<OutgoingMessage> Reader => _channel.Reader;

    public Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(message, cancellationToken).AsTask();
}

/// <summary>
/// Consumes <c>OrderPlaced</c>. The consumer span continues the trace of the request that placed the order,
/// so <c>x-correlation-id</c> from that response finds these logs too.
/// </summary>
internal sealed class OrderPlacedConsumer(InMemoryBus bus, ProcessTracer tracer, ILogger<OrderPlacedConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in bus.Reader.ReadAllAsync(stoppingToken))
            {
                using var consumer = MessagingTraceContext.StartConsumerActivity(
                    OrderDemoTelemetry.Source,
                    $"process {message.MessageType}",
                    message.ApplicationProperties,
                    [new("messaging.system", "in-memory"), new("messaging.message.id", message.MessageId.ToString())]);
                using var process = tracer.StartProcess("Reserve stock");
                try
                {
                    var orderId = JsonDocument.Parse(message.Payload).RootElement.GetProperty("orderId").GetString() ?? "unknown";
                    OrderDemoLog.StockReserved(logger, orderId);
                }
                catch (Exception ex)
                {
                    ProcessTracer.Fail(ex);
                    OrderDemoLog.HandlingFailed(logger, ex, message.MessageType, message.MessageId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}

/// <summary>Prints one line per span when no exporter is configured: trace id, span, duration, status.</summary>
internal sealed class ConsoleSummaryProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        var process = data.GetTagItem(ProcessBaggage.Name) is { } name ? $" process={name}" : string.Empty;
        Console.WriteLine($"trace={data.TraceId} span={data.DisplayName} kind={data.Kind} {data.Duration.TotalMilliseconds:F1}ms status={data.Status}{process}");
    }
}
