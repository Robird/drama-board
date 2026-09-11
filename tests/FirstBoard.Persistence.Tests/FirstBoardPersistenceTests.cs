using Atelia.DurableGraph;
using Atelia.DurableGraph.StateStore;
using DramaBoard.FirstBoard.Persistence;
using DramaBoard.FirstBoard.Tests;
using DramaBoard.Kernel;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class FirstBoardPersistenceTests
{
    private static readonly FirstBoardDriverBinding DriversBinding = new([BoardIds.Alice], "encounter-response/v1");
    private static SimulationRules Rules(ulong seed) => new(seed, 10_000);

    [Theory]
    [InlineData(PassageEncounterResolution.Continued, 0)]
    [InlineData(PassageEncounterResolution.Continued, 1)]
    [InlineData(PassageEncounterResolution.Continued, 2)]
    [InlineData(PassageEncounterResolution.Reversed, 0)]
    [InlineData(PassageEncounterResolution.Reversed, 1)]
    [InlineData(PassageEncounterResolution.Reversed, 2)]
    public async Task CompleteWorld_ReopensAndContinuesWithoutHistoricalReplay(
        PassageEncounterResolution response, int completedBeforeClose)
    {
        using var directory = new TemporaryJournalDirectory();
        var expected = await EncounterPersistenceOracle.OpenEncounterAsync(response);
        await EncounterPersistenceOracle.CompleteResponseAsync(expected);
        var s0 = EncounterPersistenceOracle.CreateTravelingS0();
        using (var history = Create(directory.Path, s0))
        {
            var kernel = FirstBoardScenario.CreateKernel(Drivers(response), s0.Instance, history);
            for (int index = 0; index < completedBeforeClose; index++)
            {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(EncounterPersistenceOracle.ContactDue));
            }
        }
        using (var history = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding))
        {
            Assert.Equal(completedBeforeClose, history.Cursor.Version.TransitionCount);
            var kernel = FirstBoardScenario.CreateKernel(Drivers(response), history.Scenario, history);
            Assert.False(kernel.RecoverPending());
            for (int index = completedBeforeClose; index < 2; index++)
            {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(EncounterPersistenceOracle.ContactDue));
            }
            EncounterPersistenceOracle.AssertWorldEqual(expected.Kernel.World, kernel.World);
            Assert.Equal(expected.Kernel.Cursor, kernel.Cursor);
            Assert.Null(history.PendingEvent);
        }
        using var reader = EventHistoryRepository.OpenReadOnlyExisting(directory.Path);
        var frames = reader.ReadEvents("main");
        Assert.Equal(2, frames.Count);
        var models = EventModels();
        for (int index = 0; index < frames.Count; index++)
        {
            AssertEventEqual(expected.History.CompletedEvents[index],
                reader.ReadEvent<OccurrenceEvent<FirstBoardFact>>(frames[index], models));
        }
        Assert.Equal(5, reader.ReadFrames("main").Count); // S0 + two complete E/S pairs.
    }

    [Fact]
    public async Task PendingEvent_RecoversExactlyItsFactsWithoutForecastPlanOrPlayer()
    {
        using var directory = new TemporaryJournalDirectory();
        var expected = await EncounterPersistenceOracle.OpenEncounterAsync(PassageEncounterResolution.Continued);
        var occurrence = Assert.Single(expected.History.CompletedEvents);
        var s0 = expected.S0;
        using (var history = Create(directory.Path, s0)) { history.CommitEvent(occurrence); }
        using (var history = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding))
        {
            EncounterPersistenceOracle.AssertWorldEqual(s0.World, history.State);
            AssertEventEqual(occurrence, Assert.IsType<OccurrenceEvent<FirstBoardFact>>(history.PendingEvent));
            var spy = new RecoverySpy(history);
            Assert.True(spy.Kernel.RecoverPending());
            Assert.Equal(occurrence.Facts.Count, spy.FoldCalls);
            Assert.Equal(0, spy.RuleCalls);
            Assert.Null(history.PendingEvent);
            EncounterPersistenceOracle.AssertWorldEqual(expected.Kernel.World, history.State);
            Assert.Equal(expected.Kernel.Cursor, history.Cursor);
            Assert.False(spy.Kernel.RecoverPending());
            Assert.Equal(occurrence.Facts.Count, spy.FoldCalls);
        }
        using var saved = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding);
        var again = new RecoverySpy(saved);
        Assert.False(again.Kernel.RecoverPending());
        Assert.Equal(0, again.FoldCalls);
        Assert.Equal(0, again.RuleCalls);
        Assert.Equal(1, saved.Cursor.Version.TransitionCount);
    }

    [Theory]
    [InlineData(PublicationInterruption.BeforeEvent)]
    [InlineData(PublicationInterruption.AfterEvent)]
    [InlineData(PublicationInterruption.BeforeState)]
    [InlineData(PublicationInterruption.AfterState)]
    public async Task PublicationException_ReopenDeterminesOutcomeWithoutDuplicateOccurrence(PublicationInterruption point)
    {
        using var directory = new TemporaryJournalDirectory();
        var expected = await EncounterPersistenceOracle.OpenEncounterAsync(PassageEncounterResolution.Continued);
        var s0 = expected.S0;
        using (var history = Create(directory.Path, s0))
        {
            var interrupted = new InterruptingHistory(history, point);
            var kernel = FirstBoardScenario.CreateKernel(Drivers(PassageEncounterResolution.Continued), s0.Instance, interrupted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(EncounterPersistenceOracle.ContactDue).AsTask());
            Assert.True(kernel.IsFaulted);
            Assert.Equal(0, kernel.Version.TransitionCount);
            Assert.Null(kernel.LastCompletion);
            await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.StepAsync(EncounterPersistenceOracle.ContactDue).AsTask());
        }
        using (var history = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding))
        {
            var spy = new RecoverySpy(history);
            bool pending = point is PublicationInterruption.AfterEvent or PublicationInterruption.BeforeState;
            Assert.Equal(pending, spy.Kernel.RecoverPending());
            Assert.Equal(pending ? 2 : 0, spy.FoldCalls);
            Assert.Equal(0, spy.RuleCalls);
            Assert.Equal(point == PublicationInterruption.BeforeEvent ? 0 : 1, history.Cursor.Version.TransitionCount);
            if (point == PublicationInterruption.BeforeEvent)
            {
                var kernel = FirstBoardScenario.CreateKernel(Drivers(PassageEncounterResolution.Continued), history.Scenario, history);
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(EncounterPersistenceOracle.ContactDue));
            }
            EncounterPersistenceOracle.AssertWorldEqual(expected.Kernel.World, history.State);
            Assert.Equal(expected.Kernel.Cursor, history.Cursor);
        }
        using var reader = EventHistoryRepository.OpenReadOnlyExisting(directory.Path);
        Assert.Single(reader.ReadEvents("main"));
        Assert.Equal(3, reader.ReadFrames("main").Count);
    }

    [Fact]
    public void Reopen_RejectsDifferentDefinitionRulesSeedAndDriverPolicy_WithoutChangingHistory()
    {
        using var directory = new TemporaryJournalDirectory();
        var s0 = EncounterPersistenceOracle.CreateTravelingS0();
        using (var history = Create(directory.Path, s0)) { }
        var differentDefinition = new ScenarioInstance(s0.Instance.Definition with
            { CellarDeadlineMs = s0.Instance.Definition.CellarDeadlineMs + 1 }, s0.Instance.WorldSeed);
        Assert.Throws<InvalidDataException>(() => FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding, differentDefinition));
        Assert.Throws<InvalidDataException>(() => FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding,
            new ScenarioInstance(s0.Instance.Definition, s0.Instance.WorldSeed + 1)));
        Assert.Throws<InvalidDataException>(() => FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding,
            expectedRules: new SimulationRules(s0.Instance.WorldSeed, 9_999)));
        Assert.Throws<InvalidDataException>(() => FirstBoardOccurrenceHistory.Open(directory.Path, "main",
            new FirstBoardDriverBinding([BoardIds.Bob], DriversBinding.PolicyId)));
        Assert.Throws<InvalidDataException>(() => FirstBoardOccurrenceHistory.Open(directory.Path, "main",
            new FirstBoardDriverBinding([BoardIds.Alice], "different-policy")));
        using var valid = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding, s0.Instance, Rules(s0.Instance.WorldSeed));
        EncounterPersistenceOracle.AssertWorldEqual(s0.World, valid.State);
        Assert.Equal(s0.History.Cursor, valid.Cursor);
    }

    [Fact]
    public async Task SavedActiveTravelGoal_ContinuesControllerWithoutAskingPlayer()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(903);
        FirstBoardWorld world = scenario.CreateInitialWorld();
        var reducer = new FirstBoardReducer(scenario.Graph);
        var instant = new LogicalInstant(world.Now, 0);
        world = reducer.Apply(world, instant, new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Bob, new ModelTime(1_000_000))));
        world = reducer.Apply(world, instant, new GameBoardFact(new ActorTravelGoalSetEvent(BoardIds.Alice, new PlaceId(BoardIds.Cellar))));
        var initialCursor = FirstBoardScenario.CreateMemoryHistory(world).Cursor;
        using (var history = FirstBoardOccurrenceHistory.Create(directory.Path, "main", scenario, world,
            initialCursor, Rules(scenario.WorldSeed), DriversBinding)) { }
        var getter = new CountingFullMapGetter();
        using var reopened = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding);
        EncounterPersistenceOracle.AssertWorldEqual(world, reopened.State);
        Assert.Equal(0, getter.CallCount);
        var kernel = FirstBoardScenario.CreateKernel(new Dictionary<string, IPlayerDriver>
            { [BoardIds.Alice] = new ThrowingPlayerDriver() }, reopened.Scenario, reopened, getter);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        Assert.Equal(1, getter.CallCount);
        Assert.Equal(new PlaceId(BoardIds.Cellar), kernel.World.Actor(BoardIds.Alice).TravelGoalPlaceId);
        Assert.IsType<TraversingLocation>(kernel.World.Spatial.Entities.Single(item => item.Id == new EntityId(BoardIds.Alice)).Location);
        Assert.Equal(1, kernel.Version.TransitionCount);
    }

    [Fact]
    public void CompleteState_PreservesKnowledgeTextIdentityCounterEntryOverridesAndScheduledChanges()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(904);
        FirstBoardWorld world = scenario.CreateInitialWorld();
        var reducer = new FirstBoardReducer(scenario.Graph);
        var instant = new LogicalInstant(world.Now, 0);
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorObservedEvent(BoardIds.Alice,
                [new BoardFact("test.saved-knowledge", BoardIds.Bob, "Full knowledge text: Unicode 世界 and detail.")], null)),
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(new PassageId(BoardIds.CellarGatePassage), new PassageEntryAccess(false, true))),
            new SpatialBoardFact(new PassageEntryChangeScheduledFact(new PassageId(BoardIds.MarketCellarApproach),
                new ModelTime(500_000), new PassageEntryPatch(null, false))),
        ];
        foreach (var fact in facts) { world = reducer.Apply(world, instant, fact); }
        world = world.With(game: world.Game.With(nextPersistentId: world.Game.NextPersistentId + 12));
        var cursor = FirstBoardScenario.CreateMemoryHistory(world).Cursor;
        using (var history = FirstBoardOccurrenceHistory.Create(directory.Path, "main", scenario, world,
            cursor, Rules(scenario.WorldSeed), DriversBinding)) { }
        using var restored = FirstBoardOccurrenceHistory.Open(directory.Path, "main", DriversBinding);
        EncounterPersistenceOracle.AssertWorldEqual(world, restored.State);
        Assert.True(restored.State.CellarSealed);
        Assert.Single(restored.State.Spatial.ScheduledPassageEntryChanges);
        Assert.NotEmpty(restored.State.Spatial.PassageEntryAccessOverrides);
        Assert.Equal("Full knowledge text: Unicode 世界 and detail.", restored.State.Actor(BoardIds.Alice).KnownFacts.Single(fact => fact.Kind == "test.saved-knowledge").Text);
    }

    [Fact]
    public void EventOnlyRegistry_RoundTripsEveryCurrentFactShape_WithoutWorldModels()
    {
        var headOnContact = new PassageContactKey(
            new PassageId("passage"),
            new EntityId("actor"),
            movementGenerationA: 3,
            new EntityId("target"),
            movementGenerationB: 5);
        var overtakeContact = new PassageContactKey(
            new PassageId("passage"),
            new EntityId("target"),
            movementGenerationA: 8,
            new EntityId("actor"),
            movementGenerationB: 7);
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorTravelStartedEvent("actor", "exit:road", "goal")),
            new GameBoardFact(new ActorTravelGoalSetEvent("actor", new PlaceId("goal"))),
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                "actor",
                new PlaceId("goal"),
                TravelGoalResolution.Completed)),
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                "actor",
                new PlaceId("goal"),
                TravelGoalResolution.Blocked)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                headOnContact,
                PassageContactKind.HeadOnMeeting)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                overtakeContact,
                PassageContactKind.Overtake)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                "actor",
                PassageEncounterResolution.Continued)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                "actor",
                PassageEncounterResolution.Reversed)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                headOnContact,
                RespondingActorId: null,
                PassageEncounterResolution.WorldChanged)),
            new GameBoardFact(new TicketConsumedEvent("actor", "ticket")),
            new GameBoardFact(new ActorWaitStartedEvent("actor", new ModelTime(11))),
            new GameBoardFact(new ActorWaitedEvent("actor")),
            new GameBoardFact(new ActorSpokeEvent("actor", "target", "hello", "known.fact")),
            new GameBoardFact(new ActorObservedEvent(
                "actor",
                [new BoardFact("known.fact", "related", "text")],
                "object")),
            new GameBoardFact(new ObjectTakenEvent("actor", "object")),
            new GameBoardFact(new ObjectPlacedEvent("actor", "object", "place")),
            new GameBoardFact(new ObjectGivenEvent("actor", "target", "object")),
            new GameBoardFact(new ObjectShownEvent("actor", "target", "object")),
            new GameBoardFact(new ChestOpenedEvent("actor", "chest", "key")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.Wait, DurationMs: 1),
                "reason")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.ContinueTravel, FreeText: "keep going"),
                "reason")),
            new GameBoardFact(new ActionRejectedEvent(
                "actor",
                new Intent(ActionKinds.ReverseTravel),
                "reason")),
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new EntityPlacedFact(new EntityId("entity"), new PlaceId("place"))),
            new SpatialBoardFact(new EntityRemovedFact(new EntityId("entity"))),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId("entity"),
                new PassageId("passage"),
                new PlaceId("from"),
                SpeedSnapshot: 2)),
            new SpatialBoardFact(new TraversalReversedFact(
                new EntityId("entity"),
                ExpectedMovementGeneration: 3)),
            new SpatialBoardFact(new PassageContactOccurredFact(
                headOnContact,
                PassageContactKind.HeadOnMeeting)),
            new SpatialBoardFact(new PassageContactOccurredFact(
                overtakeContact,
                PassageContactKind.Overtake)),
            new SpatialBoardFact(new TraversalArrivedFact(new EntityId("entity"), 3)),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(
                new PassageId("passage"),
                new PassageEntryAccess(false, true))),
            new SpatialBoardFact(new PassageEntryChangeScheduledFact(
                new PassageId("passage"),
                new ModelTime(12),
                new PassageEntryPatch(false, null))),
            new SpatialBoardFact(new ScheduledPassageEntryChangeAppliedFact(
                new PassageId("passage"),
                new ModelTime(12))),
        ];


        // These are serialization shapes, not one valid gameplay transition to be folded.
        using var directory = new TemporaryJournalDirectory();
        var s0 = EncounterPersistenceOracle.CreateTravelingS0();
        var expected = new OccurrenceEvent<FirstBoardFact>(CandidateKey.FromUtf8("all-fact-shapes"),
            new LogicalInstant(ModelTime.Zero, 0), facts);
        using (var history = Create(directory.Path, s0)) { history.CommitEvent(expected); }
        using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory.Path);
        var frame = Assert.Single(repository.ReadEvents("main"));
        StateModelRegistry models = EventModels();
        var actual = repository.ReadEvent<OccurrenceEvent<FirstBoardFact>>(frame, models);
        AssertEventEqual(expected, actual);
        Assert.Equal(facts.Select(fact => fact.GetType()), actual.Facts.Select(fact => fact.GetType()));
        // A deliberately absent State root proves the read registry is not a full facade registry.
        Assert.ThrowsAny<Exception>(() => repository.ReadState<FirstBoardCommittedState>(repository.GetPreviousState(frame), models));
    }

    [Fact]
    public void HistoricalEvent_KeepsKnowledgeSnapshotAfterLaterStateChanges()
    {
        using var directory = new TemporaryJournalDirectory();
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(902);
        FirstBoardWorld initial = scenario.CreateInitialWorld();
        var cursor = FirstBoardScenario.CreateMemoryHistory(initial).Cursor;
        var reducer = new FirstBoardReducer(scenario.Graph);
        var oldFact = new BoardFact("snapshot.fact", BoardIds.Bob, "old event text");
        var observed = new GameBoardFact(new ActorObservedEvent(BoardIds.Alice, [oldFact], null));
        var instant = new LogicalInstant(initial.Now, 0);
        var first = new OccurrenceEvent<FirstBoardFact>(CandidateKey.FromUtf8("snapshot/first"), instant, [observed]);
        using (var history = FirstBoardOccurrenceHistory.Create(directory.Path, "main", scenario,
            initial, cursor, Rules(scenario.WorldSeed), DriversBinding))
        {
            history.CommitEvent(first);
            history.CommitState(reducer.Apply(initial, instant, observed), cursor.Advance(first.CauseKey, instant));
            var secondFact = new GameBoardFact(new ActorObservedEvent(BoardIds.Alice,
                [new BoardFact("snapshot.fact", BoardIds.Bob, "new world text")], null));
            var secondInstant = new LogicalInstant(initial.Now, 1);
            var second = new OccurrenceEvent<FirstBoardFact>(CandidateKey.FromUtf8("snapshot/second"), secondInstant, [secondFact]);
            history.CommitEvent(second);
            history.CommitState(reducer.Apply(history.State, secondInstant, secondFact), history.Cursor.Advance(second.CauseKey, secondInstant));
        }
        using var repository = EventHistoryRepository.OpenReadOnlyExisting(directory.Path);
        var events = repository.ReadEvents("main");
        var oldEvent = repository.ReadEvent<OccurrenceEvent<FirstBoardFact>>(events[0], EventModels());
        AssertEventEqual(first, oldEvent);
        var newEvent = repository.ReadEvent<OccurrenceEvent<FirstBoardFact>>(events[1], EventModels());
        Assert.NotEqual(oldEvent.Facts[0], newEvent.Facts[0]);
        Assert.Equal("old event text", oldFact.Text);
    }

    private static FirstBoardOccurrenceHistory Create(string path, EncounterPersistenceOracle.EncounterS0 s0) =>
        FirstBoardOccurrenceHistory.Create(path, "main", s0.Instance, s0.World,
            new KernelCursor(new WorldVersion(FirstBoardScenario.LineageId, 0), s0.World.Now, null, null),
            Rules(s0.Instance.WorldSeed), DriversBinding);

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(PassageEncounterResolution response) =>
        new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        { [BoardIds.Alice] = new ResponseDriver(response) };

    private static void AssertEventEqual(OccurrenceEvent<FirstBoardFact> expected, OccurrenceEvent<FirstBoardFact> actual)
    {
        Assert.Equal(expected.CauseKey, actual.CauseKey);
        Assert.Equal(expected.TargetInstant, actual.TargetInstant);
        Assert.Equal(expected.Facts.ToArray(), actual.Facts.ToArray());
    }

    private static StateModelRegistry EventModels()
    {
        var registration = new EventOnlyRegistration();
        KernelDurableModels.Register(registration);
        SpatialDurableModels.Register(registration);
        FirstBoardDurableModels.Register(registration);
        Assert.DoesNotContain(typeof(FirstBoardWorld), registration.Types);
        Assert.DoesNotContain(typeof(FirstBoardGameState), registration.Types);
        Assert.DoesNotContain(typeof(GraphSpatialState), registration.Types);
        Assert.DoesNotContain(typeof(FirstBoardCommittedState), registration.Types);
        return registration.Models;
    }

    // Registration facades emit definitions into this allow-list sink. Excluded definitions
    // are never passed to StateModelRegistry; no World or State models are registered lazily.
    private sealed class EventOnlyRegistration : IStateModelRegistration
    {
        public StateModelRegistry Models { get; } = new();
        public HashSet<Type> Types { get; } = [];
        public void Register(StateDefinitionBinding definition)
        {
            if (definition.DomainTypeDefinition is { } type && Allowed(type))
            { Types.Add(type); Models.Register(definition); }
        }
        public void Register(StateModelBinding model)
        {
            if (Allowed(model.DomainType)) { Types.Add(model.DomainType); Models.Register(model); }
        }
        private static bool Allowed(Type type) =>
            typeof(FirstBoardFact).IsAssignableFrom(type) || typeof(BoardEventPayload).IsAssignableFrom(type) ||
            typeof(GraphSpatialFact).IsAssignableFrom(type) || type == typeof(OccurrenceEvent<>) ||
            type == typeof(BoardFact) || type == typeof(RejectedIntentSnapshot) ||
            type == typeof(CandidateKey) || type == typeof(LogicalInstant) || type == typeof(ModelTime) ||
            type == typeof(EntityId) || type == typeof(PlaceId) || type == typeof(PassageId) ||
            type == typeof(PassageContactKey) || type == typeof(PassageContactKind) ||
            type == typeof(PassageEntryAccess) || type == typeof(PassageEntryPatch) ||
            type == typeof(TravelGoalResolution) || type == typeof(PassageEncounterResolution);
    }

    private sealed class CountingFullMapGetter : IPlayerSpatialKnowledgeGetter<FirstBoardWorld>
    {
        public int CallCount { get; private set; }
        public PlayerSpatialKnowledgeSnapshot GetKnownGraph(FirstBoardWorld world, string subjectId, GraphDefinition graph)
        { CallCount++; return PlayerSpatialKnowledgeSnapshot.FullMap(graph); }
    }

    private sealed class ThrowingPlayerDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Restoring or continuing a delegated goal must not ask a Player.");
    }

    private sealed class ResponseDriver(PassageEncounterResolution response) : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlayerDecision(request.DecisionId,
                new Intent(response == PassageEncounterResolution.Continued ? ActionKinds.ContinueTravel : ActionKinds.ReverseTravel)));
    }

    private sealed class RecoverySpy : IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
    {
        public RecoverySpy(FirstBoardOccurrenceHistory history)
        {
            var reducer = new FirstBoardReducer(history.Scenario.Graph);
            Kernel = new(history, history.Rules, [this], (world, instant, fact) =>
                { FoldCalls++; return reducer.Apply(world, instant, fact); }, reducer.Validate);
        }
        public SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> Kernel { get; }
        public int FoldCalls { get; private set; }
        public int RuleCalls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(FirstBoardWorld world, SimulationRules rules)
        { RuleCalls++; throw new InvalidOperationException("Recovery must not Forecast."); }
        public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(FirstBoardWorld world,
            OccurrenceCandidate<BoardCandidate> winner, CancellationToken cancellationToken)
        { RuleCalls++; throw new InvalidOperationException("Recovery must not Plan or invoke a Player."); }
    }

    public enum PublicationInterruption { BeforeEvent, AfterEvent, BeforeState, AfterState }

    // Consumer-side failure seam surrounds real commits. It does not forge storage bytes or
    // claim power-loss durability: each interruption leaves the actually published head on disk.
    private sealed class InterruptingHistory(FirstBoardOccurrenceHistory inner, PublicationInterruption point)
        : IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>
    {
        public FirstBoardWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<FirstBoardFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence)
        {
            FailAt(PublicationInterruption.BeforeEvent);
            inner.CommitEvent(occurrence);
            FailAt(PublicationInterruption.AfterEvent);
        }
        public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor)
        {
            FailAt(PublicationInterruption.BeforeState);
            inner.CommitState(nextState, nextCursor);
            FailAt(PublicationInterruption.AfterState);
        }
        private void FailAt(PublicationInterruption current)
        { if (point == current) { throw new IOException($"Injected interruption at {current}."); } }
    }
}
