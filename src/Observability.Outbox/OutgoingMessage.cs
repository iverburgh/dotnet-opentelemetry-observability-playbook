namespace Observability.Outbox;

/// <summary>A message on its way to the broker.</summary>
public sealed class OutgoingMessage
{
    public required Guid MessageId { get; init; }

    public required string MessageType { get; init; }

    public required string Payload { get; init; }

    /// <summary>Broker properties; the publisher writes <c>traceparent</c> and <c>tracestate</c> here.</summary>
    public Dictionary<string, object?> ApplicationProperties { get; } = new(StringComparer.Ordinal);
}

/// <summary>Sends a message to the broker. In production: a Service Bus sender.</summary>
public interface IMessageSender
{
    Task SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>How a published message relates to the request that wrote it.</summary>
public enum OutboxTraceMode
{
    /// <summary>
    /// The publish span is a child of the stored context and links to the publisher run. The request, the
    /// publish and every consumer are one trace: one operation id from the user's click to the last handler.
    /// </summary>
    Parent,

    /// <summary>
    /// The publish span is a child of the publisher run and links to the stored context. Each run is its own
    /// trace; you follow the link to get back to the request. Choose it when one trace per run matters more
    /// than one trace per user action.
    /// </summary>
    Link,
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 100;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    public OutboxTraceMode TraceMode { get; set; } = OutboxTraceMode.Parent;
}
