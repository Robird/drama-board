using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;
using static DramaBoard.ResourceCycle.Tests.FollowingTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>One shout publishes all personal deliveries together, including at real disk boundaries.</summary>
public sealed class ShoutRecoveryTests {
    private const string Words = "ColdWind\nTerminal\n集合：\"溪边\" **这是角色的话** 🌲";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Three_participant_shout_cold_opens_and_recovers_without_planning_or_duplicate_delivery(bool interruptAfterEvent) {
        using var store = new TemporaryStore();
        CycleWorld initial = PreparedWorld();
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Shout(Words)));
        await UntilFact(baseline, IsShout);
        AssertDelivery(initial, baseline.World, Last(baseline));

        CycleWorld persisted = initial;
        KernelCursor cursor = InitialCursor(initial);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, cursor)) {
            IOccurrenceHistory<CycleWorld, CycleFact> writable = interruptAfterEvent
                ? new StopAfterEvent(history, IsShout) : history;
            var kernel = CycleEngine.CreateKernel(writable, characterController: Commands(CycleCommand.Shout(Words)));
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
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(cursor, history.Cursor);
            AssertSameWorld(persisted, history.State);
            // No controller is installed. Both discovery and planning are forbidden during replay.
            var recovered = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(history,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental),
                CycleEngine.Validate);
            Assert.Equal(interruptAfterEvent, recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Null(history.PendingEvent);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
            AssertSameWorld(baseline.World, recovered.World);
            AssertDelivery(initial, recovered.World, Last(baseline));
            Assert.Single(history.ReadCommittedOperations([Last(baseline).OperationRecordId!]));
            materials = MaterialSnapshot(recovered.World.Business.InteractionStates);
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        var forbidden = new ForbiddenController();
        var cold = CycleEngine.CreateKernel(reopened, characterController: forbidden);
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
        CycleWorld initial = PlaceBAtStream(World());
        var script = new Script(
            [CycleCommand.Handoff("sender-private-draft"), CycleCommand.Shout(Words)],
            [CycleCommand.Wait(6), CycleCommand.Continue()],
            [CycleCommand.Follow("a"), CycleCommand.Continue()]);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: script);
        await UntilFact(kernel, fact => fact.Kind == CycleEventKind.FollowStarted);
        CycleActivity waiting = kernel.World.GetCharacter("b").Activity!;
        var waitAdvance = Interaction(kernel.World, "b").PendingAdvance!;
        var followAdvance = Interaction(kernel.World, "c").PendingAdvance!;
        await UntilFact(kernel, IsShout);

        foreach (string actor in new[] { "b", "c" }) {
            CharacterInteractionState state = Interaction(kernel.World, actor);
            Assert.Equal(CharacterContextStatus.Closed, state.CurrentContext!.Status);
            Assert.Single(state.UndeliveredExperiences, experience => experience.Event.Kind == CycleObservedEventKind.SpeechShouted);
            Assert.True(kernel.World.GetCharacter(actor).Communication.AttentionPending);
            Assert.DoesNotContain("sender-private-draft", MaterialSnapshot(state), StringComparison.Ordinal);
        }
        Assert.Equal(waiting, kernel.World.GetCharacter("b").Activity);
        Assert.Equal(MaterialSnapshot(waitAdvance), MaterialSnapshot(Interaction(kernel.World, "b").PendingAdvance));
        Assert.Equal(MaterialSnapshot(followAdvance), MaterialSnapshot(Interaction(kernel.World, "c").PendingAdvance));

        var opened = new HashSet<string>(StringComparer.Ordinal);
        for (int step = 0; step < 12 && opened.Count < 2; step++) {
            await Advance(kernel);
            CycleFact fact = Last(kernel);
            if (fact.Kind != CycleEventKind.DecisionContextOpened || fact.ActorId is not ("b" or "c")) { continue; }
            string actor = fact.ActorId;
            opened.Add(actor);
            CharacterContext context = Interaction(kernel.World, actor).CurrentContext!;
            Assert.Equal(2, context.ContextSequence);
            Assert.Equal(CycleOutcomeActivityState.Ongoing, context.Opening.ActivityState);
            Assert.Equal(actor == "b" ? CycleCommandKind.Wait : CycleCommandKind.Follow, context.Opening.PreviousAdvance!.Command.Kind);
            AssertSigned(Assert.Single(context.Opening.Experiences,
                experience => experience.Event.Kind == CycleObservedEventKind.SpeechShouted).Event, actor);
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
        CycleWorld initial = World();
        var history = Memory(initial);
        var script = new Script(
            [CycleCommand.Wait(1), CycleCommand.Shout(Words)],
            [CycleCommand.Go("trail-1")],
            [CycleCommand.Wait(2)]);
        var kernel = CycleEngine.CreateKernel(history, characterController: script);
        await UntilFact(kernel, fact => fact.Kind == CycleEventKind.MoveStarted);
        Assert.IsType<TraversingLocation>(CycleActions.Location(kernel.World, "b"));
        var queries = new SpatialQueries(CycleExpeditionMap.Graph);
        Assert.DoesNotContain(new EntityId("b"), queries.GetEntitiesInOneHop(kernel.World.Expedition!.Spatial,
            new("camp"), PlaceRelationCapabilities.Sound));
        await UntilFact(kernel, IsShout);

        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(CycleActions.Location(kernel.World, "b")).PlaceId.Value);
        CycleFact[] trace = history.CompletedEvents.SelectMany(entry => entry.Facts).ToArray();
        int arrived = Array.FindIndex(trace, fact => fact.Kind == CycleEventKind.MoveArrived && fact.ActorId == "b");
        int shouted = Array.FindIndex(trace, IsShout);
        Assert.True(arrived >= 0 && arrived < shouted);
        Assert.Equal(Hour(9), trace[arrived].At);
        Assert.Equal(trace[arrived].At, trace[shouted].At);
        Assert.Equal(new[] { "a", "b", "c" }, trace[shouted].ChangedCharacters.Select(actor => actor.Id));
        AssertSigned(Assert.Single(ShoutExperiences(kernel.World, "b")).Event, "b");
        Assert.Equal(1, script.Calls("b"));
        Assert.Equal(1, kernel.World.GetCharacter("a").Communication.SentCount);
    }

    private static bool IsShout(CycleFact fact) => fact.Kind == CycleEventKind.SpeechShouted;

    private static CycleWorld PreparedWorld() {
        CycleWorld initial = PlaceBAtStream(World());
        var body = new CycleSurvival(Hour(8), 60 * CycleSurvival.Scale, 20 * CycleSurvival.Scale, 60 * CycleSurvival.Scale,
            Rate(0), Rate(0), Rate(0), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
        var notice = new CycleNotice(CycleNoticeKind.CollectorJoined, "water", "a", "fixture-existing-notice", Hour(8), "existing notice");
        var wait = new CycleActivity(CycleActivityKind.Wait, 1, Hour(8), 6 * CycleTime.Hour, Hour(14));
        CycleCharacter[] actors = [
            new("a", "阿青", survival: body, communication: new(Hour(8), 0, true)),
            new("b", "阿白", activity: wait, notices: [notice], nextActivityId: 2,
                survival: body, communication: new(Hour(8), 0, true)),
            new("c", "阿岚", survival: body, communication: new(Hour(8), 0, true), followingLeaderId: "a")
        ];
        CycleWorld world = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: actors), initial.Time);
        ValidateRuntime(world);
        return world;
    }

    private static CycleWorld PlaceBAtStream(CycleWorld initial) {
        CycleExpedition expedition = initial.Expedition!;
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [
            new(new("a"), new("camp")), new(new("b"), new("stream")), new(new("c"), new("camp"))
        ]);
        return CycleEngine.Rebuild(initial.Business.Replace(initial.Time,
            expedition: new(expedition.ScenarioId, spatial, expedition.Visits)), initial.Time);
    }

    private static void AssertDelivery(CycleWorld before, CycleWorld after, CycleFact fact) {
        Assert.Equal(Hour(8), after.Time);
        Assert.Equal("a", fact.ActorId);
        Assert.Null(fact.TargetActorId);
        Assert.Equal(Words, fact.SpeechText);
        Assert.Equal(new[] { "a", "b", "c" }, fact.ChangedCharacters.Select(actor => actor.Id));
        Assert.Equal(3, fact.ObservedEvents.Count(entry => entry.Kind == CycleObservedEventKind.SpeechShouted));
        Assert.Equal(1, after.GetCharacter("a").Communication.SentCount);
        Assert.Equal(0, after.GetCharacter("b").Communication.SentCount);
        Assert.Equal(0, after.GetCharacter("c").Communication.SentCount);
        Assert.Equal(MaterialSnapshot(before.Expedition!.Visits), MaterialSnapshot(after.Expedition!.Visits));
        Assert.Equal(before.Expedition.Spatial, after.Expedition.Spatial);
        foreach (string actor in new[] { "a", "b", "c" }) {
            CycleCharacter previous = before.GetCharacter(actor);
            CycleCharacter current = after.GetCharacter(actor);
            Assert.True(current.Communication.AttentionPending);
            Assert.Equal(MaterialSnapshot(previous.Activity), MaterialSnapshot(current.Activity));
            Assert.Equal(MaterialSnapshot(previous.Notices), MaterialSnapshot(current.Notices));
            Assert.Equal(MaterialSnapshot(previous.Survival), MaterialSnapshot(current.Survival));
            Assert.Equal(MaterialSnapshot(previous.Backpack), MaterialSnapshot(current.Backpack));
            Assert.Equal(previous.FollowingLeaderId, current.FollowingLeaderId);
            AssertSigned(Assert.Single(ShoutExperiences(after, actor)).Event, actor);
            CycleObservedEvent recorded = Assert.Single(fact.ObservedEvents,
                entry => entry.ObserverId == actor && entry.Kind == CycleObservedEventKind.SpeechShouted);
            Assert.Equal(Words, recorded.Detail);
            Assert.DoesNotContain(recorded.Effects, effect => effect.Key.Category is "location" or "memory");
        }
        Assert.Equal(CharacterContextStatus.Open, Interaction(after, "a").CurrentContext!.Status);
        Assert.All(new[] { "b", "c" }, actor => Assert.Null(Interaction(after, actor).CurrentContext));
        ValidateRuntime(after);
    }

    private static IEnumerable<CharacterExperience> ShoutExperiences(CycleWorld world, string actor) {
        CharacterInteractionState interaction = Interaction(world, actor);
        IEnumerable<CharacterExperience> experiences = interaction.UndeliveredExperiences;
        if (interaction.CurrentContext is { } context) {
            experiences = experiences.Concat(context.Opening.Experiences).Concat(context.Items.SelectMany(item => item.Experiences));
        }
        return experiences.Where(experience => experience.Event.Kind == CycleObservedEventKind.SpeechShouted);
    }

    private static void AssertSigned(CycleObservedEvent entry, string observer) {
        Assert.Equal(observer, entry.ObserverId);
        Assert.Equal(0, entry.EventOrdinal);
        Assert.Equal("a", entry.SubjectId);
        Assert.Equal("阿青", entry.SubjectName);
        Assert.Equal(Words, entry.Detail);
        Assert.Null(entry.Communication);
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending shout recovery must not discover or request a controller.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending shout recovery must apply recorded deliveries without planning again.");
    }
}
