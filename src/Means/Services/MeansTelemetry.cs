using System.Diagnostics;

namespace Means.Services;

public static class MeansTelemetry
{
    public const string ActivitySourceName = "Means";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
