using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Observability.PayloadArchive;

/// <summary>One archive blob: the request record and the response record of one HTTP exchange.</summary>
internal sealed class PayloadArchiveEntry(string containerName, string blobName, IReadOnlyDictionary<string, string> tags)
{
    public string ContainerName { get; } = containerName;

    public string BlobName { get; } = blobName;

    public IReadOnlyDictionary<string, string> Tags { get; } = tags;

    internal bool Created { get; set; }

    internal long Length { get; set; }

    /// <summary>
    /// Set after a failed write. The blob may now end in half a line, and appending the next record would
    /// glue it to that half; one missing record is better than a corrupt one.
    /// </summary>
    internal bool Broken { get; set; }
}

/// <summary>
/// Names archive blobs and writes records to them, best effort: a storage failure costs at most
/// <see cref="PayloadArchiveOptions.WriteTimeout"/> and a warning, never the request.
/// </summary>
internal sealed class PayloadArchiveWriter(
    IPayloadArchiveStore store,
    IOptionsMonitor<PayloadArchiveOptions> optionsMonitor,
    TimeProvider timeProvider,
    ILogger<PayloadArchiveWriter> logger)
{
    /// <summary>The folder for exchanges without a trace, such as requests from an unsampled, uninstrumented host.</summary>
    internal const string NoTraceFolder = "no-trace";

    /// <summary>
    /// The current options, or <c>null</c> when a configuration change made them invalid. The options monitor
    /// throws on every read of invalid options; unguarded, one typo in a refreshed setting fails every request.
    /// </summary>
    public PayloadArchiveOptions? CurrentOptions()
    {
        try
        {
            return optionsMonitor.CurrentValue;
        }
        catch (OptionsValidationException exception)
        {
            Log.InvalidOptions(logger, exception);
            return null;
        }
    }

    /// <summary>
    /// Names the blob, tags it and logs the pointer to it. Call it while the request span is still current:
    /// that is what puts the trace id on the pointer log line.
    /// </summary>
    public PayloadArchiveEntry Begin(string containerName, string direction, string method, ActivityTraceId traceId)
    {
        var blobName = BlobName(timeProvider.GetUtcNow(), traceId, Guid.NewGuid());
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["direction"] = direction,
            ["httpMethod"] = method,
        };
        if (traceId != default)
        {
            tags["traceId"] = traceId.ToHexString();
        }

        Log.Archiving(logger, direction, method, containerName, blobName);
        return new PayloadArchiveEntry(containerName, blobName, tags);
    }

    /// <summary>Creates the blob on the first write and appends the record. Never throws.</summary>
    public async Task WriteAsync(PayloadArchiveEntry entry, PayloadRecord record)
    {
        if (entry.Broken)
        {
            return;
        }

        try
        {
            var line = record.ToJsonLine();

            // Bounded by our own timeout, never by RequestAborted: a client that hangs up is exactly the
            // exchange you may want to read back later.
            var writeTimeout = CurrentOptions()?.WriteTimeout ?? PayloadArchiveOptions.DefaultWriteTimeout;
            using var timeout = new CancellationTokenSource(writeTimeout, timeProvider);

            // The storage calls would otherwise show up as dependencies in every archived trace.
            using (SuppressInstrumentationScope.Begin())
            {
                if (!entry.Created)
                {
                    await store.CreateAsync(entry.ContainerName, entry.BlobName, entry.Tags, timeout.Token).ConfigureAwait(false);
                    entry.Created = true;
                }

                foreach (var block in Chunks(line, store.MaxBlockBytes))
                {
                    await store.AppendAsync(entry.ContainerName, entry.BlobName, entry.Length, block, timeout.Token).ConfigureAwait(false);
                    entry.Length += block.Length;
                }
            }
        }
        catch (Exception exception)
        {
            // Logged outside the suppression scope: suppressed instrumentation drops log records as well.
            entry.Broken = true;
            Log.ArchiveFailed(logger, exception, entry.ContainerName, entry.BlobName);
        }
    }

    /// <summary><c>yyyy/MM/dd/{traceId}/{guid}.jsonl</c> in UTC: browsable per day, and every exchange of one trace in one folder.</summary>
    internal static string BlobName(DateTimeOffset timestamp, ActivityTraceId traceId, Guid id)
    {
        var utc = timestamp.UtcDateTime;
        var folder = traceId == default ? NoTraceFolder : traceId.ToHexString();
        return string.Create(CultureInfo.InvariantCulture, $"{utc:yyyy}/{utc:MM}/{utc:dd}/{folder}/{id}.jsonl");
    }

    /// <summary>Splits a record into blocks the store accepts; one Append Block call takes at most 4 MiB.</summary>
    internal static IEnumerable<ReadOnlyMemory<byte>> Chunks(ReadOnlyMemory<byte> data, int maxBlockBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBlockBytes);
        for (var offset = 0; offset < data.Length; offset += maxBlockBytes)
        {
            yield return data.Slice(offset, Math.Min(maxBlockBytes, data.Length - offset));
        }
    }
}
