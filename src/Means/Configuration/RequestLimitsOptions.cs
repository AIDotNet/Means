using Means.Core;

namespace Means.Configuration;

public sealed class RequestLimitsOptions
{
    public long MaxUploadSizeBytes { get; set; } = SystemSettings.DefaultMaxUploadSizeBytes;

    public int MaxConcurrentUploadRequests { get; set; } = 64;
}
