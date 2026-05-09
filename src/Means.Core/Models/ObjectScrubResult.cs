namespace Means.Core;

public sealed record ObjectScrubResult(
    int CheckedReplicas,
    int MissingReplicas,
    int CorruptReplicas,
    int QueuedRepairs);
