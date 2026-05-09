using Means.Core;

namespace Means.UnitTests;

public sealed class ObjectPlacementPlannerTests
{
    [Fact]
    public void PlacementIsDeterministicAndPrefersDistinctNodes()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a")),
            Node("node-b", Disk("disk-b", "node-b")),
            Node("node-c", Disk("disk-c", "node-c")));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");
        var request = new ObjectPlacementRequest("photos", "2026/cat.jpg", VersionId: null, ReplicaCount: 2);

        var first = planner.PlanPlacement(request, topology);
        var second = planner.PlanPlacement(request, topology);

        Assert.Equal(first.BucketName, second.BucketName);
        Assert.Equal(first.ObjectKey, second.ObjectKey);
        Assert.Equal(first.VersionId, second.VersionId);
        Assert.Equal(first.Replicas, second.Replicas);
        Assert.Equal(2, first.Replicas.Count);
        Assert.Equal(2, first.Replicas.Select(replica => replica.NodeId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlacementFiltersOfflineNodesAndDisks()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", StorageDiskStatuses.Offline)),
            Node("node-b", Disk("disk-b", "node-b")),
            Node("node-c", Disk("disk-c", "node-c"), ClusterNodeStatuses.Offline));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest("photos", "active.bin", null, 1), topology);

        Assert.Single(plan.Replicas);
        Assert.Equal("node-b", plan.Replicas[0].NodeId);
        Assert.Equal("disk-b", plan.Replicas[0].DiskId);
    }

    [Fact]
    public void PlacementRequiresEnoughDiskCapacity()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", availableBytes: 1_000)),
            Node("node-b", Disk("disk-b", "node-b", availableBytes: 5_000)));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest("photos", "capacity.bin", null, 1, ContentLength: 2_000), topology);

        Assert.Single(plan.Replicas);
        Assert.Equal("node-b", plan.Replicas[0].NodeId);
    }

    [Fact]
    public void PlacementRejectsInsufficientOnlineDisks()
    {
        var topology = CreateTopology(Node("node-a", Disk("disk-a", "node-a")));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var ex = Assert.Throws<MeansException>(() =>
            planner.PlanPlacement(new ObjectPlacementRequest("photos", "too-many.bin", null, 2), topology));

        Assert.Equal(MeansErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(503, ex.StatusCode);
    }

    private static ClusterTopology CreateTopology(params ClusterNodeInfo[] nodes)
    {
        return new ClusterTopology(
            new StorageClusterInfo("cluster-a", "Cluster A", DateTimeOffset.UnixEpoch),
            nodes,
            [
                new StoragePoolInfo(
                    "pool-a",
                    "cluster-a",
                    "Pool A",
                    DateTimeOffset.UnixEpoch,
                    nodes.Length,
                    nodes.Sum(node => node.Disks.Count),
                    30_000,
                    20_000)
            ]);
    }

    private static ClusterNodeInfo Node(string nodeId, StorageDiskInfo disk, string status = ClusterNodeStatuses.Online)
    {
        return new ClusterNodeInfo(
            nodeId,
            "cluster-a",
            nodeId,
            $"http://{nodeId}",
            status,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [disk]);
    }

    private static StorageDiskInfo Disk(
        string diskId,
        string nodeId,
        string status = StorageDiskStatuses.Online,
        long availableBytes = 5_000)
    {
        return new StorageDiskInfo(
            diskId,
            nodeId,
            "pool-a",
            $"/data/{diskId}",
            10_000,
            availableBytes,
            status,
            DateTimeOffset.UnixEpoch);
    }
}
