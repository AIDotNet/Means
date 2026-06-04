using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Means.Core;

namespace Means.Infrastructure.XlFs;

public sealed record XlDiskFormat(
    int FormatVersion,
    string DeploymentId,
    string DiskId,
    string SetId,
    int SetIndex,
    DateTimeOffset CreatedAt);

public sealed record XlObjectManifest(
    int FormatVersion,
    string BucketName,
    string Key,
    string VersionId,
    string ObjectId,
    string ETag,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModified,
    bool IsDeleteMarker,
    XlErasureInfo Erasure,
    IReadOnlyList<XlPartManifest> Parts,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags,
    string? CacheControl,
    string? ContentDisposition);

public sealed record XlErasureInfo(
    string Algorithm,
    int DataShards,
    int ParityShards,
    int BlockSizeBytes,
    int WriteQuorum,
    int ReadQuorum);

public sealed record XlPartManifest(
    int PartNumber,
    string Name,
    long Size,
    string ETag,
    string ChecksumSha256,
    IReadOnlyList<XlShardManifest> Shards);

public sealed record XlShardManifest(
    string DiskId,
    int SetIndex,
    string RelativePath,
    long Size,
    string ChecksumSha256);

public sealed record XlObjectRecord(
    string BucketName,
    string Key,
    string VersionId,
    string ObjectId,
    string ETag,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModified,
    bool IsDeleteMarker,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags,
    string? CacheControl,
    string? ContentDisposition);

public sealed record XlBucketRecord(string Name, DateTimeOffset CreatedAt);

public sealed record XlMultipartUploadRecord(
    string BucketName,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedAt,
    IReadOnlyDictionary<string, string> Metadata,
    string? CacheControl,
    string? ContentDisposition);

public sealed record XlMultipartPartRecord(
    string BucketName,
    string Key,
    string UploadId,
    int PartNumber,
    string PartId,
    string ETag,
    long Size,
    DateTimeOffset LastModified,
    string ContentPath,
    string ChecksumSha256,
    IReadOnlyList<XlShardManifest> Shards);

public sealed record XlHealRecord(
    string BucketName,
    string Key,
    string ObjectId,
    string Reason,
    string Status,
    int AttemptCount,
    DateTimeOffset QueuedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    string? LastError);

public sealed record XlAccessKeyRecord(string AccessKey, string SecretKey, bool Enabled, DateTimeOffset CreatedAt);

public sealed record XlStoragePoolRecord(
    string PoolId,
    string ClusterId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record XlRequestMetricAggregate(
    DateTimeOffset HourUtc,
    string BucketName,
    long RequestCount,
    long ErrorCount,
    long IngressBytes,
    long EgressBytes,
    long PutCount,
    long GetCount,
    long DeleteCount,
    long HeadCount,
    long ListCount,
    DateTimeOffset LastActivityAt);

[JsonSerializable(typeof(XlDiskFormat))]
[JsonSerializable(typeof(XlObjectManifest))]
[JsonSerializable(typeof(XlObjectRecord))]
[JsonSerializable(typeof(XlBucketRecord))]
[JsonSerializable(typeof(XlMultipartUploadRecord))]
[JsonSerializable(typeof(XlMultipartPartRecord))]
[JsonSerializable(typeof(XlHealRecord))]
[JsonSerializable(typeof(XlAccessKeyRecord))]
[JsonSerializable(typeof(XlStoragePoolRecord))]
[JsonSerializable(typeof(XlRequestMetricAggregate))]
[JsonSerializable(typeof(AuditEntry))]
[JsonSerializable(typeof(BucketCorsConfiguration))]
[JsonSerializable(typeof(BucketLifecycleConfiguration))]
[JsonSerializable(typeof(BucketNotificationConfiguration))]
[JsonSerializable(typeof(BucketSettings))]
[JsonSerializable(typeof(ClusterNodeInfo))]
[JsonSerializable(typeof(ErasureCodingProfile))]
[JsonSerializable(typeof(StorageClusterInfo))]
[JsonSerializable(typeof(SystemSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class XlJsonContext : JsonSerializerContext;

internal static class XlJson
{
    public static JsonTypeInfo<T> TypeInfo<T>()
    {
        return XlJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException($"JSON metadata for {typeof(T).FullName} is not registered.");
    }
}
