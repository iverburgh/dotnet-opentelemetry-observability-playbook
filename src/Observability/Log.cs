using Microsoft.Extensions.Logging;

namespace Observability;

/// <summary>
/// Source-generated log methods: no boxing, no parsing of the template per call, and the event id and
/// level live next to the message.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Telemetry heartbeat from {ServiceName}")]
    public static partial void Heartbeat(ILogger logger, string serviceName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Telemetry heartbeat failed")]
    public static partial void HeartbeatFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Recording metrics for process {ProcessName} failed")]
    public static partial void ProcessMetricFailed(ILogger logger, Exception exception, string processName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "Observability middleware failed; the request continues")]
    public static partial void MiddlewareFailed(ILogger logger, Exception exception);
}
