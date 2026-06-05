using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Means.Core;

/// <summary>
/// Deterministic object placement planner for replica and erasure-shard writes.
/// It keeps each object inside one storage pool, prefers distinct nodes inside
/// that pool, then falls back to additional online disks when needed.
/// </summary>
public sealed class DeterministicObjectPlacementPlanner(string placementSeed = "means-v1") : IObjectPlacementPlanner
{
    public ObjectPlacementPlan PlanPlacement(ObjectPlacementRequest request, ClusterTopology topology)
    {
        ValidateRequest(request);

        var allCandidates = topology.Nodes
            .Where(node => node.Status == ClusterNodeStatuses.Online)
            .SelectMany(node => node.Disks
                .Where(disk => disk.Status == StorageDiskStatuses.Online)
                .Where(disk => HasWriteCapacity(disk, request))
                .Where(disk => request.PoolId is null || string.Equals(disk.PoolId, request.PoolId, StringComparison.Ordinal))
                .Select(disk => new PlacementCandidate(
                    node.NodeId,
                    NormalizeFaultDomain(node.FaultDomain, node.NodeId),
                    disk.DiskId,
                    disk.PoolId,
                    disk.MountPath,
                    disk.AvailableBytes)))
            .ToArray();
        var candidates = SelectPoolCandidates(request, allCandidates)
            .OrderBy(candidate => Score(request, candidate))
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DiskId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length < request.ReplicaCount)
        {
            throw new MeansException(
                MeansErrorCodes.InvalidArgument,
                $"Insufficient online capacity for {request.ReplicaCount} replicas.",
                503);
        }

        var selected = new List<PlacementCandidate>(request.ReplicaCount);
        var selectedNodes = new HashSet<string>(StringComparer.Ordinal);
        var selectedFaultDomains = new HashSet<string>(StringComparer.Ordinal);

        SelectCandidates(candidate =>
            !selectedFaultDomains.Contains(candidate.FaultDomain)
            && !selectedNodes.Contains(candidate.NodeId));

        SelectCandidates(candidate => !selectedNodes.Contains(candidate.NodeId));

        SelectCandidates(_ => true);

        if (selected
                .Select(candidate => candidate.FaultDomain)
                .Distinct(StringComparer.Ordinal)
                .Count() < request.MinimumDistinctFaultDomains)
        {
            throw new MeansException(
                MeansErrorCodes.InvalidArgument,
                $"Insufficient fault domains for {request.ReplicaCount} replicas.",
                503);
        }

        return new ObjectPlacementPlan(
            request.BucketName,
            request.ObjectKey,
            request.VersionId,
            selected.Select((candidate, index) => new ObjectPlacementReplica(
                index,
                candidate.NodeId,
                candidate.DiskId,
                candidate.PoolId,
                candidate.MountPath)).ToArray());

        void SelectCandidates(Func<PlacementCandidate, bool> predicate)
        {
            foreach (var candidate in candidates)
            {
                if (selected.Count == request.ReplicaCount)
                {
                    return;
                }

                if (selected.Contains(candidate))
                {
                    continue;
                }

                if (!predicate(candidate))
                {
                    continue;
                }

                selected.Add(candidate);
                selectedNodes.Add(candidate.NodeId);
                selectedFaultDomains.Add(candidate.FaultDomain);
            }
        }
    }

    private PlacementCandidate[] SelectPoolCandidates(
        ObjectPlacementRequest request,
        IReadOnlyList<PlacementCandidate> candidates)
    {
        if (request.PoolId is not null)
        {
            return candidates.ToArray();
        }

        var eligiblePools = candidates
            .GroupBy(candidate => candidate.PoolId, StringComparer.Ordinal)
            .Where(group => group.Count() >= request.ReplicaCount)
            .Where(group => request.MinimumDistinctFaultDomains == 0
                || group.Select(candidate => candidate.FaultDomain).Distinct(StringComparer.Ordinal).Count() >= request.MinimumDistinctFaultDomains)
            .OrderBy(group => PoolScore(request, group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .ToArray();
        return eligiblePools.Length == 0 ? [] : eligiblePools[0];
    }

    private static void ValidateRequest(ObjectPlacementRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BucketName) || string.IsNullOrWhiteSpace(request.ObjectKey))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Bucket and object key are required.", 400);
        }

        if (request.ReplicaCount is < 1 or > 16)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Replica count must be between 1 and 16.", 400);
        }

        if (request.ContentLength < 0)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Content length cannot be negative.", 400);
        }

        if (request.MinimumAvailableBytesAfterWrite < 0)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Minimum available bytes after write cannot be negative.", 400);
        }

        if (request.MinimumAvailableRatioAfterWrite is < 0 or > 1)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Minimum available ratio after write must be between 0 and 1.", 400);
        }

        if (request.MinimumDistinctFaultDomains < 0 || request.MinimumDistinctFaultDomains > request.ReplicaCount)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Minimum distinct fault domains must be between 0 and replica count.", 400);
        }
    }

    private static bool HasWriteCapacity(StorageDiskInfo disk, ObjectPlacementRequest request)
    {
        var remainingBytes = disk.AvailableBytes - request.ContentLength;
        if (remainingBytes < 0)
        {
            return false;
        }

        var minimumBytes = Math.Max(
            request.MinimumAvailableBytesAfterWrite,
            (long)Math.Ceiling(Math.Max(0, disk.TotalBytes) * request.MinimumAvailableRatioAfterWrite));
        return remainingBytes >= minimumBytes;
    }

    private static string NormalizeFaultDomain(string? faultDomain, string nodeId)
    {
        return string.IsNullOrWhiteSpace(faultDomain)
            ? nodeId
            : faultDomain.Trim();
    }

    private ulong Score(ObjectPlacementRequest request, PlacementCandidate candidate)
    {
        var input = string.Join(
            '|',
            placementSeed,
            request.BucketName,
            request.ObjectKey,
            request.VersionId ?? "",
            request.PoolId ?? "",
            candidate.FaultDomain,
            candidate.NodeId,
            candidate.DiskId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash[..8]);
    }

    private ulong PoolScore(ObjectPlacementRequest request, string poolId)
    {
        var input = string.Join(
            '|',
            placementSeed,
            request.BucketName,
            request.ObjectKey,
            request.VersionId ?? "",
            poolId,
            "pool");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash[..8]);
    }

    private sealed record PlacementCandidate(
        string NodeId,
        string FaultDomain,
        string DiskId,
        string PoolId,
        string MountPath,
        long AvailableBytes);
}
