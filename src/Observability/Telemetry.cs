using System.Diagnostics;

namespace Observability;

/// <summary>
/// The activity sources and meters this library emits. A tracer or meter provider only exports what it
/// subscribes to, so every name here must be added — <c>AddObservability</c> does that for you.
/// </summary>
public static class Telemetry
{
    /// <summary>Process spans started by <see cref="ProcessTracer"/>.</summary>
    public const string ProcessSourceName = "Observability.Processes";

    /// <summary>Consumer spans started by <see cref="Messaging.MessagingTraceContext"/> on behalf of a host.</summary>
    public const string MessagingSourceName = "Observability.Messaging";

    /// <summary><c>process.completed</c> and <c>process.duration</c>.</summary>
    public const string ProcessMeterName = "Observability.Processes";

    /// <summary>The explicitly subscribed half of the telemetry heartbeat.</summary>
    public const string HeartbeatMeterName = "Observability.Heartbeat";

    internal static readonly ActivitySource ProcessSource = new(ProcessSourceName);
    internal static readonly ActivitySource MessagingSource = new(MessagingSourceName);
}
