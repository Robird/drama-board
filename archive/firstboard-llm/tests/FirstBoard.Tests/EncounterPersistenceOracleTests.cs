using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class EncounterPersistenceOracleTests
{
    [Fact]
    public async Task TravelingS0_RealEncounterOpeningAndContinueAreDeterministicCompleteBoundaries()
    {
        EncounterPersistenceOracle.EncounterRun first = await EncounterPersistenceOracle.OpenEncounterAsync(
            PassageEncounterResolution.Continued);
        Assert.NotNull(first.History.State.Game.PendingEncounter);
        Assert.Single(first.History.CompletedEvents);

        EncounterPersistenceOracle.EncounterRun second = await EncounterPersistenceOracle.OpenEncounterAsync(
            PassageEncounterResolution.Continued);
        EncounterPersistenceOracle.AssertCommittedBoundaryEqual(first, second);

        await EncounterPersistenceOracle.CompleteResponseAsync(first);
        await EncounterPersistenceOracle.CompleteResponseAsync(second);
        EncounterPersistenceOracle.AssertCommittedBoundaryEqual(first, second);
        Assert.Null(first.History.State.Game.PendingEncounter);
        Assert.Equal(new PlaceId(BoardIds.Cellar), first.History.State.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.Equal(2, first.History.Cursor.Version.TransitionCount);
        Assert.Equal(ContactInstant(1), first.History.Cursor.LastInstant);
        Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(first.History.CompletedEvents[1].Facts)).Value);
    }

    [Fact]
    public async Task TravelingS0_ReversePreservesTheSameCompleteBoundaryOracle()
    {
        EncounterPersistenceOracle.EncounterRun first = await EncounterPersistenceOracle.OpenEncounterAsync(
            PassageEncounterResolution.Reversed);
        EncounterPersistenceOracle.EncounterRun second = await EncounterPersistenceOracle.OpenEncounterAsync(
            PassageEncounterResolution.Reversed);

        await EncounterPersistenceOracle.CompleteResponseAsync(first);
        await EncounterPersistenceOracle.CompleteResponseAsync(second);

        EncounterPersistenceOracle.AssertCommittedBoundaryEqual(first, second);
        BoardActor alice = first.History.State.Actor(BoardIds.Alice);
        Assert.Null(alice.TravelGoalPlaceId);
        Assert.Collection(
            first.History.CompletedEvents[1].Facts,
            fact => Assert.IsType<PassageEncounterResolvedEvent>(Assert.IsType<GameBoardFact>(fact).Value),
            fact => Assert.IsType<TraversalReversedFact>(Assert.IsType<SpatialBoardFact>(fact).Value));
        SpatialEntity aliceSpatial = first.History.State.Spatial.Entities.Single(
            entity => entity.Id == new EntityId(BoardIds.Alice));
        var traversal = Assert.IsType<TraversingLocation>(aliceSpatial.Location);
        Assert.Equal(new PlaceId(BoardIds.Tavern), traversal.TargetPlaceId);
        Assert.Equal(EncounterPersistenceOracle.ContactDue, traversal.AnchorTime);
        Assert.Equal(2, aliceSpatial.MovementGeneration);
    }

    private static LogicalInstant ContactInstant(int ordinal) =>
        new(EncounterPersistenceOracle.ContactDue, ordinal);
}
