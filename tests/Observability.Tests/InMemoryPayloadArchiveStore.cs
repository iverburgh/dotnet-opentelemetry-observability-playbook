using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Observability.PayloadArchive;
using OpenTelemetry;

namespace Observability.Tests;

/// <summary>
/// An <see cref="IPayloadArchiveStore"/> in memory. It enforces the append position like Azure does, can be
/// told to fail or hang per operation, and records whether instrumentation was suppressed during each call.
/// </summary>
internal sealed class InMemoryPayloadArchiveStore(int maxBlockBytes = 4 * 1024 * 1024) : IPayloadArchiveStore
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);

    public int MaxBlockBytes => maxBlockBytes;

    /// <summary>Called with "create", "append", "access" or "tags" before each operation; return an exception to fail it.</summary>
    public Func<string, Exception?> Fail { get; set; } = _ => null;

    /// <summary>Operations that never complete until they are cancelled.</summary>
    public HashSet<string> Hang { get; } = [];

    public bool AlwaysSuppressed { get; private set; } = true;

    /// <summary>Every operation attempted, in order.</summary>
    public ConcurrentQueue<string> Operations { get; } = new();

    public IReadOnlyList<StoredBlob> Blobs => [.. _blobs.Values];

    public int LineCount => _blobs.Values.Sum(blob => blob.Lines.Count);

    public void Reset()
    {
        _blobs.Clear();
        Operations.Clear();
        Hang.Clear();
        Fail = _ => null;
        AlwaysSuppressed = true;
    }

    public Task CreateAsync(string containerName, string blobName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken) =>
        RunAsync("create", cancellationToken, () => _blobs[Key(containerName, blobName)] = new StoredBlob(containerName, blobName, tags));

    public Task AppendAsync(string containerName, string blobName, long offset, ReadOnlyMemory<byte> block, CancellationToken cancellationToken) =>
        RunAsync("append", cancellationToken, () =>
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(block.Length, maxBlockBytes);
            var blob = _blobs[Key(containerName, blobName)];
            lock (blob)
            {
                if (offset != blob.Content.Length)
                {
                    throw new InvalidOperationException($"Append position {offset} does not match length {blob.Content.Length}.");
                }

                blob.Content.Write(block.Span);
                blob.BlockSizes.Add(block.Length);
            }
        });

    public Task CheckAccessAsync(string containerName, CancellationToken cancellationToken) => RunAsync("access", cancellationToken, () => { });

    public Task CheckTagQueryAsync(string containerName, CancellationToken cancellationToken) => RunAsync("tags", cancellationToken, () => { });

    private async Task RunAsync(string operation, CancellationToken cancellationToken, Action action)
    {
        AlwaysSuppressed &= Sdk.SuppressInstrumentation;
        Operations.Enqueue(operation);
        if (Hang.Contains(operation))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Fail(operation) is { } exception)
        {
            throw exception;
        }

        action();
    }

    private static string Key(string containerName, string blobName) => $"{containerName}/{blobName}";
}

internal sealed class StoredBlob(string containerName, string blobName, IReadOnlyDictionary<string, string> tags)
{
    public string ContainerName { get; } = containerName;

    public string BlobName { get; } = blobName;

    public IReadOnlyDictionary<string, string> Tags { get; } = tags;

    public MemoryStream Content { get; } = new();

    public List<int> BlockSizes { get; } = [];

    public string Text
    {
        get
        {
            lock (this)
            {
                return Encoding.UTF8.GetString(Content.ToArray());
            }
        }
    }

    /// <summary>Complete lines only, parsed; a line without its newline is not a record yet.</summary>
    public IReadOnlyList<JsonElement> Lines =>
        [.. Text.Split('\n').SkipLast(1).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
}
