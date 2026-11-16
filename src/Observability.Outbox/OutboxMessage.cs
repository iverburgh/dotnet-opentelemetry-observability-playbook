using System.Collections.Concurrent;
using System.Diagnostics;

namespace Observability.Outbox;

/// <summary>
/// A message written in the same transaction as the change that caused it, published later. The trace
/// context of the request that wrote it is stored with it: the publisher runs minutes later in a loop of
/// its own, and without the stored context the message has no way back to that request.
/// </summary>
public sealed class OutboxMessage
{
    public required Guid Id { get; init; }

    public required string MessageType { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset EnqueuedAt { get; init; }

    /// <summary>The W3C <c>traceparent</c> of the span that wrote the message.</summary>
    public string? TraceParent { get; init; }

    /// <summary>The W3C <c>tracestate</c> of the span that wrote the message.</summary>
    public string? TraceState { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int FailedAttempts { get; set; }
}

/// <summary>Where outbox messages live. In production: a table in the same database as the aggregates.</summary>
public interface IOutboxStore
{
    Task AddAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid id, DateTimeOffset publishedAt, CancellationToken cancellationToken);

    Task RecordFailureAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>An in-memory store for samples and tests.</summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();

    public IReadOnlyCollection<OutboxMessage> All => [.. _messages.Values.OrderBy(m => m.EnqueuedAt)];

    public Task AddAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages[message.Id] = message;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int maxCount, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>([.. _messages.Values.Where(m => m.PublishedAt is null).OrderBy(m => m.EnqueuedAt).Take(maxCount)]);

    public Task MarkPublishedAsync(Guid id, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(id, out var message))
        {
            message.PublishedAt = publishedAt;
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_messages.TryGetValue(id, out var message))
        {
            message.FailedAttempts++;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Writes outbox messages and stores the current trace context with each one.</summary>
public sealed class OutboxWriter(IOutboxStore store, TimeProvider timeProvider)
{
    public async Task<OutboxMessage> EnqueueAsync(string messageType, string payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payload);
        var current = Activity.Current;
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = messageType,
            Payload = payload,
            EnqueuedAt = timeProvider.GetUtcNow(),
            TraceParent = current?.IdFormat == ActivityIdFormat.W3C ? current.Id : null,
            TraceState = current?.TraceStateString,
        };
        await store.AddAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }
}
