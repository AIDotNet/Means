namespace Means.Core;

/// <summary>
/// Internal node-local shard storage boundary used by the distributed data plane.
/// Implementations must stream bytes and validate relative shard paths.
/// </summary>
public interface IClusterShardStore
{
    Task<ClusterShardWriteResult> WriteShardAsync(
        string diskId,
        string relativePath,
        Stream content,
        long? expectedLength,
        string? expectedChecksumSha256,
        long maxBytes,
        CancellationToken cancellationToken);

    Task<ClusterShardReadResult> OpenShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardStatResult> StatShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<bool> DeleteShardAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardWriteResult> WriteManifestAsync(
        string diskId,
        string relativePath,
        Stream content,
        long? expectedLength,
        string? expectedChecksumSha256,
        long maxBytes,
        CancellationToken cancellationToken);

    Task<ClusterShardReadResult> OpenManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardStatResult> StatManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<bool> DeleteManifestAsync(
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);
}
