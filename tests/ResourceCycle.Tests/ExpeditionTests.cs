using System.Text.Encodings.Web;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ExpeditionTests {
    [Fact]
    public void Initial_observation_and_command_checks_do_not_disclose_remote_resources_or_exits() {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false);
        CycleDecisionContext context = CycleDecisionContext.Create(initial.World, "a");
        string before = initial.World.DescribeState();
        Assert.Empty(context.Observation.Resources);
        string serialized = JsonSerializer.Serialize(context.Observation,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        foreach (CycleResource remote in initial.World.Resources) {
            Assert.DoesNotContain(remote.Id, serialized, StringComparison.Ordinal);
            Assert.Equal(context.Check(CycleCommand.Gather("does-not-exist")), context.Check(CycleCommand.Gather(remote.Id)));
            Assert.Equal(CycleActions.Check(initial.World, "a", CycleCommand.Gather("does-not-exist")),
                CycleActions.Check(initial.World, "a", CycleCommand.Gather(remote.Id)));
        }
        Assert.DoesNotContain("山脚公路", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("巡山信号站", serialized, StringComparison.Ordinal);
        Assert.NotNull(context.Check(CycleCommand.Go(Passage("ravine", "road"))));
        Assert.Null(context.Check(CycleCommand.Go(Passage("camp", "stream"))));
        Assert.Equal(before, initial.World.DescribeState());
    }

    [Fact]
    public async Task Movement_uses_one_spatial_deadline_and_does_not_allow_concurrent_timed_actions() {
        var initial = CycleGameFactory.CreateSurvival(Hour(10), enableNeeds: false);
        var source = Commands(CycleCommand.Go(Passage("camp", "stream")));
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: source);
        await Advance(kernel);
        Assert.Equal(Hour(10), kernel.World.Time);
        TraversingLocation travelling = Assert.IsType<TraversingLocation>(Location(kernel.World));
        Assert.Equal(Hour(11), travelling.ArrivalDue);
        Assert.Equal("stream", travelling.TargetPlaceId.Value);
        Assert.Null(kernel.World.GetCharacter("a").Activity);
        Assert.Null(CycleActions.CurrentPlace(kernel.World, "a"));
        foreach (CycleCommand blocked in new[] { CycleCommand.Wait(1), CycleCommand.Gather("berries"),
            CycleCommand.Cancel(), CycleCommand.Go(Passage("camp", "ridge")) }) {
            Assert.NotNull(CycleActions.Check(kernel.World, "a", blocked));
        }
        Assert.Equal(StepStatus.BoundaryReached, await kernel.StepAsync(new ModelTime(Hour(11).Ticks - 1)));
        Assert.Same(travelling, Location(kernel.World));
        await Advance(kernel);
        Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(Location(kernel.World)).PlaceId.Value);
        Assert.Equal(Hour(11), kernel.World.Time);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, kernel.World.Expedition!.Visits.Count);
        Assert.Same(kernel.World.Expedition.Spatial, kernel.World.Spatial!.Business);
        CycleDecisionContext context = CycleDecisionContext.Create(kernel.World, "a");
        Assert.Contains(context.Observation.Resources, resource => resource.Id == "berries");
        Assert.DoesNotContain(context.Observation.Resources, resource => resource.Id == "wood");
        Assert.Null(context.Check(CycleCommand.Gather("berries")));
    }

    [Fact]
    public async Task Light_change_at_arrival_is_settled_before_arrival_and_next_character_input() {
        var initial = CycleGameFactory.CreateSurvival(Hour(17), enableNeeds: false);
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var source = Commands(CycleCommand.Go(Passage("camp", "stream")), CycleCommand.Wait(1));
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await Advance(kernel);
        await AdvanceUntil(kernel, world => CycleActions.CurrentPlace(world, "a") == "stream");
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.False(kernel.World.Light.IsDay);
        Assert.Equal(1, source.Calls);
        Assert.Equal(new[] { CycleEventKind.LightChanged, CycleEventKind.MoveArrived }, history.CompletedEvents
            .Where(e => e.TargetInstant.ModelTime == Hour(18)).SelectMany(e => e.Facts).Select(f => f.Kind));
        await Advance(kernel);
        Assert.Equal(CycleEventKind.WaitStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.Equal(2, source.Calls);
    }

    [Theory]
    [InlineData("camp,stream,ravine,road", "road")]
    [InlineData("camp,ridge,cabin,signal", "signal")]
    public async Task Both_routes_reach_an_exit_and_terminal_world_stops_without_more_input(string passages, string destination) {
        var initial = CycleGameFactory.CreateSurvival(enableNeeds: false);
        string[] places = passages.Split(',');
        var source = Commands(places.Zip(places.Skip(1), (from, to) => CycleCommand.Go(Passage(from, to))).ToArray());
        var history = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(history, characterController: source);
        await AdvanceUntil(kernel, world => world.Expedition!.Outcome != CycleExpeditionOutcome.Exploring);
        Assert.Equal(CycleExpeditionOutcome.Escaped, kernel.World.Expedition!.Outcome);
        Assert.Equal(destination, Assert.IsType<AtPlaceLocation>(Location(kernel.World)).PlaceId.Value);
        Assert.False(string.IsNullOrWhiteSpace(kernel.World.Expedition.EndingReason));
        Assert.Equal(3, source.Calls);
        string terminal = kernel.World.DescribeState();
        KernelCursor cursor = kernel.Cursor;
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(240)));
        Assert.Equal(terminal, kernel.World.DescribeState());
        Assert.Equal(cursor, kernel.Cursor);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task Departed_place_keeps_historical_resources_while_remote_regeneration_continues() {
        var initial = CycleGameFactory.CreateSurvival(Hour(6), enableNeeds: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: Commands(CycleCommand.Go(Passage("camp", "stream")), CycleCommand.Gather("berries"),
                CycleCommand.Go(Passage("stream", "cabin"))));
        await AdvanceUntil(kernel, world => CycleActions.CurrentPlace(world, "a") == "stream");
        await Advance(kernel);
        await AdvanceUntil(kernel, world => world.GetCharacter("a").Activity is null);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count(kernel.World.GetResource("berries").ItemId));
        await Advance(kernel);
        CycleVisit remembered = Assert.Single(kernel.World.Expedition!.Visits, visit => visit.PlaceId == "stream");
        string snapshot = remembered.ToString();
        await AdvanceUntil(kernel, world => CycleActions.CurrentPlace(world, "a") == "cabin");
        Assert.Equal(snapshot, Assert.Single(kernel.World.Expedition!.Visits, visit => visit.PlaceId == "stream").ToString());
        CycleObservation observation = CycleDecisionContext.Create(kernel.World, "a").Observation;
        Assert.DoesNotContain(observation.Resources, resource => resource.Id == "berries");
        Assert.Contains(observation.Resources, resource => resource.Id == "wood");
        Assert.True(remembered.At < kernel.World.Time);
        Assert.Equal(5, Assert.Single(remembered.Resources, resource => resource.Id == "berries").Stock);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
        CyclePlaceMemory memory = Assert.Single(observation.Memories, value => value.Name == CycleExpeditionMap.GetPlace("stream").Name);
        Assert.Equal(remembered.At, memory.At);
        Assert.Equal(5, Assert.Single(memory.Resources, resource => resource.Id == "berries").Stock);
    }

    [Fact]
    public async Task Mid_movement_state_cold_open_keeps_deadline_and_continues_same_trace_without_decision() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(17), enableNeeds: false);
        TraversingLocation expectedLocation;
        KernelCursor savedCursor;
        var baseline = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: Commands(CycleCommand.Go(Passage("camp", "ridge"))));
        await Advance(baseline);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Go(Passage("camp", "ridge"))));
            await Advance(kernel);
            expectedLocation = Assert.IsType<TraversingLocation>(Location(kernel.World));
            savedCursor = kernel.Cursor;
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(expectedLocation, Location(reopened.State));
        Assert.Equal(savedCursor, reopened.Cursor);
        var resumed = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
        Assert.False(resumed.RecoverPending());
        for (int steps = 0; steps < 20 && CycleActions.CurrentPlace(resumed.World, "a") != "ridge"; steps++) {
            await Advance(baseline);
            await Advance(resumed);
            Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, resumed.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, resumed.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
            AssertSpatialFacts(baseline.LastCompletion.Event, resumed.LastCompletion.Event);
        }
        Assert.Equal("ridge", CycleActions.CurrentPlace(resumed.World, "a"));
        Assert.Equal(expectedLocation.ArrivalDue, resumed.World.Time);
    }

    [Fact]
    public async Task Incremental_and_full_expedition_traces_agree_through_gathering_night_and_escape() {
        static StubCharacterController Driver() => Commands(CycleCommand.Go(Passage("camp", "stream")),
            CycleCommand.Gather("berries", 2), CycleCommand.Wait(1), CycleCommand.Go(Passage("stream", "ravine")),
            CycleCommand.Go(Passage("ravine", "road")));
        var first = CycleGameFactory.CreateSurvival(Hour(16), enableNeeds: false);
        var second = CycleGameFactory.CreateSurvival(Hour(16), enableNeeds: false);
        var incremental = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(first.World, first.Cursor),
            PredictionMode.Incremental, characterController: Driver());
        var full = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(second.World, second.Cursor),
            PredictionMode.Full, characterController: Driver());
        for (int steps = 0; incremental.World.Expedition!.Outcome == CycleExpeditionOutcome.Exploring; steps++) {
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
        Assert.Equal(CycleExpeditionOutcome.Escaped, full.World.Expedition!.Outcome);
        Assert.Equal(2, full.World.GetCharacter("a").Backpack.Count("item.food"));
    }

    [Theory]
    [InlineData(CycleEventKind.MoveStarted, false)]
    [InlineData(CycleEventKind.MoveArrived, false)]
    [InlineData(CycleEventKind.MoveArrived, true)]
    public async Task Movement_event_only_cold_recovery_applies_spatial_and_observation_effects_once(CycleEventKind stopAt, bool exitArrival) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateSurvival(Hour(17), enableNeeds: false);
        static StubCharacterController Driver() => Commands(CycleCommand.Go(Passage("camp", "stream")),
            CycleCommand.Go(Passage("stream", "ravine")), CycleCommand.Go(Passage("ravine", "road")));
        var baseline = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: Driver());
        do { await Advance(baseline); }
        while (Assert.Single(baseline.LastCompletion!.Event.Facts).Kind != stopAt ||
            exitArrival && baseline.World.Expedition!.Outcome != CycleExpeditionOutcome.Escaped);
        string beforeCrash;
        KernelCursor cursorBeforeCrash;
        int callsBeforeCrash;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var source = Driver();
            var kernel = CycleEngine.CreateKernel(new StopAfterEvent(history, stopAt, exitArrival), characterController: source);
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
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCrash, reopened.State.DescribeState());
            Assert.Equal(cursorBeforeCrash, reopened.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(reopened.PendingEvent!));
            AssertSpatialFacts(baseline.LastCompletion.Event, reopened.PendingEvent!);
            int completions = 0;
            var recovered = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => {
                    completions++;
                    return CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental);
                }, CycleEngine.Validate);
            Assert.True(recovered.RecoverPending());
            Assert.False(recovered.RecoverPending());
            Assert.Equal(1, completions);
            Assert.Equal(baseline.World.DescribeState(), recovered.World.DescribeState());
            Assert.Equal(baseline.World.Expedition!.Spatial, recovered.World.Expedition!.Spatial);
            Assert.Equal(baseline.Cursor, recovered.Cursor);
            Assert.Same(reopened.State, recovered.World);
            Assert.Same(recovered.World.Expedition!.Spatial, recovered.World.Spatial!.Business);
            Assert.Equal(exitArrival ? 4 : stopAt == CycleEventKind.MoveStarted ? 1 : 2, recovered.World.Expedition.Visits.Count);
            Assert.Equal(exitArrival ? CycleExpeditionOutcome.Escaped : CycleExpeditionOutcome.Exploring, recovered.World.Expedition.Outcome);
            finalText = recovered.World.DescribeState();
            finalCursor = recovered.Cursor;
        }
        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(finalText, final.State.DescribeState());
        Assert.Equal(finalCursor, final.Cursor);
        Assert.Equal(baseline.World.Expedition!.Spatial, final.State.Expedition!.Spatial);
    }

    private static SpatialLocation Location(CycleWorld world) => Assert.Single(world.Expedition!.Spatial.Entities).Location;
    private static string Passage(string from, string to) => Assert.Single(CycleExpeditionMap.GetPassages(from), passage => passage.To == to).Id;
    private static void AssertSpatialFacts(OccurrenceEvent<CycleFact> expected, OccurrenceEvent<CycleFact> actual) =>
        Assert.Equal(expected.Facts.SelectMany(fact => fact.SpatialFacts), actual.Facts.SelectMany(fact => fact.SpatialFacts));

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        kernel.World.Validate();
        // Existing gather tests retain their independent owner formula. Here compare the combined caches as well.
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.DescribeState(), kernel.World.DescribeState());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Same(kernel.World.Expedition!.Spatial, kernel.World.Spatial!.Business);
    }

    private static async Task AdvanceUntil(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel,
        Func<CycleWorld, bool> predicate) {
        for (int steps = 0; steps < 100; steps++) {
            if (predicate(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("Expected expedition boundary was not reached.");
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleCommand> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Travelling must not request a new character command.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Recovery must not forecast.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recovery must not plan or call a controller.");
    }

    private sealed class InjectedCrash : IOException { }

    private sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, CycleEventKind stopAt, bool exitArrival)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == stopAt &&
                (!exitArrival || fact.Outcome == CycleExpeditionOutcome.Escaped))) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
