using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests.TestSupport;

internal static class GraphTestWorld
{
    internal static readonly PlaceId A = new("a");
    internal static readonly PlaceId B = new("b");
    internal static readonly PlaceId C = new("c");
    internal static readonly PlaceId D = new("d");

    internal static readonly PassageId Bridge = new("bridge");
    internal static readonly PassageId Ferry = new("ferry");

    internal static PassageDefinition Passage(
        PassageId id,
        PlaceId a,
        PlaceId b,
        long length = 10,
        bool enterableFromA = true,
        bool enterableFromB = true) =>
        new(id, a, b, length, new PassageEntryAccess(enterableFromA, enterableFromB));

    internal static GraphDefinition Definition(
        IEnumerable<PlaceId>? places = null,
        IEnumerable<PassageDefinition>? passages = null) =>
        GraphDefinition.Create(
            places ?? [A, B, C, D],
            passages ??
            [
                Passage(Bridge, A, B),
                Passage(Ferry, A, B, length: 20),
                Passage(new PassageId("bc"), B, C),
                Passage(new PassageId("cd"), C, D),
            ]);

    internal static GraphSpatialState State(
        GraphDefinition definition,
        params (string Entity, PlaceId Place)[] placements) =>
        GraphSpatialState.Create(
            definition,
            placements.Select(value => new EntityPlacement(new EntityId(value.Entity), value.Place)));

    internal static ModelTime Time(long milliseconds) => new(milliseconds);

    internal static LogicalInstant Instant(long milliseconds, long ordinal = 0) =>
        new(Time(milliseconds), ordinal);

    internal static SpatialPlanAccepted Accepted(SpatialPlanResult result) =>
        Assert.IsType<SpatialPlanAccepted>(result);

    internal static SpatialPlanRejected Rejected(SpatialPlanResult result, string reason)
    {
        SpatialPlanRejected rejected = Assert.IsType<SpatialPlanRejected>(result);
        Assert.Equal(reason, rejected.Reason);
        return rejected;
    }

    internal static GraphSpatialState Fold(
        GraphSpatialReducer reducer,
        GraphSpatialState state,
        LogicalInstant instant,
        SpatialPlanResult result)
    {
        SpatialPlanAccepted accepted = Accepted(result);
        return accepted.Facts.Aggregate(state, (current, fact) => reducer.Apply(current, instant, fact));
    }
}
