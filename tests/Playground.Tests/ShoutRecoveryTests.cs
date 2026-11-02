using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.FollowingTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>One shout publishes all personal deliveries together, including at real disk boundaries.</summary>
public sealed class ShoutRecoveryTests {
    private const string Words = "ColdWind\nTerminal\n集合：\"溪边\" **这是角色的话** 🌲";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_participant_shout_cold_opens_and_recovers_without_planning_or_duplicate_delivery(bool interruptAfterEvent) {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = PreparedWorld();
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Shout(Words)));
        await UntilFact(baseline, IsShout);
        AssertDelivery(initial, baseline.World, Last(baseline));

        PlaygroundWorld persisted = initial;
        KernelCursor cursor = InitialCursor(initial);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, cursor)) {
            IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> writable = interruptAfterEvent
                ? new StopAfterEvent(history, IsShout) : history;
            var kernel = PlaygroundEngine.CreateKernel(writable, characterController: Commands(PlaygroundCommand.Shout(Words)));
            for (int step = 0; step < 20; step++) {
                persisted = history.State;
                cursor = history.Cursor;
                try { await Advance(kernel); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
                if (kernel.IsFaulted || IsShout(Last(kernel))) { break; }
            }
            if (interruptAfterEvent) {
                Assert.True(kernel.IsFaulted);
                Assert.Same(persisted, history.State);
                Assert.NotNull(history.PendingEvent);
                Assert.Equal(MaterialSnapshot(baseline.LastCompletion!.Event), MaterialSnapshot(history.PendingEvent));
                Assert.All(history.State.Characters, actor => Assert.Equal(0, actor.Communication.SentCount));
                Assert.All(history.State.Characters, actor => Assert.Empty(ShoutExperiences(history.State, actor.Id)));
            } else {
                Assert.False(kernel.IsFaulted);
                Assert.Null(history.PendingEvent);
                Assert.Equal(baseline.Cursor, kernel.Cursor);
                AssertSameWorld(baseline.World, kernel.World);
                persisted = history.State;
                cursor = history.Cursor;
            }
        }

        string materials;
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(cursor, history.Cursor);
            AssertSameWorld(persisted, history.State);
            // No controller is installed. Both discovery and planning are forbidden during replay.
            var recovered = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                PlaygroundEngine.Validate);
            Assert.Equal(interruptAfterEvent, recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Null(history.PendingEvent);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
            AssertSameWorld(baseline.World, recovered.World);
            AssertDelivery(initial, recovered.World, Last(baseline));
            Assert.Single(history.ReadCommittedOperations([Last(baseline).OperationRecordId!]));
            materials = MaterialSnapshot(recovered.World.Business.InteractionStates);
        }

        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        var forbidden = new ForbiddenController();
        var cold = PlaygroundEngine.CreateKernel(reopened, characterController: forbidden);
        Assert.False(cold.RecoverPending());
        Assert.False(cold.RecoverPending());
        Assert.Equal(0, forbidden.Calls);
        Assert.Equal(baseline.Cursor, cold.Cursor);
        AssertSameWorld(baseline.World, cold.World);
        Assert.Equal(materials, MaterialSnapshot(cold.World.Business.InteractionStates));
        AssertDelivery(initial, cold.World, Last(baseline));
        Assert.Single(reopened.ReadCommittedOperations([Last(baseline).OperationRecordId!]));
    }

    [Fact]
    public async Task Shout_to_closed_waiter_and_follower_is_delivered_at_their_next_opening_without_replacing_activity() {
        PlaygroundWorld initial = PlaceBAtStream(World());
        var script = new Script(
            [PlaygroundCommand.Handoff("sender-private-draft"), PlaygroundCommand.Shout(Words)],
            [PlaygroundCommand.Wait(6), PlaygroundCommand.Continue()],
            [PlaygroundCommand.Follow("a"), PlaygroundCommand.Continue()]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.FollowStarted);
        PlaygroundActivity waiting = kernel.World.GetCharacter("b").Activity!;
        var waitAdvance = Interaction(kernel.World, "b").PendingAdvance!;
        var followAdvance = Interaction(kernel.World, "c").PendingAdvance!;
        await UntilFact(kernel, IsShout);

        foreach (string actor in new[] { "b", "c" }) {
            CharacterInteractionState state = Interaction(kernel.World, actor);
            Assert.Equal(CharacterContextStatus.Closed, state.CurrentContext!.Status);
            Assert.Single(state.UndeliveredExperiences, experience => experience.Event.Kind == PlaygroundObservedEventKind.SpeechShouted);
            Assert.True(kernel.World.GetCharacter(actor).Communication.AttentionPending);
            Assert.DoesNotContain("sender-private-draft", MaterialSnapshot(state), StringComparison.Ordinal);
        }
        Assert.Equal(waiting, kernel.World.GetCharacter("b").Activity);
        Assert.Equal(MaterialSnapshot(waitAdvance), MaterialSnapshot(Interaction(kernel.World, "b").PendingAdvance));
        Assert.Equal(MaterialSnapshot(followAdvance), MaterialSnapshot(Interaction(kernel.World, "c").PendingAdvance));

        var opened = new HashSet<string>(StringComparer.Ordinal);
        for (int step = 0; step < 12 && opened.Count < 2; step++) {
            await Advance(kernel);
            PlaygroundFact fact = Last(kernel);
            if (fact.Kind != PlaygroundEventKind.DecisionContextOpened || fact.ActorId is not ("b" or "c")) { continue; }
            string actor = fact.ActorId;
            opened.Add(actor);
            CharacterContext context = Interaction(kernel.World, actor).CurrentContext!;
            Assert.Equal(2, context.ContextSequence);
            Assert.Equal(PlaygroundOutcomeActivityState.Ongoing, context.Opening.ActivityState);
            Assert.Equal(actor == "b" ? PlaygroundCommandKind.Wait : PlaygroundCommandKind.Follow, context.Opening.PreviousAdvance!.Command.Kind);
            AssertSigned(Assert.Single(context.Opening.Experiences,
                experience => experience.Event.Kind == PlaygroundObservedEventKind.SpeechShouted).Event, actor);
            Assert.Empty(Interaction(kernel.World, actor).UndeliveredExperiences);
        }
        Assert.Equal(new[] { "b", "c" }, opened.Order(StringComparer.Ordinal));
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(waiting, kernel.World.GetCharacter("b").Activity);
        Assert.Equal(Hour(14), kernel.World.GetCharacter("b").Activity!.Due);
        Assert.Equal("a", kernel.World.GetCharacter("c").FollowingLeaderId);
    }

    [Fact]
    public async Task Real_same_instant_arrival_commits_before_shout_input_selects_its_receivers() {
        PlaygroundWorld initial = World();
        var history = Memory(initial);
        var script = new Script(
            [PlaygroundCommand.Wait(1), PlaygroundCommand.Shout(Words)],
            [PlaygroundCommand.Go("trail-1")],
            [PlaygroundCommand.Wait(2)]);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.MoveStarted);
        Assert.IsType<TraversingLocation>(PlaygroundActions.Location(kernel.World, "b"));
        var queries = new SpatialQueries(PlaygroundExpeditionMap.Graph);
        Assert.DoesNotContain(new EntityId("b"), queries.GetEntitiesInOneHop(kernel.World.Expedition!.Spatial,
            new("camp"), PlaceRelationCapabilities.Sound));
        await UntilFact(kernel, IsShout);

        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(PlaygroundActions.Location(kernel.World, "b")).PlaceId.Value);
        PlaygroundFact[] trace = history.CompletedEvents.SelectMany(entry => entry.Facts).ToArray();
        int arrived = Array.FindIndex(trace, fact => fact.Kind == PlaygroundEventKind.MoveArrived && fact.ActorId == "b");
        int shouted = Array.FindIndex(trace, IsShout);
        Assert.True(arrived >= 0 && arrived < shouted);
        Assert.Equal(Hour(9), trace[arrived].At);
        Assert.Equal(trace[arrived].At, trace[shouted].At);
        Assert.Equal(new[] { "a", "b", "c" }, trace[shouted].ChangedCharacters.Select(actor => actor.Id));
        AssertSigned(Assert.Single(ShoutExperiences(kernel.World, "b")).Event, "b");
        Assert.Equal(1, script.Calls("b"));
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
    }

    private static bool IsShout(PlaygroundFact fact) => fact.Kind == PlaygroundEventKind.SpeechShouted;

    private static PlaygroundWorld PreparedWorld() {
        PlaygroundWorld initial = PlaceBAtStream(World());
        var body = new PlaygroundSurvival(Hour(8), 60 * PlaygroundSurvival.Scale, 20 * PlaygroundSurvival.Scale, 60 * PlaygroundSurvival.Scale,
            Rate(0), Rate(0), Rate(0), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        var notice = new PlaygroundNotice(PlaygroundNoticeKind.CollectorJoined, "water", "a", "fixture-existing-notice", Hour(8), "existing notice");
        var wait = new PlaygroundActivity(PlaygroundActivityKind.Wait, 1, Hour(8), 6 * PlaygroundTime.Hour, Hour(14));
        PlaygroundCharacter[] actors = [
            new("a", "阿青", survival: body, communication: new(Hour(8), 0, true)),
            new("b", "阿白", activity: wait, notices: [notice], nextActivityId: 2,
                survival: body, communication: new(Hour(8), 0, true)),
            new("c", "阿岚", survival: body, communication: new(Hour(8), 0, true), followingLeaderId: "a")
        ];
        PlaygroundWorld world = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: actors), initial.Time);
        ValidateRuntime(world);
        return world;
    }

    private static PlaygroundWorld PlaceBAtStream(PlaygroundWorld initial) {
        PlaygroundExpedition expedition = initial.Expedition!;
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [
            new(new("a"), new("camp")), new(new("b"), new("stream")), new(new("c"), new("camp"))
        ]);
        return PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time,
            expedition: new(expedition.ScenarioId, spatial, expedition.Visits)), initial.Time);
    }

    private static void AssertDelivery(PlaygroundWorld before, PlaygroundWorld after, PlaygroundFact fact) {
        Assert.Equal(Hour(8), after.Time);
        Assert.Equal("a", fact.ActorId);
        Assert.Null(fact.TargetActorId);
        Assert.Equal(Words, fact.SpeechText);
        Assert.Equal(new[] { "a", "b", "c" }, fact.ChangedCharacters.Select(actor => actor.Id));
        Assert.Equal(3, fact.ObservedEvents.Count(entry => entry.Kind == PlaygroundObservedEventKind.SpeechShouted));
        Assert.Equal(1, after.GetCharacter("a").Communication.SentCount);
        Assert.Equal(0, after.GetCharacter("b").Communication.SentCount);
        Assert.Equal(0, after.GetCharacter("c").Communication.SentCount);
        Assert.Equal(MaterialSnapshot(before.Expedition!.Visits), MaterialSnapshot(after.Expedition!.Visits));
        Assert.Equal(before.Expedition.Spatial, after.Expedition.Spatial);
        foreach (string actor in new[] { "a", "b", "c" }) {
            PlaygroundCharacter previous = before.GetCharacter(actor);
            PlaygroundCharacter current = after.GetCharacter(actor);
            Assert.True(current.Communication.AttentionPending);
            Assert.Equal(MaterialSnapshot(previous.Activity), MaterialSnapshot(current.Activity));
            Assert.Equal(MaterialSnapshot(previous.Notices), MaterialSnapshot(current.Notices));
            Assert.Equal(MaterialSnapshot(previous.Survival), MaterialSnapshot(current.Survival));
            Assert.Equal(MaterialSnapshot(previous.Backpack), MaterialSnapshot(current.Backpack));
            Assert.Equal(previous.FollowingLeaderId, current.FollowingLeaderId);
            AssertSigned(Assert.Single(ShoutExperiences(after, actor)).Event, actor);
            PlaygroundObservedEvent recorded = Assert.Single(fact.ObservedEvents,
                entry => entry.ObserverId == actor && entry.Kind == PlaygroundObservedEventKind.SpeechShouted);
            Assert.Equal(Words, recorded.Detail);
            Assert.DoesNotContain(recorded.Effects, effect => effect.Key.Category is "location" or "memory");
        }
        Assert.Equal(CharacterContextStatus.Open, Interaction(after, "a").CurrentContext!.Status);
        Assert.All(new[] { "b", "c" }, actor => Assert.Null(Interaction(after, actor).CurrentContext));
        ValidateRuntime(after);
    }

    private static IEnumerable<CharacterExperience> ShoutExperiences(PlaygroundWorld world, string actor) {
        CharacterInteractionState interaction = Interaction(world, actor);
        IEnumerable<CharacterExperience> experiences = interaction.UndeliveredExperiences;
        if (interaction.CurrentContext is { } context) {
            experiences = experiences.Concat(context.Opening.Experiences).Concat(context.Items.SelectMany(item => item.Experiences));
        }
        return experiences.Where(experience => experience.Event.Kind == PlaygroundObservedEventKind.SpeechShouted);
    }

    private static void AssertSigned(PlaygroundObservedEvent entry, string observer) {
        Assert.Equal(observer, entry.ObserverId);
        Assert.Equal(0, entry.EventOrdinal);
        Assert.Equal("a", entry.SubjectId);
        Assert.Equal("阿青", entry.SubjectName);
        Assert.Equal(Words, entry.Detail);
        Assert.Null(entry.Communication);
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending shout recovery must not discover or request a controller.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending shout recovery must apply recorded deliveries without planning again.");
    }
}
