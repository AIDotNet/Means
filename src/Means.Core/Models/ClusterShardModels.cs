namespace Means.Core;

public sealed record ClusterShardWriteResult(
    string DiskId,
    string RelativePath,
    long Length,
    string ChecksumSha256);

public sealed record ClusterShardReadResult(
    string DiskId,
    string RelativePath,
    long Length,
    Stream Content,
    string? ContentPath = null,
    IDisposable? Lease = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Content.Dispose();
        Lease?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record ClusterShardStatResult(
    string DiskId,
    string RelativePath,
    long Length,
    string ChecksumSha256);
