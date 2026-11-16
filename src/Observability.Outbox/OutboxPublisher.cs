using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Observability.Messaging;

namespace Observability.Outbox;

/// <summary>
/// Publishes pending outbox messages and keeps each one connected to the request that wrote it. Each run is
/// a root process; each message gets a producer span whose parent (or link) is the stored trace context,
/// and that context travels on in the message's <c>traceparent</c> property.
/// </summary>
public sealed partial class OutboxPublisher(
    IOutboxStore store,
    IMessageSender sender,
    ProcessTracer tracer,
    OutboxMetrics metrics,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisher> logger)
{
    /// <summary>Publishes up to one batch of pending messages.</summary>
    /// <returns>The number of messages the broker accepted.</returns>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var pending = await store.GetPendingAsync(settings.BatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            // No run span for an empty poll: a trace every few seconds that says "nothing to do" is noise.
            return 0;
        }

        using var scope = tracer.StartRootProcess(OutboxTelemetry.PublishRunProcess);
        var run = scope.Process;
        run?.SetTag("outbox.batch_size", pending.Count);
        var published = 0;
        foreach (var message in pending)
        {
            if (await PublishAsync(message, run, settings.TraceMode, cancellationToken).ConfigureAwait(false))
            {
                published++;
            }
        }

        LogRunCompleted(logger, published, pending.Count);
        if (published < pending.Count)
        {
            run?.SetStatus(ActivityStatusCode.Error, $"{pending.Count - published} of {pending.Count} messages failed");
        }

        return published;
    }

    private async Task<bool> PublishAsync(OutboxMessage message, Activity? run, OutboxTraceMode mode, CancellationToken cancellationToken)
    {
        var hasOrigin = ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var origin);
        using var activity = StartPublishActivity(message, run, hasOrigin ? origin : null, mode);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["MessageId"] = message.Id,
            ["MessageType"] = message.MessageType,
        });

        var outgoing = new OutgoingMessage { MessageId = message.Id, MessageType = message.MessageType, Payload = message.Payload };
        if (activity is not null)
        {
            MessagingTraceContext.Inject(activity, outgoing.ApplicationProperties);
        }
        else if (hasOrigin)
        {
            // No span was recorded, but the consumer must still land in the request's trace.
            outgoing.ApplicationProperties[MessagingTraceContext.TraceParent] = message.TraceParent;
            if (!string.IsNullOrEmpty(message.TraceState))
            {
                outgoing.ApplicationProperties[MessagingTraceContext.TraceState] = message.TraceState;
            }
        }

        try
        {
            await sender.SendAsync(outgoing, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            await store.MarkPublishedAsync(message.Id, now, cancellationToken).ConfigureAwait(false);
            metrics.Published(message.MessageType, now - message.EnqueuedAt);
            LogPublished(logger, message.MessageType, message.Id);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            metrics.Failed(message.MessageType);
            await store.RecordFailureAsync(message.Id, cancellationToken).ConfigureAwait(false);
            LogPublishFailed(logger, ex, message.MessageType, message.Id);
            return false;
        }
    }

    internal static Activity? StartPublishActivity(OutboxMessage message, Activity? run, ActivityContext? origin, OutboxTraceMode mode)
    {
        ActivityContext parent;
        ActivityLink[]? links;
        if (mode == OutboxTraceMode.Parent && origin is { } stored)
        {
            parent = stored;
            links = run is null ? null : [new ActivityLink(run.Context)];
        }
        else
        {
            parent = run?.Context ?? default;
            links = origin is { } stored2 ? [new ActivityLink(stored2)] : null;
        }

        var activity = OutboxTelemetry.Source.StartActivity(ActivityKind.Producer, parent, tags: null, links, name: $"publish {message.MessageType}");
        activity?.SetTag("messaging.system", "outbox");
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag(OutboxTelemetry.MessageTypeTag, message.MessageType);
        return activity;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Published outbox message {MessageType} {OutboxMessageId}")]
    private static partial void LogPublished(ILogger logger, string messageType, Guid outboxMessageId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Publishing outbox message {MessageType} {OutboxMessageId} failed")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string messageType, Guid outboxMessageId);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Outbox run published {Published} of {Pending} messages")]
    private static partial void LogRunCompleted(ILogger logger, int published, int pending);
}

/// <summary>Runs <see cref="OutboxPublisher"/> on the configured polling interval.</summary>
internal sealed class OutboxPublisherService(
    OutboxPublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollingInterval, timeProvider);
        try
        {
            do
            {
                try
                {
                    await publisher.PublishPendingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Outbox run failed; retrying on the next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
