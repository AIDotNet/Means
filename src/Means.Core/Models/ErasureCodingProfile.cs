namespace Means.Core;

public sealed record ErasureCodingProfile(
    string ProfileId,
    int DataShards,
    int ParityShards,
    int CellSizeBytes,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public int TotalShards => DataShards + ParityShards;
}
