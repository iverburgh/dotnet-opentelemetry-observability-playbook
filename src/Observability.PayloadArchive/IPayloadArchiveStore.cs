namespace Observability.PayloadArchive;

/// <summary>
/// The few append-blob operations the archive needs. <see cref="BlobPayloadArchiveStore"/> talks to Azure
/// Storage; tests use an in-memory store, and you could add one that writes files on a developer machine.
/// </summary>
public interface IPayloadArchiveStore
{
    /// <summary>The largest block <see cref="AppendAsync"/> accepts. Azure append blobs take at most 4 MiB per call.</summary>
    int MaxBlockBytes { get; }

    /// <summary>Creates an empty append blob with these index tags.</summary>
    Task CreateAsync(string containerName, string blobName, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one block at <paramref name="offset"/>, the current length of the blob. A store must reject a
    /// block whose offset does not match, so a retried append that already landed cannot duplicate data.
    /// </summary>
    Task AppendAsync(string containerName, string blobName, long offset, ReadOnlyMemory<byte> block, CancellationToken cancellationToken);

    /// <summary>Throws unless the container exists and this identity can reach it.</summary>
    Task CheckAccessAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>Throws unless this identity may find blobs by their index tags in the container.</summary>
    Task CheckTagQueryAsync(string containerName, CancellationToken cancellationToken);
}
