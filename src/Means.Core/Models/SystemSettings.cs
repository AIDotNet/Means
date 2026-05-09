namespace Means.Core;

/// <summary>
/// Operator-managed runtime settings exposed by the built-in console.
/// </summary>
public sealed record SystemSettings(long MaxUploadSizeBytes)
{
    public const long MinimumMaxUploadSizeBytes = 1L * 1024 * 1024;
    public const long DefaultMaxUploadSizeBytes = 1L * 1024 * 1024 * 1024;
    public const long MaximumMaxUploadSizeBytes = 5L * 1024 * 1024 * 1024 * 1024;

    public static SystemSettings Default { get; } = new(DefaultMaxUploadSizeBytes);

    public static long ValidateMaxUploadSizeBytes(long value)
    {
        if (value < MinimumMaxUploadSizeBytes || value > MaximumMaxUploadSizeBytes)
        {
            throw new MeansException(
                MeansErrorCodes.InvalidArgument,
                $"Max upload size must be between {MinimumMaxUploadSizeBytes} and {MaximumMaxUploadSizeBytes} bytes.",
                400);
        }

        return value;
    }
}
