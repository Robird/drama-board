using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests;

public sealed class SpatialOccurrenceRuleTests
{
    private static readonly ModelTime Due = SpatialEventTestHarness.AtSecond(1);
    private static readonly SimulationRules Rules = new(worldSeed: 0xC0FFEE, maxTransitionsPerModelTime: 16);

    [Fact]
    public void Forecast_EnumeratesEveryMutationAndCurrentLeg_WithStableUniqueKeys()
    {
        SpatialDefinition definition = Definition();
        SpatialState state = TwoArrivals(definition);
        state = ScheduleMutation(definition, state, new SetCellOverrideMutation(
            Cell(0, 0), new CellOverride(blocksSight: true)));
        var rule = new SpatialOccurrenceRule(definition);

        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> candidates = rule.Forecast(state, Rules);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(3, candidates.Select(candidate => candidate.Key).Distinct().Count());
        Assert.All(candidates, candidate => Assert.Equal(Due, candidate.Due.ModelTime));
        Assert.Equal(2, candidates.Count(candidate => candidate.Data is SpatialArrivalOccurrenceData));
        Assert.Single(candidates, candidate => candidate.Data is SpatialMutationOccurrenceData);

        OccurrenceCandidate<SpatialOccurrenceData> expected = OccurrenceScheduler.SelectWinner(candidates, Rules.WorldSeed);
        Assert.Equal(expected.Key, OccurrenceScheduler.SelectWinner(candidates.Reverse(), Rules.WorldSeed).Key);
        Assert.All(candidates, candidate => Assert.NotEmpty(candidate.Key.ToByteArray()));
    }

    [Fact]
    public async Task SameTickArrivals_CommitAsTwoTransitions_AndFirstDoesNotConsumePeer()
    {
        SpatialDefinition definition = Definition();
        SpatialState genesis = TwoArrivals(definition);
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 91);
        SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> kernel =
            CreateKernel(definition, genesis, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));

        Assert.Single(journal.Batches);
        Assert.Single(kernel.World.Journeys);
        Assert.Single(new SpatialOccurrenceRule(definition).Forecast(kernel.World, Rules));
        Assert.Equal(new LogicalInstant(Due, 0), journal.Batches[0].Instant);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));

        Assert.Equal(2, journal.Batches.Count);
        Assert.Empty(kernel.World.Journeys);
        Assert.Equal(new LogicalInstant(Due, 1), journal.Batches[1].Instant);
        Assert.All(journal.Batches, batch => Assert.Contains(batch.Facts, fact => fact is JourneyCompletedEvent));
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Due));
    }

    [Fact]
    public async Task MutationAndArrivalAtSameTick_AreArbitratedByKernel_ThenReforecasted()
    {
        SpatialDefinition definition = Definition();
        SpatialState genesis = OneArrival(definition);
        genesis = ScheduleMutation(definition, genesis, new SetCellOverrideMutation(
            Cell(0, 1), new CellOverride(blocksSight: true)));
        var rule = new SpatialOccurrenceRule(definition);
        IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> forecast = rule.Forecast(genesis, Rules);
        OccurrenceCandidate<SpatialOccurrenceData> expected = OccurrenceScheduler.SelectWinner(forecast, Rules.WorldSeed);
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 91);
        SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> kernel =
            CreateKernel(definition, genesis, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));

        JournalBatch<SpatialEvent> first = Assert.Single(journal.Batches);
        Assert.Equal(expected.Key, first.CauseKey);
        OccurrenceCandidate<SpatialOccurrenceData> survivor = Assert.Single(rule.Forecast(kernel.World, Rules));
        Assert.NotEqual(expected.Key, survivor.Key);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));
        Assert.Empty(rule.Forecast(kernel.World, Rules));
        Assert.Equal(2, journal.Batches.Count);
    }

    [Fact]
    public async Task IdempotentMutation_StillConsumesItsOwnSchedule()
    {
        SpatialDefinition definition = Definition();
        SpatialState genesis = ScheduleMutation(
            definition,
            SpatialState.Create(definition),
            new SetCellOverrideMutation(Cell(0, 0), Value: null));
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 91);
        SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> kernel =
            CreateKernel(definition, genesis, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));

        JournalBatch<SpatialEvent> batch = Assert.Single(journal.Batches);
        Assert.Single(batch.Facts);
        Assert.IsType<MutationConsumedEvent>(batch.Facts[0]);
        Assert.Empty(kernel.World.ScheduledMutations);
    }

    [Fact]
    public async Task ImpassableFrozenLeg_IsBlockedAndCannotRecur()
    {
        SpatialDefinition definition = TestSpatialDefinitionBuilder.CreateDefault();
        CellRef from = TestSpatialDefinitionBuilder.Cell("world", 1, 0);
        CellRef to = TestSpatialDefinitionBuilder.Cell("town", 0, 0);
        SpatialState genesis = SpatialEventTestHarness.Place(
            definition, SpatialState.Create(definition), entityId: 1, from);
        var due = SpatialEventTestHarness.AtSecond(3);
        var leg = new CurrentLeg(
            from,
            to,
            SpatialEdgeKind.Portal,
            new PortalId("world-to-town"),
            ModelTime.Zero,
            due,
            journeyGeneration: 1);
        genesis = SpatialEventTestHarness.Apply(definition, genesis, new JourneyStartedEvent(
            new JourneyState(new JourneyId(1), new EntityId(1), new CellGoal(to), 1, leg)));
        genesis = SpatialEventTestHarness.Apply(
            definition,
            genesis,
            new PortalStateChangedEvent(
                new PortalId("world-to-town"),
                expectedOverride: null,
                resultingOverride: false));
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 91);
        SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> kernel =
            CreateKernel(definition, genesis, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(due));

        JournalBatch<SpatialEvent> batch = Assert.Single(journal.Batches);
        Assert.Single(batch.Facts.OfType<JourneyBlockedEvent>());
        Assert.Empty(kernel.World.Journeys);
        Assert.Equal(from, Assert.Single(kernel.World.Entities).Cell);
        Assert.Empty(new SpatialOccurrenceRule(definition).Forecast(kernel.World, Rules));
    }

    [Fact]
    public async Task TransitionFactsShareInstant_AndReplayRebuildsCommittedWorld()
    {
        SpatialDefinition definition = Definition();
        SpatialState genesis = OneArrival(definition);
        var journal = new InMemoryJournal<SpatialEvent>(lineageId: 91);
        SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> kernel =
            CreateKernel(definition, genesis, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Due));
        JournalBatch<SpatialEvent> batch = Assert.Single(journal.Batches);
        Assert.True(batch.Facts.Count >= 2);

        var reducer = new SpatialReducer(definition);
        ReplayResult<SpatialState> replay = SimulationReplay.Replay(
            genesis,
            lineageId: 91,
            ModelTime.Zero,
            journal.Batches,
            reducer.Apply,
            state => SpatialStateValidator.ValidateComplete(definition, state));

        Assert.Equal(kernel.World, replay.World);
        Assert.Equal(new WorldVersion(91, 1), replay.Version);
        Assert.Equal(batch.Instant, replay.LastCommittedInstant);
    }

    [Fact]
    public async Task StaleCandidate_IsRejectedAfterAnotherOccurrenceChangesRevision()
    {
        SpatialDefinition definition = Definition();
        SpatialState state = TwoArrivals(definition);
        var rule = new SpatialOccurrenceRule(definition);
        OccurrenceCandidate<SpatialOccurrenceData>[] forecast = [.. rule.Forecast(state, Rules)];
        OccurrenceCandidate<SpatialOccurrenceData> winner = OccurrenceScheduler.SelectWinner(forecast, Rules.WorldSeed);
        OccurrenceCandidate<SpatialOccurrenceData> stale = forecast.Single(candidate => candidate.Key != winner.Key);
        TransitionDraft<SpatialEvent> winnerDraft = await rule.PlanSelectedAsync(state, winner, CancellationToken.None);
        var reducer = new SpatialReducer(definition);
        SpatialState changed = state;
        var instant = new LogicalInstant(Due, 0);
        foreach (SpatialEvent fact in winnerDraft.Facts)
        {
            changed = reducer.Apply(changed, instant, fact);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rule.PlanSelectedAsync(changed, stale, CancellationToken.None));
    }

    private static SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent> CreateKernel(
        SpatialDefinition definition,
        SpatialState genesis,
        InMemoryJournal<SpatialEvent> journal)
    {
        var reducer = new SpatialReducer(definition);
        return new SimulationKernel<SpatialState, SpatialOccurrenceData, SpatialEvent>(
            genesis,
            new WorldVersion(lineageId: 91, transitionCount: 0),
            ModelTime.Zero,
            lastCommittedInstant: null,
            Rules,
            [new SpatialOccurrenceRule(definition)],
            journal,
            reducer.Apply,
            state => SpatialStateValidator.ValidateComplete(definition, state));
    }

    private static SpatialState TwoArrivals(SpatialDefinition definition)
    {
        SpatialState state = OneArrival(definition);
        state = SpatialEventTestHarness.Place(definition, state, entityId: 2, Cell(0, 1));
        var leg = new CurrentLeg(Cell(0, 1), Cell(1, 1), SpatialEdgeKind.Orthogonal, null,
            ModelTime.Zero, Due, journeyGeneration: 1);
        return SpatialEventTestHarness.Apply(definition, state, new JourneyStartedEvent(
            new JourneyState(new JourneyId(2), new EntityId(2), new CellGoal(leg.To), 1, leg)));
    }

    private static SpatialState OneArrival(SpatialDefinition definition)
    {
        SpatialState state = SpatialEventTestHarness.Place(
            definition, SpatialState.Create(definition), entityId: 1, Cell(0, 0));
        var leg = new CurrentLeg(Cell(0, 0), Cell(1, 0), SpatialEdgeKind.Orthogonal, null,
            ModelTime.Zero, Due, journeyGeneration: 1);
        return SpatialEventTestHarness.Apply(definition, state, new JourneyStartedEvent(
            new JourneyState(new JourneyId(1), new EntityId(1), new CellGoal(leg.To), 1, leg)));
    }

    private static SpatialState ScheduleMutation(
        SpatialDefinition definition,
        SpatialState state,
        ScheduledSpatialMutation mutation) =>
        SpatialEventTestHarness.Apply(definition, state, new MutationScheduledEvent(
            new ScheduledSpatialMutationState(
                new ScheduledMutationId(state.NextMutationOrdinal), Due, mutation)));

    private static SpatialDefinition Definition() => TestSpatialDefinitionBuilder.Create(
        maps: [TestSpatialDefinitionBuilder.Map("map", width: 2, height: 2)]);

    private static CellRef Cell(int x, int y) => TestSpatialDefinitionBuilder.Cell("map", x, y);
}
