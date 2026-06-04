namespace Means.Core;

public interface IClusterShardTransport
{
    bool Enabled { get; }

    Task<ClusterShardWriteResult> WriteShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        Stream content,
        long expectedLength,
        string expectedChecksumSha256,
        CancellationToken cancellationToken);

    Task<ClusterShardReadResult> OpenShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardStatResult> StatShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<bool> DeleteShardAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardWriteResult> WriteManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        Stream content,
        long expectedLength,
        string expectedChecksumSha256,
        CancellationToken cancellationToken);

    Task<ClusterShardReadResult> OpenManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<ClusterShardStatResult> StatManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);

    Task<bool> DeleteManifestAsync(
        ClusterNodeInfo node,
        string diskId,
        string relativePath,
        CancellationToken cancellationToken);
}
