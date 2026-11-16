using System.Diagnostics;

namespace Observability.Outbox;

public static class OutboxTelemetry
{
    public const string SourceName = "Observability.Outbox";
    public const string MeterName = "Observability.Outbox";
    public const string MessageTypeTag = "outbox.message_type";
    public const string PublishRunProcess = "Publish outbox messages";

    internal static readonly ActivitySource Source = new(SourceName);
}
