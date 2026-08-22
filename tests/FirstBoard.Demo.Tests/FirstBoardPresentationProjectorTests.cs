using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class FirstBoardPresentationProjectorTests
{
    private const ulong Seed = 20_260_823;

    [Fact]
    public void ExplicitRegistriesAndDeveloperLaneCoverAllCurrentFactSubtypes()
    {
        Type[] expectedGame =
        [
            .. typeof(BoardEventPayload).Assembly.GetTypes().Where(type =>
                !type.IsAbstract && typeof(BoardEventPayload).IsAssignableFrom(type)),
        ];
        Type[] expectedSpatial =
        [
            .. typeof(GraphSpatialFact).Assembly.GetTypes().Where(type =>
                !type.IsAbstract && typeof(GraphSpatialFact).IsAssignableFrom(type)),
        ];
        Assert.Equal(17, expectedGame.Length);
        Assert.Equal(9, expectedSpatial.Length);
        Assert.Equal(
            expectedGame.OrderBy(type => type.FullName),
            FirstBoardPresentationProjector.KnownGamePayloadTypes.OrderBy(type => type.FullName));
        Assert.Equal(
            expectedSpatial.OrderBy(type => type.FullName),
            FirstBoardPresentationProjector.KnownSpatialPayloadTypes.OrderBy(type => type.FullName));

        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld world = instance.CreateInitialWorld();
        PassageContactKey contact = ContactKey(BoardIds.Alice, BoardIds.Bob);
        const string secret = "DEVELOPER-ONLY-SECRET";
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorTravelStartedEvent(
                BoardIds.Alice,
                BoardIds.TavernMarketRoad,
                BoardIds.Market)),
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Market))),
            new GameBoardFact(new ActorTravelGoalResolvedEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Market),
                TravelGoalResolution.Completed)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                contact,
                PassageContactKind.HeadOnMeeting)),
            new GameBoardFact(new PassageEncounterResolvedEvent(
                contact,
                BoardIds.Alice,
                PassageEncounterResolution.Continued)),
            new GameBoardFact(new TicketConsumedEvent(BoardIds.Alice, BoardIds.SilverCoinOne)),
            new GameBoardFact(new ActorWaitStartedEvent(BoardIds.Alice, new ModelTime(10))),
            new GameBoardFact(new ActorWaitedEvent(BoardIds.Alice)),
            new GameBoardFact(new ActorSpokeEvent(
                BoardIds.Alice,
                BoardIds.Bob,
                "hello",
                SharedFactKind: null)),
            new GameBoardFact(new ActorObservedEvent(
                BoardIds.Bob,
                [new BoardFact("secret.kind", "secret.id", secret)])),
            new GameBoardFact(new ObjectTakenEvent(BoardIds.Bob, BoardIds.BrassKey)),
            new GameBoardFact(new ObjectPlacedEvent(
                BoardIds.Bob,
                BoardIds.BrassKey,
                BoardIds.Market)),
            new GameBoardFact(new ObjectGivenEvent(
                BoardIds.Alice,
                BoardIds.Bob,
                BoardIds.SilverCoinOne)),
            new GameBoardFact(new ObjectShownEvent(
                BoardIds.Alice,
                BoardIds.Bob,
                BoardIds.SilverCoinTwo)),
            new GameBoardFact(new ChestOpenedEvent(
                BoardIds.Alice,
                BoardIds.LockedChest,
                BoardIds.BrassKey)),
            new GameBoardFact(new ActionRejectedEvent(
                BoardIds.Alice,
                new Intent(ActionKinds.Wait),
                "test reason")),
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new EntityPlacedFact(
                new EntityId("sample"),
                new PlaceId(BoardIds.Tavern))),
            new SpatialBoardFact(new EntityRemovedFact(new EntityId("sample"))),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId(BoardIds.Alice),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Tavern),
                1)),
            new SpatialBoardFact(new TraversalReversedFact(new EntityId(BoardIds.Alice), 1)),
            new SpatialBoardFact(new PassageContactOccurredFact(
                contact,
                PassageContactKind.HeadOnMeeting)),
            new SpatialBoardFact(new TraversalArrivedFact(new EntityId(BoardIds.Alice), 1)),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(
                new PassageId(BoardIds.CellarGatePassage),
                new PassageEntryAccess(false, true))),
            new SpatialBoardFact(new PassageEntryChangeScheduledFact(
                new PassageId(BoardIds.CellarGatePassage),
                new ModelTime(10),
                new PassageEntryPatch(false, null))),
            new SpatialBoardFact(new ScheduledPassageEntryChangeAppliedFact(
                new PassageId(BoardIds.CellarGatePassage),
                new ModelTime(10))),
        ];
        Assert.Equal(
            FirstBoardPresentationProjector.KnownGamePayloadTypes.OrderBy(type => type.FullName),
            facts.OfType<GameBoardFact>()
                .Select(fact => fact.Value.GetType())
                .OrderBy(type => type.FullName));
        Assert.Equal(
            FirstBoardPresentationProjector.KnownSpatialPayloadTypes.OrderBy(type => type.FullName),
            facts.OfType<SpatialBoardFact>()
                .Select(fact => fact.Value.GetType())
                .OrderBy(type => type.FullName));
        var projector = new FirstBoardPresentationProjector(instance, humanActorId: null);

        FirstBoardProjection projection = projector.Project(
            world,
            Transition(new LogicalInstant(ModelTime.Zero, 0), facts),
            world);

        Assert.Empty(projection.PlayerCues);
        DeveloperOverlay[] factOverlays =
            [.. projection.DeveloperOverlays.Where(value => value.Code == "developer.fact")];
        Assert.Equal(facts.Length, factOverlays.Length);
        Assert.Contains(factOverlays, overlay => overlay.Text.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void RemoteObservationSecretIsHiddenButDeveloperCanInspectIt()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld pre = instance.CreateInitialWorld();
        const string secret = "BOB-PRIVATE-LETTER-CONTENT";
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorObservedEvent(
                BoardIds.Bob,
                [new BoardFact("private.secret", BoardIds.DuchessLetter, secret)])),
        ];
        LogicalInstant instant = new(new ModelTime(1), 0);
        FirstBoardWorld post = Fold(instance, pre, instant, facts);
        var projector = new FirstBoardPresentationProjector(instance, BoardIds.Alice);

        FirstBoardProjection projection = projector.Project(pre, Transition(instant, facts), post);

        Assert.Empty(projection.PlayerCues);
        Assert.DoesNotContain(
            projection.PlayerCues,
            cue => cue.Text.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(
            projection.DeveloperOverlays,
            overlay => overlay.Text.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void DepartureUsesPreWorldAndArrivalUsesPostWorld()
    {
        ScenarioInstance departureInstance = ScenarioWithActorAt(BoardIds.Bob, BoardIds.Tavern);
        FirstBoardWorld departurePre = departureInstance.CreateInitialWorld();
        LogicalInstant startedAt = new(ModelTime.Zero, 0);
        FirstBoardFact[] departureFacts = TravelStartFacts(BoardIds.Alice);
        FirstBoardWorld departurePost = Fold(
            departureInstance,
            departurePre,
            startedAt,
            departureFacts);
        var departureProjector = new FirstBoardPresentationProjector(
            departureInstance,
            BoardIds.Bob);

        FirstBoardProjection departure = departureProjector.Project(
            departurePre,
            Transition(startedAt, departureFacts),
            departurePost);

        Assert.Single(departure.PlayerCues, cue => cue.Code == "actor.travel-started");
        Assert.DoesNotContain(
            departure.PlayerCues,
            cue => cue.Code == "spatial.traversal-started");

        ScenarioInstance arrivalInstance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld beforeStart = arrivalInstance.CreateInitialWorld();
        FirstBoardWorld arrivalPre = Fold(
            arrivalInstance,
            beforeStart,
            startedAt,
            departureFacts);
        var arrivalProjector = new FirstBoardPresentationProjector(arrivalInstance, BoardIds.Bob);
        FirstBoardProjection remoteDeparture = arrivalProjector.Project(
            beforeStart,
            Transition(startedAt, departureFacts),
            arrivalPre);
        Assert.Empty(remoteDeparture.PlayerCues);

        LogicalInstant arrivedAt = new(new ModelTime(300_000), 0);
        FirstBoardFact[] arrivalFacts =
        [
            new SpatialBoardFact(new TraversalArrivedFact(new EntityId(BoardIds.Alice), 1)),
        ];
        FirstBoardWorld arrivalPost = Fold(
            arrivalInstance,
            arrivalPre,
            arrivedAt,
            arrivalFacts);

        FirstBoardProjection arrival = arrivalProjector.Project(
            arrivalPre,
            Transition(arrivedAt, arrivalFacts),
            arrivalPost);

        PresentationCue cue = Assert.Single(arrival.PlayerCues);
        Assert.Equal("spatial.traversal-arrived", cue.Code);
        Assert.Contains(BoardIds.Market, cue.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PassageContactRequiresExactParticipantAndAtomicOpeningIsNotDuplicated()
    {
        ScenarioInstance instance = ScenarioWithCharlie();
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        LogicalInstant startedAt = new(ModelTime.Zero, 0);
        FirstBoardFact[] starts =
        [
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId(BoardIds.Bob),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Market),
                1)),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId("charlie"),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Tavern),
                1)),
        ];
        FirstBoardWorld pre = Fold(instance, genesis, startedAt, starts);
        PassageContactKey key = ContactKey(BoardIds.Bob, "charlie");
        LogicalInstant contactAt = new(new ModelTime(150_000), 0);
        FirstBoardFact[] contactFacts =
        [
            new SpatialBoardFact(new PassageContactOccurredFact(
                key,
                PassageContactKind.HeadOnMeeting)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                key,
                PassageContactKind.HeadOnMeeting)),
        ];
        FirstBoardWorld post = Fold(instance, pre, contactAt, contactFacts);

        FirstBoardProjection nonParticipant = new FirstBoardPresentationProjector(
            instance,
            BoardIds.Alice).Project(pre, Transition(contactAt, contactFacts), post);
        FirstBoardProjection participant = new FirstBoardPresentationProjector(
            instance,
            BoardIds.Bob).Project(pre, Transition(contactAt, contactFacts), post);

        Assert.Empty(nonParticipant.PlayerCues);
        PresentationCue cue = Assert.Single(participant.PlayerCues);
        Assert.Equal("passage-encounter.opened", cue.Code);
    }

    [Fact]
    public void CellarSealIsHiddenRemotelyVisibleAtEndpointAndNotNarratedAsGlobalBell()
    {
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(
                new PassageId(BoardIds.CellarGatePassage),
                new PassageEntryAccess(false, true))),
        ];
        LogicalInstant instant = new(new ModelTime(BoardTiming.DeadlineTicks), 0);

        ScenarioInstance remoteInstance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld remotePre = remoteInstance.CreateInitialWorld();
        FirstBoardWorld remotePost = Fold(remoteInstance, remotePre, instant, facts);
        FirstBoardProjection remote = new FirstBoardPresentationProjector(
            remoteInstance,
            BoardIds.Alice).Project(remotePre, Transition(instant, facts), remotePost);
        Assert.Empty(remote.PlayerCues);

        ScenarioInstance localInstance = ScenarioWithActorAt(BoardIds.Alice, BoardIds.CellarGate);
        FirstBoardWorld localPre = localInstance.CreateInitialWorld();
        FirstBoardWorld localPost = Fold(localInstance, localPre, instant, facts);
        FirstBoardProjection local = new FirstBoardPresentationProjector(
            localInstance,
            BoardIds.Alice).Project(localPre, Transition(instant, facts), localPost);

        PresentationCue cue = Assert.Single(local.PlayerCues);
        Assert.Equal("cellar.sealed", cue.Code);
        Assert.DoesNotContain("bell", cue.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObjectAndReverseAtomicGroupsEmitOnePrimaryCueEach()
    {
        ScenarioInstance objectInstance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld takePre = objectInstance.CreateInitialWorld();
        LogicalInstant takeAt = new(new ModelTime(1), 0);
        FirstBoardFact[] takeFacts =
        [
            new GameBoardFact(new ObjectTakenEvent(BoardIds.Bob, BoardIds.BrassKey)),
            new SpatialBoardFact(new EntityRemovedFact(new EntityId(BoardIds.BrassKey))),
        ];
        FirstBoardWorld takePost = Fold(objectInstance, takePre, takeAt, takeFacts);
        var objectProjector = new FirstBoardPresentationProjector(objectInstance, BoardIds.Bob);
        FirstBoardProjection take = objectProjector.Project(
            takePre,
            Transition(takeAt, takeFacts),
            takePost);
        Assert.Single(take.PlayerCues, cue => cue.Code == "object.taken");
        Assert.DoesNotContain(take.PlayerCues, cue => cue.Code == "spatial.entity-removed");

        LogicalInstant putAt = new(new ModelTime(2), 0);
        FirstBoardFact[] putFacts =
        [
            new GameBoardFact(new ObjectPlacedEvent(
                BoardIds.Bob,
                BoardIds.BrassKey,
                BoardIds.Market)),
            new SpatialBoardFact(new EntityPlacedFact(
                new EntityId(BoardIds.BrassKey),
                new PlaceId(BoardIds.Market))),
        ];
        FirstBoardWorld putPost = Fold(objectInstance, takePost, putAt, putFacts);
        FirstBoardProjection put = objectProjector.Project(
            takePost,
            Transition(putAt, putFacts),
            putPost);
        Assert.Single(put.PlayerCues, cue => cue.Code == "object.placed");
        Assert.DoesNotContain(put.PlayerCues, cue => cue.Code == "spatial.entity-placed");

        EncounterWorld encounter = CreateBobCharlieEncounter();
        FirstBoardFact[] reverseFacts =
        [
            new GameBoardFact(new PassageEncounterResolvedEvent(
                encounter.ContactKey,
                BoardIds.Bob,
                PassageEncounterResolution.Reversed)),
            new SpatialBoardFact(new TraversalReversedFact(new EntityId(BoardIds.Bob), 1)),
        ];
        FirstBoardWorld reversePost = Fold(
            encounter.Instance,
            encounter.World,
            encounter.Instant,
            reverseFacts);
        FirstBoardProjection reverse = new FirstBoardPresentationProjector(
            encounter.Instance,
            "charlie").Project(
                encounter.World,
                Transition(encounter.Instant, reverseFacts),
                reversePost);
        Assert.Single(reverse.PlayerCues, cue => cue.Code == "passage-encounter.resolved");
        Assert.DoesNotContain(
            reverse.PlayerCues,
            cue => cue.Code == "spatial.traversal-reversed");
    }

    [Fact]
    public void TravelGoalAndTraversalStartAreOnePrimaryCueAndProjectionDoesNotMutateWorlds()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld pre = instance.CreateInitialWorld();
        string preSnapshot = FirstBoardScenario.WorldSnapshot(pre);
        LogicalInstant instant = new(ModelTime.Zero, 0);
        FirstBoardFact[] facts =
        [
            new GameBoardFact(new ActorTravelGoalSetEvent(
                BoardIds.Alice,
                new PlaceId(BoardIds.Market))),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId(BoardIds.Alice),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Tavern),
                1)),
        ];
        FirstBoardWorld post = Fold(instance, pre, instant, facts);
        string postSnapshot = FirstBoardScenario.WorldSnapshot(post);
        var projector = new FirstBoardPresentationProjector(instance, BoardIds.Alice);

        FirstBoardProjection projection = projector.Project(pre, Transition(instant, facts), post);

        Assert.Single(projection.PlayerCues, cue => cue.Code == "actor.travel-goal-set");
        Assert.DoesNotContain(
            projection.PlayerCues,
            cue => cue.Code == "spatial.traversal-started");
        Assert.Equal(preSnapshot, FirstBoardScenario.WorldSnapshot(pre));
        Assert.Equal(postSnapshot, FirstBoardScenario.WorldSnapshot(post));
    }

    [Fact]
    public void UnknownPayloadDefaultsToHiddenPlayerLane()
    {
        ScenarioInstance instance = ScenarioInstance.CreateDefault(Seed);
        FirstBoardWorld world = instance.CreateInitialWorld();
        FirstBoardFact[] facts = [new GameBoardFact(new UnknownGamePayload())];

        FirstBoardProjection projection = new FirstBoardPresentationProjector(
            instance,
            BoardIds.Alice).Project(
                world,
                Transition(new LogicalInstant(ModelTime.Zero, 0), facts),
                world);

        Assert.Empty(projection.PlayerCues);
        Assert.Contains(
            projection.DeveloperOverlays,
            overlay => overlay.Text.Contains(nameof(UnknownGamePayload), StringComparison.Ordinal));
    }

    private static FirstBoardFact[] TravelStartFacts(string actorId) =>
    [
        new GameBoardFact(new ActorTravelStartedEvent(
            actorId,
            BoardIds.TavernMarketRoad,
            BoardIds.Market)),
        new SpatialBoardFact(new TraversalStartedFact(
            new EntityId(actorId),
            new PassageId(BoardIds.TavernMarketRoad),
            new PlaceId(BoardIds.Tavern),
            1)),
    ];

    private static EncounterWorld CreateBobCharlieEncounter()
    {
        ScenarioInstance instance = ScenarioWithCharlie();
        FirstBoardWorld genesis = instance.CreateInitialWorld();
        LogicalInstant startedAt = new(ModelTime.Zero, 0);
        FirstBoardFact[] starts =
        [
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId(BoardIds.Bob),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Market),
                1)),
            new SpatialBoardFact(new TraversalStartedFact(
                new EntityId("charlie"),
                new PassageId(BoardIds.TavernMarketRoad),
                new PlaceId(BoardIds.Tavern),
                1)),
        ];
        FirstBoardWorld traversing = Fold(instance, genesis, startedAt, starts);
        PassageContactKey key = ContactKey(BoardIds.Bob, "charlie");
        LogicalInstant contactAt = new(new ModelTime(150_000), 0);
        FirstBoardFact[] contactFacts =
        [
            new SpatialBoardFact(new PassageContactOccurredFact(
                key,
                PassageContactKind.HeadOnMeeting)),
            new GameBoardFact(new PassageEncounterOpenedEvent(
                key,
                PassageContactKind.HeadOnMeeting)),
        ];
        FirstBoardWorld contact = Fold(instance, traversing, contactAt, contactFacts);
        return new EncounterWorld(instance, contact, key, contactAt);
    }

    private static ScenarioInstance ScenarioWithActorAt(string actorId, string placeId)
    {
        ScenarioDefinition definition = ScenarioDefinition.Default with
        {
            Actors = Array.AsReadOnly(
                ScenarioDefinition.Default.Actors
                    .Select(actor => actor.Id == actorId
                        ? actor with { InitialPlaceId = placeId }
                        : actor)
                    .ToArray()),
        };
        return new ScenarioInstance(definition, Seed);
    }

    private static ScenarioInstance ScenarioWithCharlie()
    {
        ScenarioRoleDefinition role = ScenarioDefinition.Default.Actor(BoardIds.Bob).Role;
        ScenarioDefinition definition = ScenarioDefinition.Default with
        {
            Actors = Array.AsReadOnly(
            [
                .. ScenarioDefinition.Default.Actors,
                new ScenarioActorDefinition("charlie", BoardIds.Tavern, role),
            ]),
        };
        return new ScenarioInstance(definition, Seed);
    }

    private static FirstBoardWorld Fold(
        ScenarioInstance instance,
        FirstBoardWorld pre,
        LogicalInstant instant,
        IReadOnlyList<FirstBoardFact> facts)
    {
        var reducer = new FirstBoardReducer(instance.Graph);
        FirstBoardWorld post = pre;
        foreach (FirstBoardFact fact in facts)
        {
            post = reducer.Apply(post, instant, fact);
        }

        reducer.Validate(post);
        return post;
    }

    private static CommittedTransition Transition(
        LogicalInstant instant,
        IReadOnlyList<FirstBoardFact> facts) =>
        new(
            new WorldVersion(FirstBoardScenario.LineageId, 1),
            new JournalBatch<FirstBoardFact>(
                instant,
                CandidateKey.FromUtf8("projector/test"),
                facts));

    private static PassageContactKey ContactKey(string entityA, string entityB) =>
        new(
            new PassageId(BoardIds.TavernMarketRoad),
            new EntityId(entityA),
            1,
            new EntityId(entityB),
            1);

    private sealed record UnknownGamePayload : BoardEventPayload;

    private sealed record EncounterWorld(
        ScenarioInstance Instance,
        FirstBoardWorld World,
        PassageContactKey ContactKey,
        LogicalInstant Instant);
}
