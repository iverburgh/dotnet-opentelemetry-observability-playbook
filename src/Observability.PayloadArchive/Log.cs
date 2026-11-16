using Microsoft.Extensions.Logging;

namespace Observability.PayloadArchive;

/// <summary>Source-generated log methods for the payload archive, in the 3000 range.</summary>
internal static partial class Log
{
    /// <summary>
    /// The pointer from the trace to the blob. It is written while the request span is current, so the log
    /// line carries the trace id and a support engineer can jump from the trace to the raw payload.
    /// </summary>
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Archiving {Direction} {Method} payload as {Container}/{BlobName}")]
    public static partial void Archiving(ILogger logger, string direction, string method, string container, string blobName);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Archiving payload {Container}/{BlobName} failed; the request continues")]
    public static partial void ArchiveFailed(ILogger logger, Exception exception, string container, string blobName);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Reading the {Direction} payload for the archive failed; the request is not archived")]
    public static partial void ReadFailed(ILogger logger, Exception exception, string direction);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "The payload archive configuration is invalid; nothing is archived until it is fixed")]
    public static partial void InvalidOptions(ILogger logger, Exception exception);
}
