using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Tests;

public sealed class PassageEncounterHostTests
{
    private const long ContactDueTicks = 150_000;
    private static readonly ModelTime ContactDue = new(ContactDueTicks);

    [Fact]
    public async Task OpeningAdapter_PreservesInnerKeyDueDataAndSpatialDraft_ThenAppendsGameFact()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 401);
        FirstBoardWorld world = CreateTravelingPrefix(instance);
        var rules = new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100);
        var inner = new SpatialContactOccurrenceRule(instance.Graph);
        var outer = new FirstBoardPassageEncounterRule(
            instance.Graph,
            Drivers((BoardIds.Alice, new RecordingPlayerDriver())));

        OccurrenceCandidate<PassageContactOccurrenceData> innerCandidate =
            Assert.Single(inner.Forecast(world.Spatial, rules));
        OccurrenceCandidate<BoardCandidate> outerCandidate =
            Assert.Single(outer.Forecast(world, rules));
        PassageEncounterOpeningCandidate opening =
            Assert.IsType<PassageEncounterOpeningCandidate>(outerCandidate.Data);

        Assert.Equal(innerCandidate.Key, outerCandidate.Key);
        Assert.Equal(innerCandidate.Due, outerCandidate.Due);
        Assert.Equal(innerCandidate.Data, opening.Value);

        TransitionDraft<GraphSpatialFact> innerDraft = await inner.PlanSelectedAsync(
            world.Spatial,
            innerCandidate,
            CancellationToken.None);
        TransitionDraft<FirstBoardFact> outerDraft = await outer.PlanSelectedAsync(
            world,
            outerCandidate,
            CancellationToken.None);

        Assert.Equal(2, outerDraft.Facts.Count);
        Assert.Equal(
            Assert.Single(innerDraft.Facts),
            Assert.IsType<SpatialBoardFact>(outerDraft.Facts[0]).Value);
        PassageEncounterOpenedEvent opened = Assert.IsType<PassageEncounterOpenedEvent>(
            Assert.IsType<GameBoardFact>(outerDraft.Facts[1]).Value);
        Assert.Equal(innerCandidate.Data.ContactKey, opened.ContactKey);
        Assert.Equal(innerCandidate.Data.Kind, opened.Kind);
    }

    [Fact]
    public async Task SingleDriver_ContinueRunsPlayableCoreAndBuildsLocalRequestWithoutKnowledgeGetter()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 402);
        FirstBoardWorld initial = CreateTravelingPrefix(instance, withAliceGoal: true);
        BoardActor aliceBefore = initial.Actor(BoardIds.Alice);
        SpatialEntity aliceSpatialBefore = SpatialEntity(initial, BoardIds.Alice);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ContinueTravel, FreeText: "Keep going.")));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice)),
                instance,
                journal,
                spatialKnowledgeGetter: new ThrowingKnowledgeGetter());

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));
        Assert.Empty(alice.Requests);
        AssertOpeningBatch(journal.CompletedEvents[0]);
        Assert.NotNull(kernel.World.Game.PendingEncounter);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));

        DecisionRequest request = Assert.Single(alice.Requests);
        AssertEncounterRequest(
            request,
            actorId: BoardIds.Alice,
            counterpartId: BoardIds.Bob,
            expectedTarget: BoardIds.Market,
            expectedReverseDestination: BoardIds.Tavern,
            expectedGoal: BoardIds.Cellar,
            reverseAdvertised: true);
        PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
            Assert.IsType<GameBoardFact>(Assert.Single(journal.CompletedEvents[1].Facts)).Value);
        Assert.Equal(PassageEncounterResolution.Continued, resolved.Resolution);
        Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
        Assert.Null(kernel.World.Game.PendingEncounter);
        BoardActor aliceAfter = kernel.World.Actor(BoardIds.Alice);
        Assert.Equal(aliceBefore.Generation + 1, aliceAfter.Generation);
        Assert.Equal(aliceBefore.DecisionSequence + 1, aliceAfter.DecisionSequence);
        Assert.Equal(aliceBefore.TravelGoalPlaceId, aliceAfter.TravelGoalPlaceId);
        Assert.Equal(aliceSpatialBefore, SpatialEntity(kernel.World, BoardIds.Alice));
    }

    [Fact]
    public async Task Reverse_CommitsGameThenSpatial_ClearsGoalAndReanchorsMovement()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 403);
        FirstBoardWorld initial = CreateTravelingPrefix(instance, withAliceGoal: true);
        BoardActor aliceBefore = initial.Actor(BoardIds.Alice);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ReverseTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice)),
                instance,
                journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));

        Assert.Collection(
            journal.CompletedEvents[1].Facts,
            fact =>
            {
                PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
                    Assert.IsType<GameBoardFact>(fact).Value);
                Assert.Equal(PassageEncounterResolution.Reversed, resolved.Resolution);
                Assert.Equal(BoardIds.Alice, resolved.RespondingActorId);
            },
            fact =>
            {
                TraversalReversedFact reversed = Assert.IsType<TraversalReversedFact>(
                    Assert.IsType<SpatialBoardFact>(fact).Value);
                Assert.Equal(new EntityId(BoardIds.Alice), reversed.EntityId);
                Assert.Equal(1, reversed.ExpectedMovementGeneration);
            });
        BoardActor aliceAfter = kernel.World.Actor(BoardIds.Alice);
        Assert.Null(aliceAfter.TravelGoalPlaceId);
        Assert.Equal(aliceBefore.Generation + 1, aliceAfter.Generation);
        Assert.Equal(aliceBefore.DecisionSequence + 1, aliceAfter.DecisionSequence);
        Assert.Contains(
            "interrupted",
            aliceAfter.KnownFacts.Single(fact => fact.Kind == BoardIds.LastActionOutcome).Text,
            StringComparison.Ordinal);
        SpatialEntity spatial = SpatialEntity(kernel.World, BoardIds.Alice);
        Assert.Equal(2, spatial.MovementGeneration);
        TraversingLocation reversedTraversal = Assert.IsType<TraversingLocation>(spatial.Location);
        Assert.Equal(new PlaceId(BoardIds.Tavern), reversedTraversal.TargetPlaceId);
        Assert.Equal(ContactDue, reversedTraversal.AnchorTime);
        Assert.Empty(kernel.World.Spatial.ConsumedContacts);
        Assert.Null(kernel.World.Game.PendingEncounter);
    }

    [Fact]
    public async Task ClosedReverse_IsNotAdvertised_AndForgedReverseCommitsNothing()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 404);
        FirstBoardWorld initial = CreateTravelingPrefix(instance, closeReverseEntry: true);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ReverseTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice)),
                instance,
                journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));
        string openedSnapshot = FirstBoardScenario.WorldSnapshot(kernel.World);
        WorldVersion openedVersion = kernel.Version;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ContactDue));

        DecisionRequest request = Assert.Single(alice.Requests);
        AssertEncounterRequest(
            request,
            BoardIds.Alice,
            BoardIds.Bob,
            BoardIds.Market,
            BoardIds.Tavern,
            expectedGoal: null,
            reverseAdvertised: false);
        Assert.Equal(openedSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Equal(openedVersion, kernel.Version);
        Assert.Single(journal.CompletedEvents);
        Assert.NotNull(kernel.World.Game.PendingEncounter);
    }

    [Fact]
    public async Task ArrivalStalesPending_ThenCleanupCallsNoPlayerAndPreservesGoalAndSequence()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 405);
        FirstBoardWorld opened = CreateOpenedPrefix(instance, withAliceGoal: true);
        var reducer = new FirstBoardReducer(instance.Graph);
        SpatialEntity aliceSpatial = SpatialEntity(opened, BoardIds.Alice);
        TraversingLocation aliceTraversal = Assert.IsType<TraversingLocation>(aliceSpatial.Location);
        FirstBoardWorld stale = reducer.Apply(
            opened,
            new LogicalInstant(aliceTraversal.ArrivalDue, 0),
            new SpatialBoardFact(new TraversalArrivedFact(
                aliceSpatial.Id,
                aliceSpatial.MovementGeneration)));
        BoardActor aliceBeforeCleanup = stale.Actor(BoardIds.Alice);
        var alice = new RecordingPlayerDriver();
        var journal = FirstBoardScenario.CreateMemoryHistory(stale);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice)),
                instance,
                journal);

        for (int step = 0; step < 2 && kernel.World.Game.PendingEncounter is not null; step++)
        {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(kernel.World.Now));
        }

        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Empty(alice.Requests);
        PassageEncounterResolvedEvent cleanup = journal.CompletedEvents
            .SelectMany(batch => batch.Facts)
            .Select(fact => fact is GameBoardFact game ? game.Value : null)
            .OfType<PassageEncounterResolvedEvent>()
            .Single();
        Assert.Equal(PassageEncounterResolution.WorldChanged, cleanup.Resolution);
        Assert.Null(cleanup.RespondingActorId);
        BoardActor aliceAfterCleanup = kernel.World.Actor(BoardIds.Alice);
        Assert.Equal(aliceBeforeCleanup.Generation, aliceAfterCleanup.Generation);
        Assert.Equal(aliceBeforeCleanup.DecisionSequence, aliceAfterCleanup.DecisionSequence);
        Assert.Equal(aliceBeforeCleanup.TravelGoalPlaceId, aliceAfterCleanup.TravelGoalPlaceId);
    }

    [Fact]
    public async Task TwoDrivers_DifferentSeedsCanRespondButEachEncounterCallsExactlyOnePlayer()
    {
        (ulong aliceSeed, ulong bobSeed) = FindTwoResponderSeeds();
        Assert.NotEqual(aliceSeed, bobSeed);

        await AssertOnlyExpectedResponderAsync(aliceSeed, BoardIds.Alice);
        await AssertOnlyExpectedResponderAsync(bobSeed, BoardIds.Bob);
    }

    [Fact]
    public async Task NoDriverContact_IsNotOpenedOrConsumedByFirstBoardKernel()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 406);
        FirstBoardWorld initial = CreateTravelingPrefix(instance);
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal),
                instance,
                journal);

        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(ContactDue));
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Empty(kernel.World.Spatial.ConsumedContacts);
        Assert.Empty(journal.CompletedEvents);
    }

    [Fact]
    public void CurrentPendingWithoutAnyRegisteredResponder_IsAConfigurationInvariant()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 410);
        FirstBoardWorld opened = CreateOpenedPrefix(instance);
        var rule = new FirstBoardPassageEncounterResponseRule(
            instance.Graph,
            new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal));

        Assert.Throws<InvalidOperationException>(() => rule.Forecast(
            opened,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
    }

    [Fact]
    public async Task SelectedResponse_RejectsTamperedIdentityDueAndActorStateBeforeCallingPlayer()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 411);
        FirstBoardWorld opened = CreateOpenedPrefix(instance);
        var alice = new RecordingPlayerDriver();
        var rule = new FirstBoardPassageEncounterResponseRule(
            instance.Graph,
            Drivers((BoardIds.Alice, alice)));
        OccurrenceCandidate<BoardCandidate> candidate = Assert.Single(rule.Forecast(
            opened,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        PassageEncounterResponseCandidate response =
            Assert.IsType<PassageEncounterResponseCandidate>(candidate.Data);
        OccurrenceCandidate<BoardCandidate>[] tampered =
        [
            new(
                CandidateKey.FromUtf8("tampered"),
                candidate.Due,
                response),
            new(
                candidate.Key,
                new CandidateDue(new ModelTime(opened.Now.Ticks + 1)),
                response),
            new(
                candidate.Key,
                candidate.Due,
                response with { ActorGeneration = response.ActorGeneration + 1 }),
            new(
                candidate.Key,
                candidate.Due,
                response with { NextDecisionSequence = response.NextDecisionSequence + 1 }),
        ];
        foreach (OccurrenceCandidate<BoardCandidate> invalid in tampered)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await rule.PlanSelectedAsync(opened, invalid, CancellationToken.None));
        }

        FirstBoardWorld changedActor = opened.With(game: opened.Game.With(actors: Array.AsReadOnly(opened.Actors.Select(actor =>
                    actor.Key == BoardIds.Alice
                        ? actor.With(generation: actor.Generation + 1)
                        : actor).ToArray())));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rule.PlanSelectedAsync(changedActor, candidate, CancellationToken.None));
        Assert.Empty(alice.Requests);
    }

    [Theory]
    [InlineData(300_000, ContactDueTicks)]
    [InlineData(11, 5)]
    public async Task SameTickPeerContacts_AreOpenedAndResolvedOneAtATimeWithoutConsumptionLoss(
        long passageLength,
        long contactDueTicks)
    {
        ScenarioInstance instance = CreateRoadInstance(worldSeed: 407, length: passageLength);
        FirstBoardWorld initial = CreateThreeActorTravelingPrefix(instance);
        var bob = new RecordingPlayerDriver(
            request => new PlayerDecision(request.DecisionId, new Intent(ActionKinds.ContinueTravel)),
            request => new PlayerDecision(request.DecisionId, new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Bob, bob)),
                instance,
                journal);

        for (int step = 0; step < 4; step++)
        {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(contactDueTicks)));
        }

        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Equal(2, kernel.World.Spatial.ConsumedContacts.Count);
        Assert.Equal(2, bob.Requests.Count);
        Assert.Equal(4, journal.CompletedEvents.Count);
        Assert.All(journal.CompletedEvents,
            occurrence => Assert.Equal(new ModelTime(contactDueTicks), occurrence.TargetInstant.ModelTime));
        Assert.Equal(
            2,
            journal.CompletedEvents.SelectMany(batch => batch.Facts)
                .Count(fact => fact is SpatialBoardFact { Value: PassageContactOccurredFact }));
        Assert.Equal(
            2,
            journal.CompletedEvents.SelectMany(batch => batch.Facts)
                .Count(fact => fact is GameBoardFact
                    { Value: PassageEncounterResolvedEvent
                    { Resolution: PassageEncounterResolution.Continued } }));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(407UL)]
    public async Task FractionalContactAndResponse_PrecedeCeilingArrivalRegardlessOfSeed(ulong seed)
    {
        ScenarioInstance instance = CreateRoadInstance(seed, length: 10);
        FirstBoardWorld initial = CreateTravelingPrefix(instance, aliceSpeed: 6, bobSpeed: 1);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId, new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var kernel = FirstBoardScenario.CreateKernel(Drivers((BoardIds.Alice, alice)), instance, journal);
        var interactionTime = new ModelTime(1);

        // Exact contact is 10/7 and Alice's arrival is 10/6. Both used to ceil to 2.
        Assert.Equal(new ModelTime(2),
            Assert.IsType<TraversingLocation>(SpatialEntity(initial, BoardIds.Alice).Location).ArrivalDue);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(interactionTime));
        AssertOpeningBatch(Assert.Single(journal.CompletedEvents));
        Assert.Empty(alice.Requests);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(interactionTime));
        Assert.Equal(1, Assert.Single(alice.Requests).ModelTimeMs);
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.All(journal.CompletedEvents, occurrence => Assert.Equal(interactionTime, occurrence.TargetInstant.ModelTime));
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(interactionTime));
        Assert.IsType<TraversingLocation>(SpatialEntity(kernel.World, BoardIds.Alice).Location);
    }

    [Fact]
    public async Task BornAtAnchorContact_OffersContinueOnly_AndResolvesBeforeArrival()
    {
        ScenarioInstance instance = CreateRoadInstance(worldSeed: 412, length: 1);
        FirstBoardWorld initial = CreateTravelingPrefix(instance);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId, new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var kernel = FirstBoardScenario.CreateKernel(Drivers((BoardIds.Alice, alice)), instance, journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        AssertOpeningBatch(Assert.Single(journal.CompletedEvents));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        DecisionRequest request = Assert.Single(alice.Requests);
        Assert.Equal(0, request.ModelTimeMs);
        Assert.Equal([ActionKinds.ContinueTravel], request.AvailableActions.Select(action => action.ActionKind));
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.Single(kernel.World.Spatial.ConsumedContacts);
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(ModelTime.Zero));
        Assert.All(kernel.World.Spatial.Entities.Where(entity => entity.Location is TraversingLocation),
            entity => Assert.Equal(new ModelTime(1), ((TraversingLocation)entity.Location).ArrivalDue));
    }

    [Fact]
    public async Task BornAtAnchorContact_ForgedReversePublishesNoResponse()
    {
        ScenarioInstance instance = CreateRoadInstance(worldSeed: 413, length: 1);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId, new Intent(ActionKinds.ReverseTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(CreateTravelingPrefix(instance));
        var kernel = FirstBoardScenario.CreateKernel(Drivers((BoardIds.Alice, alice)), instance, journal);
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ModelTime.Zero));
        FirstBoardWorld opened = kernel.World;
        KernelCursor cursor = kernel.Cursor;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(ModelTime.Zero));

        Assert.DoesNotContain(Assert.Single(alice.Requests).AvailableActions,
            action => action.ActionKind == ActionKinds.ReverseTravel);
        Assert.Same(opened, kernel.World);
        Assert.Equal(cursor, kernel.Cursor);
        Assert.Single(journal.CompletedEvents);
        Assert.Null(journal.PendingEvent);
        Assert.NotNull(kernel.World.Game.PendingEncounter);
    }

    [Fact]
    public async Task ReverseCreatesNewSameTickOvertake_WithoutAllowingRepeatedFlip()
    {
        ScenarioInstance instance = CreateRoadInstance(worldSeed: 414, length: 10);
        FirstBoardWorld initial = CreateTravelingPrefix(instance, aliceSpeed: 1, bobSpeed: 6);
        var alice = new RecordingPlayerDriver(
            request => new PlayerDecision(request.DecisionId, new Intent(ActionKinds.ReverseTravel)),
            request => new PlayerDecision(request.DecisionId, new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var kernel = FirstBoardScenario.CreateKernel(Drivers((BoardIds.Alice, alice)), instance, journal);
        var interactionTime = new ModelTime(1);

        // Head-on at 10/7 floors to 1. After Alice reverses at position 1, Bob catches
        // her at 8/5, also in tick 1; this is a distinct, still actionable generation pair.
        for (int step = 0; step < 4; step++)
        {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(interactionTime));
        }

        Assert.Equal(2, alice.Requests.Count);
        Assert.Contains(alice.Requests[0].AvailableActions, action => action.ActionKind == ActionKinds.ReverseTravel);
        Assert.Equal([ActionKinds.ContinueTravel],
            alice.Requests[1].AvailableActions.Select(action => action.ActionKind));
        PassageContactOccurredFact[] contacts = journal.CompletedEvents.SelectMany(occurrence => occurrence.Facts)
            .OfType<SpatialBoardFact>().Select(fact => fact.Value).OfType<PassageContactOccurredFact>().ToArray();
        Assert.Equal([PassageContactKind.HeadOnMeeting, PassageContactKind.Overtake], contacts.Select(contact => contact.Kind));
        Assert.NotEqual(contacts[0].ContactKey, contacts[1].ContactKey);
        Assert.Equal(2, SpatialEntity(kernel.World, BoardIds.Alice).MovementGeneration);
        Assert.Single(kernel.World.Spatial.ConsumedContacts);
        Assert.Null(kernel.World.Game.PendingEncounter);
        Assert.All(journal.CompletedEvents, occurrence => Assert.Equal(interactionTime, occurrence.TargetInstant.ModelTime));
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(interactionTime));
        Assert.Equal(4, journal.CompletedEvents.Count);
    }

    [Fact]
    public async Task SameTickPendingResponseAndEntryClose_DifferentSeedsCommitEitherLegalBranch()
    {
        ScenarioInstance template = ScenarioInstance.CreateDefault(worldSeed: 0);
        FirstBoardWorld templateWorld = CreateOpenedPrefixWithScheduledReverseClose(template);
        var templateDrivers = Drivers((BoardIds.Alice, new RecordingPlayerDriver()));
        var rules = new SimulationRules(template.WorldSeed, maxTransitionsPerModelTime: 100);
        OccurrenceCandidate<BoardCandidate>[] candidates =
        [
            .. new SpatialHostOccurrenceRule(template.Graph).Forecast(templateWorld, rules),
            .. new FirstBoardPassageEncounterResponseRule(template.Graph, templateDrivers)
                .Forecast(templateWorld, rules),
        ];
        (ulong responseFirstSeed, ulong closeFirstSeed) = FindContestSeeds(
            candidates,
            candidate => candidate is PassageEncounterResponseCandidate,
            candidate => candidate is SpatialBoardCandidate
                { Value: PassageEntryChangeOccurrenceData });

        await AssertResponseEntryCloseBranchAsync(responseFirstSeed, responseFirst: true);
        await AssertResponseEntryCloseBranchAsync(closeFirstSeed, responseFirst: false);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Opening_AnyFactFoldFailureCommitsNoScratchPrefix(int failAtFact)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 408);
        FirstBoardWorld initial = CreateTravelingPrefix(instance);
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var reducer = new FirstBoardReducer(instance.Graph);
        int foldCount = 0;
        var kernel = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            journal,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100),
            [new FirstBoardPassageEncounterRule(
                instance.Graph,
                Drivers((BoardIds.Alice, new RecordingPlayerDriver())))],
            (world, instant, fact) =>
            {
                foldCount++;
                if (foldCount == failAtFact)
                {
                    throw new InvalidOperationException("Injected fold failure.");
                }

                return reducer.Apply(world, instant, fact);
            },
            reducer.Validate);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(ContactDue));

        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Equal(new WorldVersion(FirstBoardScenario.LineageId, 0), kernel.Version);
        Assert.Empty(journal.CompletedEvents);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Reverse_AnyFactFoldFailureCommitsNoScratchPrefix(int failAtFact)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 409);
        FirstBoardWorld initial = CreateOpenedPrefix(instance, withAliceGoal: true);
        string initialSnapshot = FirstBoardScenario.WorldSnapshot(initial);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ReverseTravel)));
        var drivers = Drivers((BoardIds.Alice, alice));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        var reducer = new FirstBoardReducer(instance.Graph);
        int foldCount = 0;
        var kernel = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            journal,
            new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100),
            [new FirstBoardPassageEncounterResponseRule(instance.Graph, drivers)],
            (world, instant, fact) =>
            {
                foldCount++;
                if (foldCount == failAtFact)
                {
                    throw new InvalidOperationException("Injected fold failure.");
                }

                return reducer.Apply(world, instant, fact);
            },
            reducer.Validate);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await kernel.StepAsync(initial.Now));

        Assert.Single(alice.Requests);
        Assert.Equal(initialSnapshot, FirstBoardScenario.WorldSnapshot(kernel.World));
        Assert.Equal(new WorldVersion(FirstBoardScenario.LineageId, 0), kernel.Version);
        Assert.Empty(journal.CompletedEvents);
    }

    private static async Task AssertOnlyExpectedResponderAsync(ulong seed, string expectedActorId)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
        FirstBoardWorld initial = CreateTravelingPrefix(instance);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ContinueTravel)));
        var bob = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice), (BoardIds.Bob, bob)),
                instance,
                journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));

        Assert.Equal(expectedActorId == BoardIds.Alice ? 1 : 0, alice.Requests.Count);
        Assert.Equal(expectedActorId == BoardIds.Bob ? 1 : 0, bob.Requests.Count);
        Assert.Equal(1, alice.Requests.Count + bob.Requests.Count);
        Assert.Null(kernel.World.Game.PendingEncounter);
    }

    private static async Task AssertResponseEntryCloseBranchAsync(
        ulong seed,
        bool responseFirst)
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(seed);
        FirstBoardWorld initial = CreateOpenedPrefixWithScheduledReverseClose(instance);
        var alice = new RecordingPlayerDriver(request => new PlayerDecision(
            request.DecisionId,
            request.AvailableActions.Any(action => action.ActionKind == ActionKinds.ReverseTravel)
                ? new Intent(ActionKinds.ReverseTravel)
                : new Intent(ActionKinds.ContinueTravel)));
        var journal = FirstBoardScenario.CreateMemoryHistory(initial);
        SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact> kernel =
            FirstBoardScenario.CreateKernel(
                Drivers((BoardIds.Alice, alice)),
                instance,
                journal);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));

        if (responseFirst)
        {
            Assert.Collection(
                Assert.Single(journal.CompletedEvents).Facts,
                fact => Assert.Equal(
                    PassageEncounterResolution.Reversed,
                    Assert.IsType<PassageEncounterResolvedEvent>(
                        Assert.IsType<GameBoardFact>(fact).Value).Resolution),
                fact => Assert.IsType<TraversalReversedFact>(
                    Assert.IsType<SpatialBoardFact>(fact).Value));
            DecisionRequest request = Assert.Single(alice.Requests);
            Assert.Contains(
                request.AvailableActions,
                action => action.ActionKind == ActionKinds.ReverseTravel);
            Assert.Null(kernel.World.Game.PendingEncounter);
            Assert.Equal(
                2,
                SpatialEntity(kernel.World, BoardIds.Alice).MovementGeneration);
        }
        else
        {
            Assert.IsType<ScheduledPassageEntryChangeAppliedFact>(
                Assert.IsType<SpatialBoardFact>(
                    Assert.Single(Assert.Single(journal.CompletedEvents).Facts)).Value);
            Assert.Empty(alice.Requests);
            Assert.NotNull(kernel.World.Game.PendingEncounter);

            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(ContactDue));

            DecisionRequest request = Assert.Single(alice.Requests);
            Assert.DoesNotContain(
                request.AvailableActions,
                action => action.ActionKind == ActionKinds.ReverseTravel);
            PassageEncounterResolvedEvent resolved = Assert.IsType<PassageEncounterResolvedEvent>(
                Assert.IsType<GameBoardFact>(
                    Assert.Single(journal.CompletedEvents[1].Facts)).Value);
            Assert.Equal(PassageEncounterResolution.Continued, resolved.Resolution);
            Assert.Null(kernel.World.Game.PendingEncounter);
            Assert.Equal(
                1,
                SpatialEntity(kernel.World, BoardIds.Alice).MovementGeneration);
        }
    }

    private static (ulong AliceSeed, ulong BobSeed) FindTwoResponderSeeds()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(worldSeed: 0);
        FirstBoardWorld opened = CreateOpenedPrefix(instance);
        var drivers = Drivers(
            (BoardIds.Alice, new RecordingPlayerDriver()),
            (BoardIds.Bob, new RecordingPlayerDriver()));
        var rule = new FirstBoardPassageEncounterResponseRule(instance.Graph, drivers);
        OccurrenceCandidate<BoardCandidate>[] candidates =
        [
            .. rule.Forecast(
                opened,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)),
        ];
        Assert.Equal(2, candidates.Length);
        ulong? aliceSeed = null;
        ulong? bobSeed = null;
        for (ulong seed = 0; seed < 10_000 && (aliceSeed is null || bobSeed is null); seed++)
        {
            PassageEncounterResponseCandidate winner =
                Assert.IsType<PassageEncounterResponseCandidate>(
                    OccurrenceScheduler.SelectWinner(candidates, seed).Data);
            if (winner.RespondingActorId == BoardIds.Alice)
            {
                aliceSeed ??= seed;
            }
            else if (winner.RespondingActorId == BoardIds.Bob)
            {
                bobSeed ??= seed;
            }
        }

        return (
            aliceSeed ?? throw new InvalidOperationException("No Alice response seed was found."),
            bobSeed ?? throw new InvalidOperationException("No Bob response seed was found."));
    }

    private static (ulong FirstSeed, ulong SecondSeed) FindContestSeeds(
        IReadOnlyList<OccurrenceCandidate<BoardCandidate>> candidates,
        Func<BoardCandidate, bool> firstBranch,
        Func<BoardCandidate, bool> secondBranch)
    {
        Assert.Contains(candidates, candidate => firstBranch(candidate.Data));
        Assert.Contains(candidates, candidate => secondBranch(candidate.Data));
        ulong? firstSeed = null;
        ulong? secondSeed = null;
        for (ulong seed = 0; seed < 10_000 && (firstSeed is null || secondSeed is null); seed++)
        {
            BoardCandidate winner = OccurrenceScheduler.SelectWinner(candidates, seed).Data;
            if (firstBranch(winner))
            {
                firstSeed ??= seed;
            }
            else if (secondBranch(winner))
            {
                secondSeed ??= seed;
            }
        }

        return (
            firstSeed ?? throw new InvalidOperationException(
                "No seed selected the first contest branch."),
            secondSeed ?? throw new InvalidOperationException(
                "No seed selected the second contest branch."));
    }

    private static void AssertEncounterRequest(
        DecisionRequest request,
        string actorId,
        string counterpartId,
        string expectedTarget,
        string expectedReverseDestination,
        string? expectedGoal,
        bool reverseAdvertised)
    {
        Assert.Equal(actorId, request.ActorId);
        Assert.Equal(
            $"decision.{actorId}.{(expectedGoal is null ? 1 : 2)}",
            request.DecisionId.Value);
        Assert.Equal(ContactDueTicks, request.ModelTimeMs);
        Assert.Equal(BoardIds.TavernMarketRoad, request.Observation.LocationId);
        Assert.Empty(request.Observation.Exits);
        Assert.Equal([counterpartId], request.Observation.VisibleActorIds);
        Assert.Empty(request.Observation.VisibleObjectIds);
        string[] expectedFactKinds = expectedGoal is null
            ?
            [
                BoardIds.CurrentTravelTarget,
                BoardIds.CurrentTravelReverseDestination,
                BoardIds.CurrentTravelEta,
                BoardIds.PassageContactKindKnown,
                BoardIds.PassageContactCounterpart,
            ]
            :
            [
                BoardIds.CurrentTravelTarget,
                BoardIds.CurrentTravelReverseDestination,
                BoardIds.CurrentTravelEta,
                BoardIds.PassageContactKindKnown,
                BoardIds.PassageContactCounterpart,
                BoardIds.ActiveTravelGoal,
            ];
        Assert.Equal(
            expectedFactKinds,
            request.Observation.KnownFacts.Select(fact => fact.FactKind.Id));
        Assert.Equal(
            expectedTarget,
            request.Observation.KnownFacts.Single(fact =>
                fact.FactKind.Id == BoardIds.CurrentTravelTarget).RelatedId);
        Assert.Equal(
            expectedReverseDestination,
            request.Observation.KnownFacts.Single(fact =>
                fact.FactKind.Id == BoardIds.CurrentTravelReverseDestination).RelatedId);
        Assert.Contains(
            PassageContactKind.HeadOnMeeting.ToString(),
            request.Observation.KnownFacts.Single(fact =>
                fact.FactKind.Id == BoardIds.PassageContactKindKnown).Text,
            StringComparison.Ordinal);
        if (expectedGoal is not null)
        {
            Assert.Equal(
                expectedGoal,
                request.Observation.KnownFacts.Single(fact =>
                    fact.FactKind.Id == BoardIds.ActiveTravelGoal).RelatedId);
        }

        Assert.Equal(
            reverseAdvertised
                ? [ActionKinds.ContinueTravel, ActionKinds.ReverseTravel]
                : [ActionKinds.ContinueTravel],
            request.AvailableActions.Select(action => action.ActionKind));
        Assert.All(request.AvailableActions, action =>
        {
            Assert.Null(action.CandidateActorIds);
            Assert.Null(action.CandidateObjectIds);
            Assert.Null(action.CandidateExitIds);
            Assert.Null(action.CandidateDestinationIds);
        });
        string visibleText = string.Join(" ", request.Observation.KnownFacts.Select(fact => fact.Text));
        Assert.DoesNotContain("offset", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fraction", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("route", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("occupancy", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rank", visibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from", visibleText, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOpeningBatch(OccurrenceEvent<FirstBoardFact> batch)
    {
        Assert.Collection(
            batch.Facts,
            fact => Assert.IsType<PassageContactOccurredFact>(
                Assert.IsType<SpatialBoardFact>(fact).Value),
            fact => Assert.IsType<PassageEncounterOpenedEvent>(
                Assert.IsType<GameBoardFact>(fact).Value));
    }

    private static FirstBoardWorld CreateOpenedPrefix(
        ScenarioInstance instance,
        bool withAliceGoal = false)
    {
        FirstBoardWorld world = CreateTravelingPrefix(instance, withAliceGoal);
        var reducer = new FirstBoardReducer(instance.Graph);
        OccurrenceCandidate<PassageContactOccurrenceData> contact = Assert.Single(
            new SpatialContactOccurrenceRule(instance.Graph).Forecast(
                world.Spatial,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        var instant = new LogicalInstant(contact.Due.ModelTime, 0);
        world = reducer.Apply(
            world,
            instant,
            new SpatialBoardFact(new PassageContactOccurredFact(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        world = reducer.Apply(
            world,
            instant,
            new GameBoardFact(new PassageEncounterOpenedEvent(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        reducer.Validate(world);
        return world;
    }

    private static FirstBoardWorld CreateOpenedPrefixWithScheduledReverseClose(
        ScenarioInstance instance)
    {
        FirstBoardWorld world = CreateTravelingPrefix(instance);
        var reducer = new FirstBoardReducer(instance.Graph);
        var scheduleInstant = new LogicalInstant(ModelTime.Zero, 0);
        world = reducer.Apply(
            world,
            scheduleInstant,
            new SpatialBoardFact(new PassageEntryChangeScheduledFact(
                new PassageId(BoardIds.TavernMarketRoad),
                ContactDue,
                new PassageEntryPatch(enterableFromA: null, enterableFromB: false))));
        OccurrenceCandidate<PassageContactOccurrenceData> contact = Assert.Single(
            new SpatialContactOccurrenceRule(instance.Graph).Forecast(
                world.Spatial,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)));
        var contactInstant = new LogicalInstant(contact.Due.ModelTime, 0);
        world = reducer.Apply(
            world,
            contactInstant,
            new SpatialBoardFact(new PassageContactOccurredFact(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        world = reducer.Apply(
            world,
            contactInstant,
            new GameBoardFact(new PassageEncounterOpenedEvent(
                contact.Data.ContactKey,
                contact.Data.Kind)));
        reducer.Validate(world);
        return world;
    }

    private static ScenarioInstance CreateRoadInstance(ulong worldSeed, long length)
    {
        ScenarioDefinition definition = ScenarioDefinition.Default;
        return new ScenarioInstance(
            definition with
            {
                Passages = Array.AsReadOnly(definition.Passages.Select(passage =>
                    passage.Id == BoardIds.TavernMarketRoad
                        ? passage with { Length = length }
                        : passage).ToArray()),
            },
            worldSeed);
    }

    private static FirstBoardWorld CreateTravelingPrefix(
        ScenarioInstance instance,
        bool withAliceGoal = false,
        bool closeReverseEntry = false,
        long aliceSpeed = BoardTiming.TravelSpeed,
        long bobSpeed = BoardTiming.TravelSpeed)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld world = instance.CreateInitialWorld();
        var instant = new LogicalInstant(ModelTime.Zero, 0);
        if (withAliceGoal)
        {
            world = reducer.Apply(
                world,
                instant,
                new GameBoardFact(new ActorTravelGoalSetEvent(
                    BoardIds.Alice,
                    new PlaceId(BoardIds.Cellar))));
        }

        world = StartTraversal(instance, reducer, world, BoardIds.Alice, instant, aliceSpeed);
        world = StartTraversal(instance, reducer, world, BoardIds.Bob, instant, bobSpeed);
        if (closeReverseEntry)
        {
            world = reducer.Apply(
                world,
                instant,
                new SpatialBoardFact(new PassageEntryAccessChangedFact(
                    new PassageId(BoardIds.TavernMarketRoad),
                    new PassageEntryAccess(EnterableFromA: true, EnterableFromB: false))));
        }

        reducer.Validate(world);
        return world;
    }

    private static FirstBoardWorld CreateThreeActorTravelingPrefix(ScenarioInstance instance)
    {
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        const string charlieId = "charlie";
        BoardActor[] actors =
        [
            .. genesis.Actors,
            new BoardActor(
                Id: 99,
                Key: charlieId,
                Generation: 0,
                DecisionSequence: 0,
                Activity: null,
                TravelGoalPlaceId: null,
                KnownFacts: []),
        ];
        EntityPlacement[] placements =
        [
            .. genesis.Spatial.Entities.Select(entity => new EntityPlacement(
                entity.Id,
                Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId)),
            new(new EntityId(charlieId), new PlaceId(BoardIds.Tavern)),
        ];
        FirstBoardWorld world = new(
            genesis.Game.With(nextPersistentId: 100,
            actors: Array.AsReadOnly(actors.OrderBy(actor => actor.Id).ToArray())),
            GraphSpatialState.Create(instance.Graph, placements));
        var reducer = new FirstBoardReducer(instance.Graph);
        var instant = new LogicalInstant(ModelTime.Zero, 0);
        world = StartTraversal(instance, reducer, world, BoardIds.Alice, instant);
        world = StartTraversal(instance, reducer, world, BoardIds.Bob, instant);
        world = StartTraversal(instance, reducer, world, charlieId, instant);
        reducer.Validate(world);
        Assert.Equal(
            2,
            new SpatialContactOccurrenceRule(instance.Graph).Forecast(
                world.Spatial,
                new SimulationRules(instance.WorldSeed, maxTransitionsPerModelTime: 100)).Count);
        return world;
    }

    private static FirstBoardWorld StartTraversal(
        ScenarioInstance instance,
        FirstBoardReducer reducer,
        FirstBoardWorld world,
        string actorId,
        LogicalInstant instant,
        long speed = BoardTiming.TravelSpeed)
    {
        SpatialPlanAccepted plan = Assert.IsType<SpatialPlanAccepted>(
            new SpatialPlanner(instance.Graph).TryStartTraversal(
                world.Spatial,
                new EntityId(actorId),
                new PassageId(BoardIds.TavernMarketRoad),
                speed,
                instant.ModelTime));
        return plan.Facts.Aggregate(
            world,
            (current, fact) => reducer.Apply(
                current,
                instant,
                new SpatialBoardFact(fact)));
    }

    private static SpatialEntity SpatialEntity(FirstBoardWorld world, string actorId)
    {
        Assert.True(world.Spatial.TryGetEntity(new EntityId(actorId), out SpatialEntity? entity));
        return entity!;
    }

    private static IReadOnlyDictionary<string, IPlayerDriver> Drivers(
        params (string ActorId, IPlayerDriver Driver)[] entries) =>
        entries.ToDictionary(entry => entry.ActorId, entry => entry.Driver, StringComparer.Ordinal);

    private sealed class ThrowingKnowledgeGetter :
        IPlayerSpatialKnowledgeGetter<FirstBoardWorld>
    {
        public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
            FirstBoardWorld committedWorld,
            string subjectId,
            GraphDefinition objectiveGraph) =>
            throw new InvalidOperationException(
                "Encounter response must not call the Player spatial knowledge Getter.");
    }
}
