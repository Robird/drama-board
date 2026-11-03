using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class ExpeditionTests {
    [Fact]
    public void Initial_observation_and_command_checks_do_not_disclose_remote_resources_or_exits() {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(initial.World, "a");
        string before = initial.World.DescribeState();
        Assert.Empty(observation.Resources);
        string serialized = JsonSerializer.Serialize(observation,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        foreach (PlaygroundResource remote in initial.World.Resources) {
            Assert.DoesNotContain(remote.Id, serialized, StringComparison.Ordinal);
            Assert.Equal(PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Gather("does-not-exist")), PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Gather(remote.Id)));
            Assert.Equal(PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Gather("does-not-exist")),
                PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Gather(remote.Id)));
        }
        Assert.DoesNotContain(PlaygroundExpeditionMap.GetPlace("road").Name, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("巡山信号站", serialized, StringComparison.Ordinal);
        Assert.NotNull(PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Go(Passage("ravine", "road"))));
        Assert.Null(PlaygroundActions.Check(initial.World, "a", PlaygroundCommand.Go(Passage("camp", "stream"))));
        Assert.Equal(before, initial.World.DescribeState());
    }

    [Fact]
    public async Task Movement_uses_one_spatial_deadline_and_does_not_allow_concurrent_timed_actions() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(10), enableNeeds: false, enableWeather: false);
        var source = Commands(PlaygroundCommand.Go(Passage("camp", "stream")));
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: source);
        await StepWorldEvent(kernel);
        Assert.Equal(Hour(10), kernel.World.Time);
        TraversingLocation travelling = Assert.IsType<TraversingLocation>(Location(kernel.World));
        Assert.Equal(Hour(11), travelling.ArrivalDue);
        Assert.Equal("stream", travelling.TargetPlaceId.Value);
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        Assert.Null(PlaygroundActions.CurrentPlace(kernel.World, "a"));
        foreach (PlaygroundCommand blocked in new[] { PlaygroundCommand.Wait(1), PlaygroundCommand.Gather("berries"),
            PlaygroundCommand.Cancel(), PlaygroundCommand.Go(Passage("camp", "ridge")) }) {
            Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", blocked));
        }
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(new ModelTime(Hour(11).Ticks - 1)));
        Assert.Same(travelling, Location(kernel.World));
        await Advance(kernel);
        Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(Location(kernel.World)).PlaceId.Value);
        Assert.Equal(Hour(11), kernel.World.Time);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, kernel.World.Expedition!.Visits.Count);
        Assert.Same(kernel.World.Expedition.Spatial, kernel.World.Spatial!.Business);
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Contains(observation.Resources, resource => resource.Id == "berries");
        Assert.Null(observation.Journey);
        Assert.DoesNotContain(observation.Resources, resource => resource.Id == "wood");
        Assert.Null(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Gather("berries")));
    }

    [Fact]
    public async Task Return_journey_retains_known_destination_and_reverse_direction() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(10), enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: Commands(PlaygroundCommand.Go("e-c7"), PlaygroundCommand.Go("e-n4")));
        await StepWorldEvent(kernel);
        await Advance(kernel);
        await StepWorldEvent(kernel);
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Equal(new PlaygroundJourneyObservation("溪流湾", "e-n4", "沿林间缓坡向上", PlaygroundExpeditionMap.GetPlace("camp").Name), observation.Journey);
        Assert.Equal(Hour(12), observation.ArrivalDue);
        Assert.Single(kernel.World.Expedition!.ConfirmedPassages);
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "camp");
        Assert.Single(kernel.World.Expedition!.ConfirmedPassages);
    }

    [Fact]
    public async Task Light_change_at_arrival_is_settled_before_arrival_and_next_character_input() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(17), enableNeeds: false, enableWeather: false);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var source = Commands(PlaygroundCommand.Go(Passage("camp", "stream")), PlaygroundCommand.Wait(1));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await StepWorldEvent(kernel);
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "stream");
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.False(kernel.World.Light.IsDay);
        Assert.Equal(1, source.Calls);
        Assert.Equal(new[] { PlaygroundEventKind.LightChanged, PlaygroundEventKind.MoveArrived }, history.CompletedEvents
            .Where(e => e.TargetInstant.ModelTime == Hour(18)).SelectMany(e => e.Facts).Select(f => f.Kind));
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(1, source.Calls);
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WaitStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData("camp,stream,ravine,road", "road")]
    [InlineData("camp,ridge,cabin,signal", "signal")]
    public async Task Former_exit_routes_remain_active_and_allow_return_travel(string passages, string destination) {
        var initial = PlaygroundGameFactory.CreateSurvival(enableNeeds: false, enableWeather: false);
        string[] places = passages.Split(',');
        var source = Commands(places.Zip(places.Skip(1), (from, to) => PlaygroundCommand.Go(Passage(from, to)))
            .Append(PlaygroundCommand.Go(Passage(places[^1], places[^2]))).ToArray());
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == destination);
        Assert.Null(kernel.World.GetCharacter("a").Ending);
        Assert.Single(kernel.World.Expedition!.Spatial.Entities);
        Assert.Equal(PlaygroundExpeditionSummary.Exploring, kernel.World.ExpeditionSummary);
        Assert.Equal(3, source.Calls);
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == places[^2]);
        Assert.True(kernel.World.GetCharacter("a").IsActive);
        Assert.Equal(4, source.Calls);
        Assert.Contains(kernel.World.Expedition.Visits, visit => visit.PlaceId == destination);
    }

    [Fact]
    public async Task Departed_place_keeps_historical_resources_while_remote_regeneration_continues() {
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(6), enableNeeds: false, enableWeather: false);
        var kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: Commands(PlaygroundCommand.Go(Passage("camp", "stream")), PlaygroundCommand.Gather("berries"),
                PlaygroundCommand.Go(Passage("stream", "cabin"))));
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "stream");
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        await Advance(kernel);
        await AdvanceUntil(kernel, world => world.GetCharacter("a").Activity is null);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count(kernel.World.GetResource("berries").ItemId));
        await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        await Advance(kernel);
        PlaygroundVisit remembered = Assert.Single(kernel.World.Expedition!.Visits, visit => visit.PlaceId == "stream");
        string snapshot = remembered.ToString();
        await AdvanceUntil(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(snapshot, Assert.Single(kernel.World.Expedition!.Visits, visit => visit.PlaceId == "stream").ToString());
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.DoesNotContain(observation.Resources, resource => resource.Id == "berries");
        Assert.Contains(observation.Resources, resource => resource.Id == "wood");
        Assert.True(remembered.At < kernel.World.Time);
        Assert.Equal(5, Assert.Single(remembered.Resources, resource => resource.Id == "berries").Stock);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
        PlaygroundPlaceMemory memory = Assert.Single(observation.Memories, value => value.Name == PlaygroundExpeditionMap.GetPlace("stream").Name);
        Assert.Equal(remembered.At, memory.At);
        Assert.Equal(5, Assert.Single(memory.Resources, resource => resource.Id == "berries").Stock);
    }

    [Fact]
    public async Task Mid_movement_state_cold_open_keeps_deadline_and_continues_same_trace_without_decision() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(17), enableNeeds: false, enableWeather: false);
        TraversingLocation expectedLocation;
        KernelCursor savedCursor;
        var baseline = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: Commands(PlaygroundCommand.Go(Passage("camp", "ridge"))));
        await StepWorldEvent(baseline);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Go(Passage("camp", "ridge"))));
            await StepWorldEvent(kernel);
            expectedLocation = Assert.IsType<TraversingLocation>(Location(kernel.World));
            savedCursor = kernel.Cursor;
        }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(expectedLocation, Location(reopened.State));
        Assert.Equal(savedCursor, reopened.Cursor);
        var resumed = PlaygroundEngine.CreateKernel(reopened, characterController: new ForbiddenController());
        Assert.False(resumed.RecoverPending());
        for (int steps = 0; steps < 20 && PlaygroundActions.CurrentPlace(resumed.World, "a") != "ridge"; steps++) {
            await Advance(baseline);
            await Advance(resumed);
            Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, resumed.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, resumed.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
            AssertSpatialFacts(baseline.LastCompletion.Event, resumed.LastCompletion.Event);
        }
        Assert.Equal("ridge", PlaygroundActions.CurrentPlace(resumed.World, "a"));
        Assert.Equal(expectedLocation.ArrivalDue, resumed.World.Time);
    }

    [Fact]
    public async Task Incremental_and_full_expedition_traces_agree_through_gathering_night_and_road_arrival() {
        static StubCharacterController Driver() => Commands(PlaygroundCommand.Go(Passage("camp", "stream")),
            PlaygroundCommand.Gather("berries", 2), PlaygroundCommand.Wait(1), PlaygroundCommand.Go(Passage("stream", "ravine")),
            PlaygroundCommand.Go(Passage("ravine", "road")));
        var first = PlaygroundGameFactory.CreateSurvival(Hour(16), enableNeeds: false, enableWeather: false);
        var second = PlaygroundGameFactory.CreateSurvival(Hour(16), enableNeeds: false, enableWeather: false);
        var incremental = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(first.World, first.Cursor),
            PredictionMode.Incremental, characterController: Driver());
        var full = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(second.World, second.Cursor),
            PredictionMode.Full, characterController: Driver());
        for (int steps = 0; PlaygroundActions.CurrentPlace(incremental.World, "a") != "road"; steps++) {
            Assert.True(steps < 100);
            await Advance(incremental);
            await Advance(full);
            Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
            Assert.Equal(full.World.Expedition!.Spatial, incremental.World.Expedition!.Spatial);
            Assert.Equal(full.World.Spatial!.Contacts, incremental.World.Spatial!.Contacts);
            Assert.Equal(full.Cursor, incremental.Cursor);
            Assert.Equal(Describe(full.LastCompletion!.Event), Describe(incremental.LastCompletion!.Event));
            AssertSpatialFacts(full.LastCompletion.Event, incremental.LastCompletion.Event);
        }
        Assert.Null(full.World.GetCharacter("a").Ending);
        Assert.Equal("road", PlaygroundActions.CurrentPlace(full.World, "a"));
        Assert.Equal(2, full.World.GetCharacter("a").Backpack.Count("item.food"));
    }

    [Theory]
    [InlineData(PlaygroundEventKind.MoveStarted, false)]
    [InlineData(PlaygroundEventKind.MoveArrived, false)]
    [InlineData(PlaygroundEventKind.MoveArrived, true)]
    public async Task Movement_event_only_cold_recovery_applies_spatial_and_observation_effects_once(PlaygroundEventKind stopAt, bool exitArrival) {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(17), enableNeeds: false, enableWeather: false);
        static StubCharacterController Driver() => Commands(PlaygroundCommand.Go(Passage("camp", "stream")),
            PlaygroundCommand.Go(Passage("stream", "ravine")), PlaygroundCommand.Go(Passage("ravine", "road")));
        var baseline = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: Driver());
        do { await Advance(baseline); }
        while (Assert.Single(baseline.LastCompletion!.Event.Facts).Kind != stopAt ||
            exitArrival && PlaygroundActions.CurrentPlace(baseline.World, "a") != "road");
        string beforeCrash;
        KernelCursor cursorBeforeCrash;
        int callsBeforeCrash;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var source = Driver();
            var kernel = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, stopAt, exitArrival), characterController: source);
            for (int steps = 0; ; steps++) {
                Assert.True(steps < 20);
                beforeCrash = history.State.DescribeState();
                cursorBeforeCrash = history.Cursor;
                try { await Advance(kernel); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { break; }
            }
            Assert.True(kernel.IsFaulted);
            Assert.Equal(beforeCrash, history.State.DescribeState());
            callsBeforeCrash = source.Calls;
            Assert.Equal(exitArrival ? 3 : 1, callsBeforeCrash);
        }
        string finalText;
        KernelCursor finalCursor;
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCrash, reopened.State.DescribeState());
            Assert.Equal(cursorBeforeCrash, reopened.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(reopened.PendingEvent!));
            AssertSpatialFacts(baseline.LastCompletion.Event, reopened.PendingEvent!);
            int completions = 0;
            var recovered = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => {
                    completions++;
                    return PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental);
                }, PlaygroundEngine.Validate);
            Assert.True(recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(baseline.World.DescribeState(), recovered.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, recovered.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
            Assert.Same(reopened.State, recovered.World);
            Assert.Same(recovered.World.Expedition!.Spatial, recovered.World.Spatial!.Business);
            Assert.Equal(exitArrival ? 4 : stopAt == PlaygroundEventKind.MoveStarted ? 1 : 2, recovered.World.Expedition.Visits.Count);
            Assert.Null(recovered.World.GetCharacter("a").Ending);
            if (exitArrival) { Assert.Equal("road", PlaygroundActions.CurrentPlace(recovered.World, "a")); }
            finalText = recovered.World.DescribeState();
            finalCursor = recovered.Cursor;
        }
        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(finalText, final.State.DescribeState());
        Assert.Equal(finalCursor, final.Cursor);
        Assert.Equal(baseline.World.Expedition!.Spatial, final.State.Expedition!.Spatial);
    }

    private static SpatialLocation Location(PlaygroundWorld world) => Assert.Single(world.Expedition!.Spatial.Entities).Location;
    private static string Passage(string from, string to) => Assert.Single(PlaygroundExpeditionMap.GetEntrances(from), passage => passage.To == to).EntranceId;
    private static void AssertSpatialFacts(OccurrenceEvent<PlaygroundFact> expected, OccurrenceEvent<PlaygroundFact> actual) =>
        Assert.Equal(expected.Facts.SelectMany(fact => fact.SpatialFacts), actual.Facts.SelectMany(fact => fact.SpatialFacts));

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        kernel.World.Validate();
        // Existing gather tests retain their independent owner formula. Here compare the combined caches as well.
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.DescribeState(), kernel.World.DescribeState());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial!.Business);
    }

    private static async Task AdvanceUntil(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        Func<PlaygroundWorld, bool> predicate) {
        for (int steps = 0; steps < 100; steps++) {
            if (predicate(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected expedition boundary was not reached.");
    }

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Travelling must not request a new character command.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call a controller.");
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class StopAfterEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, PlaygroundEventKind stopAt, bool exitArrival)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt &&
                (!exitArrival || fact.Visits.Any(visit => visit.ActorId == "a" && visit.PlaceId == "road")))) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
