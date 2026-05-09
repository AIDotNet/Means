namespace Means.Core;

public interface IObjectPlacementPlanner
{
    ObjectPlacementPlan PlanPlacement(ObjectPlacementRequest request, ClusterTopology topology);
}
