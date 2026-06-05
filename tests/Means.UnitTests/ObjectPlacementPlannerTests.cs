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
    public void PlacementKeepsReplicasInsideOneStoragePool()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", poolId: "pool-a")),
            Node("node-b", Disk("disk-b", "node-b", poolId: "pool-a")),
            Node("node-c", Disk("disk-c", "node-c", poolId: "pool-b")),
            Node("node-d", Disk("disk-d", "node-d", poolId: "pool-b")));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");
        var request = new ObjectPlacementRequest("photos", "pool-bound.bin", VersionId: null, ReplicaCount: 2);

        var first = planner.PlanPlacement(request, topology);
        var second = planner.PlanPlacement(request, topology);

        Assert.Equal(first.Replicas, second.Replicas);
        Assert.Equal(2, first.Replicas.Count);
        Assert.Single(first.Replicas.Select(replica => replica.PoolId).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, first.Replicas.Select(replica => replica.NodeId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlacementPrefersDistinctFaultDomains()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a"), faultDomain: "rack-a"),
            Node("node-b", Disk("disk-b", "node-b"), faultDomain: "rack-a"),
            Node("node-c", Disk("disk-c", "node-c"), faultDomain: "rack-b"));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest("photos", "fault-domains.bin", null, 2), topology);
        var domains = plan.Replicas
            .Select(replica => topology.Nodes.Single(node => node.NodeId == replica.NodeId).FaultDomain)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, plan.Replicas.Count);
        Assert.Equal(2, domains.Length);
    }

    [Fact]
    public void PlacementRejectsInsufficientFaultDomainsWhenPolicyRequiresThem()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a"), faultDomain: "rack-a"),
            Node("node-b", Disk("disk-b", "node-b"), faultDomain: "rack-a"));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var ex = Assert.Throws<MeansException>(() =>
            planner.PlanPlacement(new ObjectPlacementRequest(
                "photos",
                "strict-fault-domains.bin",
                VersionId: null,
                ReplicaCount: 2,
                MinimumDistinctFaultDomains: 2), topology));

        Assert.Equal(MeansErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public void PlacementHonorsExplicitStoragePool()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", poolId: "pool-a")),
            Node("node-b", Disk("disk-b", "node-b", poolId: "pool-a")),
            Node("node-c", Disk("disk-c", "node-c", poolId: "pool-b")),
            Node("node-d", Disk("disk-d", "node-d", poolId: "pool-b")));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest(
            "photos",
            "explicit-pool.bin",
            VersionId: null,
            ReplicaCount: 2,
            PoolId: "pool-b"), topology);

        Assert.Equal(2, plan.Replicas.Count);
        Assert.All(plan.Replicas, replica => Assert.Equal("pool-b", replica.PoolId));
    }

    [Fact]
    public void PlacementDoesNotSatisfyReplicaCountByCrossingPools()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", poolId: "pool-a")),
            Node("node-b", Disk("disk-b", "node-b", poolId: "pool-b")));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var ex = Assert.Throws<MeansException>(() =>
            planner.PlanPlacement(new ObjectPlacementRequest("photos", "split-pool.bin", null, 2), topology));

        Assert.Equal(MeansErrorCodes.InvalidArgument, ex.Code);
        Assert.Equal(503, ex.StatusCode);
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
    public void PlacementRequiresConfiguredReserveBytesAfterWrite()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", availableBytes: 3_000)),
            Node("node-b", Disk("disk-b", "node-b", availableBytes: 5_000)));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest(
            "photos",
            "reserve-bytes.bin",
            VersionId: null,
            ReplicaCount: 1,
            ContentLength: 2_000,
            MinimumAvailableBytesAfterWrite: 2_000), topology);

        Assert.Single(plan.Replicas);
        Assert.Equal("node-b", plan.Replicas[0].NodeId);
    }

    [Fact]
    public void PlacementRequiresConfiguredReserveRatioAfterWrite()
    {
        var topology = CreateTopology(
            Node("node-a", Disk("disk-a", "node-a", availableBytes: 3_500)),
            Node("node-b", Disk("disk-b", "node-b", availableBytes: 5_000)));
        var planner = new DeterministicObjectPlacementPlanner("test-seed");

        var plan = planner.PlanPlacement(new ObjectPlacementRequest(
            "photos",
            "reserve-ratio.bin",
            VersionId: null,
            ReplicaCount: 1,
            ContentLength: 1_000,
            MinimumAvailableRatioAfterWrite: 0.30), topology);

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
        var pools = nodes
            .SelectMany(node => node.Disks.Select(disk => (Node: node, Disk: disk)))
            .GroupBy(item => item.Disk.PoolId, StringComparer.Ordinal)
            .Select(group => new StoragePoolInfo(
                group.Key,
                "cluster-a",
                "Pool " + group.Key,
                DateTimeOffset.UnixEpoch,
                group.Select(item => item.Node.NodeId).Distinct(StringComparer.Ordinal).Count(),
                group.Count(),
                group.Sum(item => item.Disk.TotalBytes),
                group.Sum(item => item.Disk.AvailableBytes)))
            .OrderBy(pool => pool.PoolId, StringComparer.Ordinal)
            .ToArray();
        return new ClusterTopology(
            new StorageClusterInfo("cluster-a", "Cluster A", DateTimeOffset.UnixEpoch),
            nodes,
            pools);
    }

    private static ClusterNodeInfo Node(
        string nodeId,
        StorageDiskInfo disk,
        string status = ClusterNodeStatuses.Online,
        string? faultDomain = null)
    {
        return new ClusterNodeInfo(
            nodeId,
            "cluster-a",
            nodeId,
            $"http://{nodeId}",
            status,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [disk],
            faultDomain ?? nodeId);
    }

    private static StorageDiskInfo Disk(
        string diskId,
        string nodeId,
        string status = StorageDiskStatuses.Online,
        long availableBytes = 5_000,
        string poolId = "pool-a")
    {
        return new StorageDiskInfo(
            diskId,
            nodeId,
            poolId,
            $"/data/{diskId}",
            10_000,
            availableBytes,
            status,
            DateTimeOffset.UnixEpoch);
    }
}
