using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

internal sealed record FirstBoardTravelGoalPlan(
    RouteResult Route,
    FirstBoardExit? FirstExit);

/// <summary>
/// Finds one goal-directed next leg from player-known static topology plus current live exits.
/// </summary>
internal static class FirstBoardTravelGoalPlanner
{
    public static FirstBoardTravelGoalPlan PlanNextLeg(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor,
        PlaceId destinationPlaceId,
        PlayerSpatialKnowledgeSnapshot knowledge)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(knowledge);

        if (!world.TryGetPlace(actor.Key, out PlaceId currentPlaceId))
        {
            throw new InvalidOperationException(
                $"Travel goal actor '{actor.Key}' must be at a Place while planning a next leg.");
        }

        GraphDefinition knownGraph = knowledge.KnownGraph;
        IReadOnlyList<FirstBoardExit> liveExits =
            FirstBoardSpatialProjection.GetExits(instance, world, actor);
        PassageDefinition[] planningPassages =
        [
            .. knownGraph.Passages.Select(passage => OverlayCurrentEntry(
                passage,
                currentPlaceId,
                liveExits)),
        ];
        GraphDefinition planningGraph = GraphDefinition.Create(
            knownGraph.Places,
            planningPassages);
        GraphSpatialState planningState = GraphSpatialState.Create(planningGraph, []);
        RouteResult route = new SpatialNavigator(planningGraph).FindRoute(
            planningState,
            currentPlaceId,
            destinationPlaceId,
            BoardTiming.TravelSpeed);

        if (route is not RouteFound found)
        {
            return new FirstBoardTravelGoalPlan(route, FirstExit: null);
        }

        RouteLeg firstLeg = found.Legs[0];
        FirstBoardExit? firstExit = liveExits.SingleOrDefault(exit =>
            exit.Objective.PassageId == firstLeg.PassageId &&
            currentPlaceId == firstLeg.FromPlaceId &&
            exit.Objective.DestinationPlaceId == firstLeg.ToPlaceId);
        if (firstExit is null || !firstExit.CanTakeNow)
        {
            throw new InvalidOperationException(
                "A known TravelTo route did not map to one currently usable objective first leg.");
        }

        return new FirstBoardTravelGoalPlan(route, firstExit);
    }

    private static PassageDefinition OverlayCurrentEntry(
        PassageDefinition passage,
        PlaceId currentPlaceId,
        IReadOnlyList<FirstBoardExit> liveExits)
    {
        PassageEntryAccess access = passage.InitialEntryAccess;
        if (passage.EndpointA != currentPlaceId && passage.EndpointB != currentPlaceId)
        {
            return passage;
        }

        PlaceId expectedDestination = passage.EndpointA == currentPlaceId
            ? passage.EndpointB
            : passage.EndpointA;
        FirstBoardExit? liveExit = liveExits.SingleOrDefault(exit =>
            exit.Objective.PassageId == passage.Id &&
            exit.Objective.DestinationPlaceId == expectedDestination);
        if (liveExit is null)
        {
            throw new InvalidOperationException(
                $"Known Passage '{passage.Id}' is not an objective exit from current Place '{currentPlaceId}'.");
        }

        PassageEntryAccess overlaid = passage.EndpointA == currentPlaceId
            ? access with { EnterableFromA = liveExit.CanTakeNow }
            : access with { EnterableFromB = liveExit.CanTakeNow };
        return new PassageDefinition(
            passage.Id,
            passage.EndpointA,
            passage.EndpointB,
            passage.Length,
            overlaid);
    }
}
