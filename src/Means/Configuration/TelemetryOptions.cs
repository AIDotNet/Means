namespace Means.Configuration;

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; }

    public string ServiceName { get; set; } = "Means";

    public string ServiceVersion { get; set; } = "";

    public string OtlpEndpoint { get; set; } = "";

    public double SampleRatio { get; set; } = 1.0;
}
