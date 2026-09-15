using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

internal sealed record FirstBoardExit(
    string ExitId,
    PassageExit Objective,
    string? RequiredTicketObjectId,
    bool CanTakeNow);

internal static class FirstBoardSpatialProjection
{
    public static IReadOnlyList<FirstBoardExit> GetExits(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor)
    {
        if (!world.TryGetPlace(actor.Key, out PlaceId placeId))
        {
            return [];
        }

        var queries = new SpatialQueries(instance.Graph);
        return Array.AsReadOnly(
            queries.GetExits(world.Spatial, placeId, BoardTiming.TravelSpeed)
                .Select(exit =>
                {
                    string? ticket = instance.Definition.RequiredTicket(exit.PassageId);
                    bool ownsTicket = ticket is null ||
                        world.Objects.Any(item =>
                            item.Key == ticket && item.OwnerActorId == actor.Id);
                    return new FirstBoardExit(
                        ExitId(exit.PassageId),
                        exit,
                        ticket,
                        exit.EffectiveEntryAllowed && ownsTicket);
                })
                .OrderBy(exit => exit.ExitId, StringComparer.Ordinal)
                .ToArray());
    }

    public static IReadOnlyList<ObservedExit> ObserveExits(
        ScenarioInstance instance,
        FirstBoardWorld world,
        BoardActor actor) =>
        Array.AsReadOnly(
            GetExits(instance, world, actor)
                .Select(exit => new ObservedExit(
                    exit.ExitId,
                    exit.Objective.DestinationPlaceId.Value,
                    exit.Objective.ExpectedDuration.Ticks,
                    exit.CanTakeNow))
                .ToArray());

    public static string ExitId(PassageId passageId) =>
        $"exit:{passageId.Value}";
}
