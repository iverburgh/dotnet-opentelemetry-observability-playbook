using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Observability.PayloadArchive;

/// <summary>Stores archive records as append blobs in Azure Storage, one blob per HTTP exchange.</summary>
internal sealed class BlobPayloadArchiveStore(BlobServiceClient serviceClient) : IPayloadArchiveStore
{
    /// <summary>The service limit for one Append Block call.</summary>
    public const int MaxAppendBlockBytes = 4 * 1024 * 1024;

    // JSON Lines; the content type makes the blob readable in the portal and in Storage Explorer.
    private const string ContentType = "application/x-ndjson";

    private readonly ConcurrentDictionary<string, BlobContainerClient> _containers = new(StringComparer.Ordinal);

    public int MaxBlockBytes => MaxAppendBlockBytes;

    public async Task CreateAsync(string containerName, string blobName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
    {
        var options = new AppendBlobCreateOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = ContentType },
            Tags = tags.ToDictionary(),
        };
        await Blob(containerName, blobName).CreateAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendAsync(string containerName, string blobName, long offset, ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(block.Length, MaxAppendBlockBytes);
        using var content = MemoryMarshal.TryGetArray(block, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(block.ToArray(), writable: false);

        // The append-position condition turns a retry of an append that did land into a 412, not a duplicate.
        var options = new AppendBlobAppendBlockOptions
        {
            Conditions = new AppendBlobRequestConditions { IfAppendPositionEqual = offset },
        };
        await Blob(containerName, blobName).AppendBlockAsync(content, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckAccessAsync(string containerName, CancellationToken cancellationToken) =>
        await Container(containerName).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task CheckTagQueryAsync(string containerName, CancellationToken cancellationToken)
    {
        // Query on the container, not the account: the account-level query needs a role on the whole account,
        // and a role assignment on the archive container is all the writer should have.
        var pages = Container(containerName)
            .FindBlobsByTagsAsync("\"healthcheck\"='probe'", cancellationToken)
            .AsPages(pageSizeHint: 1);
        await using var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        await enumerator.MoveNextAsync().ConfigureAwait(false);
    }

    private AppendBlobClient Blob(string containerName, string blobName) =>
        Container(containerName).GetAppendBlobClient(blobName);

    private BlobContainerClient Container(string containerName) =>
        _containers.GetOrAdd(containerName, serviceClient.GetBlobContainerClient);
}
