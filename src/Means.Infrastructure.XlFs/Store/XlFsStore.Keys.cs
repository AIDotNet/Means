namespace Means.Infrastructure.XlFs;

public sealed partial class XlFsStore
{
    private static class Keys
    {
        public const string BucketPrefix = "b:";
        public const string CurrentObjectGlobalPrefix = "o:";
        public const string AccessKeyPrefix = "ak:";
        public const string AuditPrefix = "audit:";
        public const string MultipartUploadGlobalPrefix = "mpu:";
        public const string MetricPrefix = "metric:";
        public const string HealPrefix = "heal:";
        public const string EcProfilePrefix = "ecp:";
        public const string ClusterNodePrefix = "cluster:node:";
        public const string ClusterPoolGlobalPrefix = "cluster:pool:";
        public const string SystemSettings = "sys:settings";
        public const string ClusterInfo = "cluster:info";

        public static string Bucket(string bucket) => "b:" + Escape(bucket);

        public static string CurrentObjectPrefix(string bucket) => "o:" + Escape(bucket) + ":";

        public static string CurrentObject(string bucket, string key) => CurrentObjectPrefix(bucket) + Escape(key);

        public static string VersionPrefix(string bucket) => "v:" + Escape(bucket) + ":";

        public static string Version(string bucket, string key, string versionId)
        {
            return VersionPrefix(bucket) + Escape(key) + ":" + Escape(versionId);
        }

        public static string BucketVersioning(string bucket) => "bv:" + Escape(bucket);

        public static string MultipartUploadPrefix(string bucket) => "mpu:" + Escape(bucket) + ":";

        public static string MultipartUpload(string bucket, string key, string uploadId)
        {
            return MultipartUploadPrefix(bucket) + Escape(key) + ":" + Escape(uploadId);
        }

        public static string MultipartPartPrefix(string bucket, string key, string uploadId)
        {
            return "mpp:" + Escape(bucket) + ":" + Escape(key) + ":" + Escape(uploadId) + ":";
        }

        public static string MultipartPart(string bucket, string key, string uploadId, int partNumber)
        {
            return MultipartPartPrefix(bucket, key, uploadId) + partNumber.ToString("D5");
        }

        public static string Policy(string bucket) => "policy:" + Escape(bucket);

        public static string Lifecycle(string bucket) => "lc:" + Escape(bucket);

        public static string Cors(string bucket) => "cors:" + Escape(bucket);

        public static string Notification(string bucket) => "notif:" + Escape(bucket);

        public static string BucketSettings(string bucket) => "bs:" + Escape(bucket);

        public static string AccessKey(string accessKey) => "ak:" + Escape(accessKey);

        public static string Audit(long id) => "audit:" + id.ToString("D20");

        public static string Heal(string objectId) => "heal:" + Escape(objectId);

        public static string Metric(DateTimeOffset hourUtc, string bucket)
        {
            return MetricPrefix + hourUtc.UtcTicks.ToString("D20") + ":" + Escape(bucket);
        }

        public static string MetricPrefixForRange(DateTimeOffset hourUtc)
        {
            return MetricPrefix + hourUtc.UtcTicks.ToString("D20") + ":";
        }

        public static string EcProfile(string profileId) => EcProfilePrefix + Escape(profileId);

        public static string ClusterNode(string nodeId) => ClusterNodePrefix + Escape(nodeId);

        public static string ClusterPoolPrefix(string clusterId) => ClusterPoolGlobalPrefix + Escape(clusterId) + ":";

        public static string ClusterPool(string clusterId, string poolId) => ClusterPoolPrefix(clusterId) + Escape(poolId);
    }
}
