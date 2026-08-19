using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class LayeredWorldAcceptanceTests
{
    private const long SpatialSourceId = 7001;

    [Fact]
    public void SemanticMoveAcrossLayeredWorld_IsDeterministicReplayableAndForkable()
    {
        LayeredFixture fixture = CreateFixture();
        SpatialState initialState = SpatialState.Create(fixture.Definition);
        SpatialCommand[] playerIntents =
        [
            new AssignMoveGoalCommand(
                new SpatialCommandId("move.mover.to-sanctum"),
                fixture.MoverId,
                new AnchorGoal(fixture.SanctumAnchorId)),
            new PlaceEntityCommand(
                new SpatialCommandId("place.companion"),
                fixture.CompanionId,
                fixture.Start,
                observationEnabled: true),
            new PlaceEntityCommand(
                new SpatialCommandId("place.mover"),
                fixture.MoverId,
                fixture.Start,
                observationEnabled: true),
        ];
        var handler = new SpatialCommandHandler(fixture.Definition);

        SpatialCommandBatchResult externalBatch = handler.HandleBatch(
            initialState,
            playerIntents,
            ModelTime.Zero);

        Assert.All(externalBatch.Results, result =>
            Assert.Equal(SpatialCommandDisposition.Accepted, result.Disposition));
        Assert.Equal(
            new JourneyId(1),
            externalBatch.Results.Single(result =>
                result.CommandId == new SpatialCommandId("move.mover.to-sanctum")).JourneyId);
        JourneyStartedEvent initialJourney = Assert.Single(
            externalBatch.Events.Select(value => value.Payload).OfType<JourneyStartedEvent>());
        Assert.IsType<AnchorGoal>(initialJourney.Journey.Goal);
        Assert.Equal(fixture.Start, initialJourney.Journey.CurrentLeg.From);
        Assert.Equal(fixture.WorldMiddle, initialJourney.Journey.CurrentLeg.To);
        Assert.DoesNotContain(externalBatch.Events, value => value.Payload is JourneyContinuedEvent);
        Assert.Contains(externalBatch.Events, value =>
            value.Payload is CoPresenceStartedEvent started &&
            started.FirstEntityId == fixture.MoverId &&
            started.SecondEntityId == fixture.CompanionId);

        var subsystem = new SpatialSubsystem(fixture.Definition, SpatialSourceId);
        var reducer = new SpatialReducer(fixture.Definition);
        var loop = new SimulationLoop<SpatialState, SpatialMomentCandidate, SpatialEvent>(
            [subsystem],
            reducer);
        ModelTime finalBoundary = new(500);

        var oneShotJournal = new InMemoryJournal<SpatialEvent>();
        SimulationRunResult<SpatialState, SpatialEvent> oneShot = loop.Run(
            initialState,
            SimulationCursor.CreateInitial(lineageId: 71, ModelTime.Zero),
            finalBoundary,
            oneShotJournal,
            externalBatch.Events);

        Assert.Equal(StopReason.Exhausted, oneShot.StopReason);
        Assert.Equal(fixture.Sanctum, Entity(oneShot.World, fixture.MoverId).Cell);
        Assert.Equal(fixture.Start, Entity(oneShot.World, fixture.CompanionId).Cell);
        Assert.Empty(oneShot.World.Journeys);
        SpatialStateValidator.ValidateComplete(fixture.Definition, oneShot.World);

        CurrentLeg[] committedLegs =
        [
            initialJourney.Journey.CurrentLeg,
            .. oneShotJournal.Events
                .Select(value => value.Payload)
                .OfType<JourneyContinuedEvent>()
                .Select(value => value.ResultingLeg),
        ];
        Assert.Equal(8, committedLegs.Length);
        Assert.Equal(
            [
                SpatialEdgeKind.Orthogonal,
                SpatialEdgeKind.Orthogonal,
                SpatialEdgeKind.Portal,
                SpatialEdgeKind.Orthogonal,
                SpatialEdgeKind.Orthogonal,
                SpatialEdgeKind.Portal,
                SpatialEdgeKind.Orthogonal,
                SpatialEdgeKind.Orthogonal,
            ],
            committedLegs.Select(leg => leg.EdgeKind));
        Assert.Equal(
            [fixture.WorldToRegionPortalId, fixture.RegionToInteriorPortalId],
            committedLegs.Where(leg => leg.EdgeKind == SpatialEdgeKind.Portal)
                .Select(leg => leg.PortalId!.Value));
        Assert.Equal(
            ["world", "region", "interior"],
            committedLegs.SelectMany(leg => new[] { leg.From.MapId.Value, leg.To.MapId.Value })
                .Distinct());
        Assert.All(
            committedLegs.Zip(committedLegs.Skip(1)),
            pair => Assert.Equal(pair.First.To, pair.Second.From));

        SpatialEvent[] history = [.. oneShotJournal.Events.Select(value => value.Payload)];
        Assert.Contains(history, payload =>
            payload is ZoneLeftEvent left &&
            left.EntityId == fixture.MoverId &&
            left.ZoneId == fixture.OriginZoneId);
        Assert.Contains(history, payload =>
            payload is ZoneEnteredEvent entered &&
            entered.EntityId == fixture.MoverId &&
            entered.ZoneId == fixture.SanctumZoneId);
        Assert.Contains(history, payload =>
            payload is CoPresenceEndedEvent ended &&
            ended.FirstEntityId == fixture.MoverId &&
            ended.SecondEntityId == fixture.CompanionId);
        AssertVisibilityLifecycle(history, fixture.MoverId, fixture.CompanionId);
        AssertVisibilityLifecycle(history, fixture.CompanionId, fixture.MoverId);
        Assert.DoesNotContain(history, payload => payload is JourneyBlockedEvent or JourneyReroutedEvent);

        var queries = new SpatialQueries(fixture.Definition);
        Assert.Empty(queries.GetVisibleEntities(oneShot.World, fixture.MoverId));
        Assert.Empty(queries.GetVisibleEntities(oneShot.World, fixture.CompanionId));

        var splitJournal = new InMemoryJournal<SpatialEvent>();
        SimulationRunResult<SpatialState, SpatialEvent> firstSegment = loop.Run(
            initialState,
            SimulationCursor.CreateInitial(lineageId: 71, ModelTime.Zero),
            fixture.FirstPortalArrival,
            splitJournal,
            externalBatch.Events);
        Assert.Equal(StopReason.BoundaryReached, firstSegment.StopReason);
        Assert.Equal(fixture.RegionEntrance, Entity(firstSegment.World, fixture.MoverId).Cell);
        int prefixLength = splitJournal.Events.Count;

        SpatialState replayedPrefix = Replay(
            fixture.Definition,
            initialState,
            splitJournal.Events.Take(prefixLength),
            reducer);
        Assert.Equal(firstSegment.World, replayedPrefix);
        EventCandidate<SpatialMomentCandidate> liveNext = Assert.Single(
            subsystem.ForecastNext(firstSegment.World, firstSegment.CurrentTime));
        SimulationCursor forkCursor = SimulationCursor.CreateFork(
            lineageId: 99,
            firstSegment.CurrentTime,
            firstSegment.Cursor.NextBatchOrdinal);
        EventCandidate<SpatialMomentCandidate> forkNext = Assert.Single(
            subsystem.ForecastNext(replayedPrefix, forkCursor.Now));
        AssertCandidateEqual(liveNext, forkNext);

        SimulationRunResult<SpatialState, SpatialEvent> splitResult = loop.Run(
            firstSegment.World,
            firstSegment.Cursor,
            finalBoundary,
            splitJournal);
        Assert.Equal(oneShot.World, splitResult.World);
        Assert.Equal(JournalFacts(oneShotJournal.Events), JournalFacts(splitJournal.Events));

        var forkSuffixJournal = new InMemoryJournal<SpatialEvent>();
        SimulationRunResult<SpatialState, SpatialEvent> forkResult = loop.Run(
            replayedPrefix,
            forkCursor,
            finalBoundary,
            forkSuffixJournal);
        Assert.Equal(oneShot.World, forkResult.World);
        Assert.Equal(
            JournalFacts(oneShotJournal.Events.Skip(prefixLength)),
            JournalFacts(forkSuffixJournal.Events));

        SpatialState replayedComplete = Replay(
            fixture.Definition,
            initialState,
            oneShotJournal.Events,
            reducer);
        Assert.Equal(oneShot.World, replayedComplete);
        Assert.Empty(subsystem.ForecastNext(replayedComplete, oneShot.CurrentTime));
    }

    private static void AssertVisibilityLifecycle(
        IEnumerable<SpatialEvent> history,
        EntityId observer,
        EntityId target)
    {
        GeometricVisibilityChangedEvent[] changes =
        [
            .. history.OfType<GeometricVisibilityChangedEvent>()
                .Where(value => value.ObserverId == observer),
        ];
        Assert.Contains(changes, change => change.AddedEntityIds.SequenceEqual([target]));
        Assert.Contains(changes, change => change.RemovedEntityIds.SequenceEqual([target]));
    }

    private static SpatialEntityState Entity(SpatialState state, EntityId entityId) =>
        state.Entities.Single(entity => entity.Id == entityId);

    private static SpatialState Replay(
        SpatialDefinition definition,
        SpatialState initial,
        IEnumerable<DomainEvent<SpatialEvent>> events,
        SpatialReducer reducer)
    {
        SpatialState state = events.Aggregate(initial, reducer.Apply);
        SpatialStateValidator.ValidateComplete(definition, state);
        return state;
    }

    private static JournalFact[] JournalFacts(IEnumerable<DomainEvent<SpatialEvent>> events) =>
    [
        .. events.Select(value => new JournalFact(
            value.Timestamp,
            value.Cause,
            value.Kind,
            value.Payload)),
    ];

    private static void AssertCandidateEqual(
        EventCandidate<SpatialMomentCandidate> expected,
        EventCandidate<SpatialMomentCandidate> actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Due, actual.Due);
        Assert.Equal(expected.SourceId, actual.SourceId);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    private static LayeredFixture CreateFixture()
    {
        CellRef start = Cell("world", 0);
        CellRef worldMiddle = Cell("world", 1);
        CellRef worldExit = Cell("world", 2);
        CellRef regionEntrance = Cell("region", 0);
        CellRef regionMiddle = Cell("region", 1);
        CellRef regionExit = Cell("region", 2);
        CellRef interiorEntrance = Cell("interior", 0);
        CellRef interiorMiddle = Cell("interior", 1);
        CellRef sanctum = Cell("interior", 2);
        var worldToRegionPortalId = new PortalId("portal.world-to-region");
        var regionToInteriorPortalId = new PortalId("portal.region-to-interior");
        var sanctumAnchorId = new AnchorId("anchor.sanctum");
        var originZoneId = new ZoneId("zone.origin-camp");
        var sanctumZoneId = new ZoneId("zone.sanctum");
        var worldToRegion = new PortalDefinition(
            worldToRegionPortalId,
            worldExit,
            regionEntrance,
            new ModelDuration(7),
            initiallyEnabled: true);
        var regionToInterior = new PortalDefinition(
            regionToInteriorPortalId,
            regionExit,
            interiorEntrance,
            new ModelDuration(11),
            initiallyEnabled: true);
        SpatialDefinition definition = SpatialDefinition.Create(
            new SpatialDefinitionId("acceptance.layered-world"),
            revision: 1,
            rulesVersion: 1,
            maps:
            [
                Map("interior", stepTicks: 30),
                Map("world", stepTicks: 10),
                Map("region", stepTicks: 20),
            ],
            portals: [worldToRegion, regionToInterior],
            anchors: [new AnchorDefinition(sanctumAnchorId, sanctum)],
            zones:
            [
                new ZoneDefinition(sanctumZoneId, [sanctum]),
                new ZoneDefinition(originZoneId, [start]),
            ]);

        return new LayeredFixture(
            definition,
            new EntityId(1),
            new EntityId(2),
            start,
            worldMiddle,
            regionEntrance,
            sanctum,
            worldToRegionPortalId,
            regionToInteriorPortalId,
            sanctumAnchorId,
            originZoneId,
            sanctumZoneId,
            FirstPortalArrival: new ModelTime(27));
    }

    private static GridMapDefinition Map(string id, long stepTicks)
    {
        var floor = new CellDefinition(
            new TerrainId("terrain.floor"),
            moveCost: 1,
            blocksMovement: false,
            blocksSight: false);
        return new GridMapDefinition(
            new MapId(id),
            width: 3,
            height: 1,
            new ModelDuration(stepTicks),
            visionRange: 3,
            rowMajorCells: [floor, floor, floor]);
    }

    private static CellRef Cell(string mapId, int x) => new(new MapId(mapId), x, y: 0);

    private sealed record LayeredFixture(
        SpatialDefinition Definition,
        EntityId MoverId,
        EntityId CompanionId,
        CellRef Start,
        CellRef WorldMiddle,
        CellRef RegionEntrance,
        CellRef Sanctum,
        PortalId WorldToRegionPortalId,
        PortalId RegionToInteriorPortalId,
        AnchorId SanctumAnchorId,
        ZoneId OriginZoneId,
        ZoneId SanctumZoneId,
        ModelTime FirstPortalArrival);

    private sealed record JournalFact(
        LogicalTimestamp Timestamp,
        EventCause Cause,
        EventKind Kind,
        SpatialEvent Payload);
}
