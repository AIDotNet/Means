using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Means.Core;

/// <summary>
/// Deterministic object placement planner for future replica writes.
/// It prefers distinct nodes first, then falls back to additional online disks if
/// the cluster does not have enough nodes to satisfy the requested replica count.
/// </summary>
public sealed class DeterministicObjectPlacementPlanner(string placementSeed = "means-v1") : IObjectPlacementPlanner
{
    public ObjectPlacementPlan PlanPlacement(ObjectPlacementRequest request, ClusterTopology topology)
    {
        ValidateRequest(request);

        var candidates = topology.Nodes
            .Where(node => node.Status == ClusterNodeStatuses.Online)
            .SelectMany(node => node.Disks
                .Where(disk => disk.Status == StorageDiskStatuses.Online)
                .Where(disk => disk.AvailableBytes >= request.ContentLength)
                .Where(disk => request.PoolId is null || string.Equals(disk.PoolId, request.PoolId, StringComparison.Ordinal))
                .Select(disk => new PlacementCandidate(node.NodeId, disk.DiskId, disk.PoolId, disk.MountPath, disk.AvailableBytes)))
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
        foreach (var candidate in candidates)
        {
            if (selectedNodes.Add(candidate.NodeId))
            {
                selected.Add(candidate);
                if (selected.Count == request.ReplicaCount)
                {
                    break;
                }
            }
        }

        if (selected.Count < request.ReplicaCount)
        {
            foreach (var candidate in candidates)
            {
                if (selected.Contains(candidate))
                {
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count == request.ReplicaCount)
                {
                    break;
                }
            }
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
            candidate.NodeId,
            candidate.DiskId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash[..8]);
    }

    private sealed record PlacementCandidate(string NodeId, string DiskId, string PoolId, string MountPath, long AvailableBytes);
}
