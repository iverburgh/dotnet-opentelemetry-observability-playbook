using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Observability.Metrics;

namespace Observability.Outbox;

/// <summary>
/// Published and failed counts and the age of a message at publish time. Age is the one to alert on: a
/// publisher that stopped produces no errors, only messages that get older.
/// </summary>
[MeterName(OutboxTelemetry.MeterName)]
public sealed class OutboxMetrics : MetricsBundle
{
    private readonly Counter<long> _published;
    private readonly Counter<long> _failed;
    private readonly Histogram<double> _age;

    public OutboxMetrics(IMeterFactory meterFactory, IOptions<ObservabilityOptions> options)
        : base(meterFactory, options)
    {
        _published = Meter.CreateCounter<long>("outbox.messages.published", "{message}", "Outbox messages sent to the broker.");
        _failed = Meter.CreateCounter<long>("outbox.messages.failed", "{message}", "Outbox messages the broker did not accept.");
        _age = Meter.CreateHistogram<double>("outbox.message.age", "s", "Time from writing a message to publishing it.");
    }

    public void Published(string messageType, TimeSpan age)
    {
        var tag = new KeyValuePair<string, object?>(OutboxTelemetry.MessageTypeTag, messageType);
        _published.Add(1, tag);
        _age.Record(Math.Max(0, age.TotalSeconds), tag);
    }

    public void Failed(string messageType) =>
        _failed.Add(1, new KeyValuePair<string, object?>(OutboxTelemetry.MessageTypeTag, messageType));
}
