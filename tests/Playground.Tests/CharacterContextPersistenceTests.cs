using System.Collections;
using System.Reflection;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CharacterContextPersistenceTests {
    [Fact]
    public async Task Multi_source_rates_survive_state_and_pending_event_with_old_opening_frozen() {
        using var store = new TemporaryStore();
        PlaygroundWorld original = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        PlaygroundCharacter actor = original.GetCharacter("a");
        PlaygroundSurvival body = actor.Survival!;
        var food = new RateBreakdown([new("fixture.base", "基础消耗", -8), new("fixture.recovery", "恢复", 3)]);
        var warmth = new RateBreakdown([new("fixture.sun", "日照", 18), new("fixture.exposure", "暴露", -12)]);
        PlaygroundWorld initial = PlaygroundEngine.Rebuild(original.Business.Replace(original.Time,
            changedCharacters: [actor.WithSurvival(body.WithRates(original.Time, food, body.WaterRate, warmth))]), original.Time);
        CharacterContext savedOpening;
        PlaygroundFact recorded;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, fact => fact.Kind == PlaygroundEventKind.ItemUsed),
                characterController: Commands(PlaygroundCommand.Use("item.water")));
            await Advance(interrupted);
            savedOpening = history.State.Business.InteractionStates["a"].CurrentContext!;
            Assert.True(warmth.SameAs(savedOpening.Opening.Observation.Survival!.WarmthRate));
            Assert.Equal(new PlaygroundAttributeForecast(Hour(9), Hour(14)), savedOpening.Opening.Observation.Survival.FoodForecast);
            Assert.Equal(new PlaygroundAttributeForecast(new(Hour(8).Ticks + 5 * PlaygroundTime.Hour / 2), new(Hour(8).Ticks + 45 * PlaygroundTime.Hour / 8)),
                savedOpening.Opening.Observation.Survival.WaterForecast);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(interrupted));
            recorded = Assert.Single(history.PendingEvent!.Facts);
            Assert.True(food.SameAs(Assert.Single(recorded.ChangedCharacters).Survival!.FoodRate));
            Assert.True(warmth.SameAs(history.State.GetCharacter("a").Survival!.WarmthRate));
        }

        CharacterInteractionState recoveredState;
        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            AssertSemanticEqual(savedOpening, history.State.Business.InteractionStates["a"].CurrentContext);
            Assert.True(warmth.SameAs(history.State.GetCharacter("a").Survival!.WarmthRate));
            Assert.True(food.SameAs(history.State.GetCharacter("a").Survival!.FoodRate));
            AssertSemanticEqual(recorded.InteractionChanges, Assert.Single(history.PendingEvent!.Facts).InteractionChanges);
            var forbidden = new ForbiddenController();
            var resumed = PlaygroundEngine.CreateKernel(history, characterController: forbidden);
            Assert.True(resumed.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            recoveredState = resumed.World.Business.InteractionStates["a"];
            Assert.True(warmth.SameAs(recoveredState.CurrentContext!.Opening.Observation.Survival!.WarmthRate));
            Assert.True(food.SameAs(recoveredState.CurrentContext.LatestObservation.Survival!.FoodRate));
            Assert.True(PlaygroundSurvivalRules.WarmthRate(false, true).SameAs(recoveredState.CurrentContext.LatestObservation.Survival.WarmthRate));
            Assert.False(warmth.SameAs(recoveredState.CurrentContext.LatestObservation.Survival.WarmthRate));
            PlaygroundSurvivalObservation openingBody = recoveredState.CurrentContext.Opening.Observation.Survival!;
            PlaygroundSurvivalObservation latestBody = recoveredState.CurrentContext.LatestObservation.Survival!;
            Assert.Equal(savedOpening.Opening.Observation.Survival!.WaterForecast, openingBody.WaterForecast);
            Assert.NotEqual(openingBody.WaterForecast, latestBody.WaterForecast);
            Assert.Equal(openingBody.FoodForecast, latestBody.FoodForecast);
            Assert.Equal(openingBody.WarmthForecast, latestBody.WarmthForecast);
        }

        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        AssertSemanticEqual(recoveredState, final.State.Business.InteractionStates["a"]);
        Assert.True(food.SameAs(final.State.GetCharacter("a").Survival!.FoodRate));
    }

    [Fact]
    public async Task State_cold_open_keeps_full_current_context_and_continues_without_reopening() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        CharacterInteractionState saved;
        KernelCursor cursor;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var controller = Commands(PlaygroundCommand.Handoff("沿溪流看看"), PlaygroundCommand.Use("item.water"));
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            await Advance(kernel);
            Assert.Equal(0, controller.Calls);
            Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Last(kernel).Kind);
            await Advance(kernel);
            await Advance(kernel);
            saved = kernel.World.Business.InteractionStates["a"];
            var context = saved.CurrentContext!;
            Assert.Equal(CharacterContextStatus.Open, context.Status);
            Assert.Equal(1, context.ContextSequence);
            Assert.Equal(2, context.LastOperationSequence);
            Assert.Equal(2, context.Items.Count);
            Assert.Equal(new long[] { 1, 2 }, context.Items.Select(item => item.ItemSequence));
            Assert.Equal(new[] { "call_fixture_1", "call_fixture_2" }, context.Items.Select(item => item.ToolCallId));
            Assert.Equal(1, context.Opening.Observation.Actor.Backpack.Count("item.water"));
            Assert.Equal(0, context.LatestObservation.Actor.Backpack.Count("item.water"));
            Assert.Equal("沿溪流看看", saved.DraftHandoff);
            Assert.All(context.Items.SelectMany(item => item.Experiences), experience => Assert.Equal("a", experience.Event.ObserverId));
            cursor = kernel.Cursor;
        }

        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(cursor, history.Cursor);
            var restored = history.State.Business.InteractionStates["a"];
            Assert.NotSame(saved, restored);
            AssertSemanticEqual(saved, restored);
            CharacterContextLifecycleTests.AssertFrozenVisibleGraph(restored.CurrentContext!);
            var controller = new ResumedController();
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
            Assert.False(kernel.RecoverPending());
            await Advance(kernel);
            Assert.Equal(PlaygroundEventKind.HandoffEdited, Last(kernel).Kind);
            var current = kernel.World.Business.InteractionStates["a"].CurrentContext!;
            Assert.Equal(1, current.ContextSequence);
            Assert.Equal(3, current.LastOperationSequence);
            Assert.Equal(new long[] { 1, 2, 3 }, current.Items.Select(item => item.ItemSequence));
            AssertSemanticEqual(restored.CurrentContext!.Opening, current.Opening);
            Assert.Equal("冷开后继续写", kernel.World.Business.InteractionStates["a"].DraftHandoff);
        }
    }

    [Theory]
    [InlineData("instant")]
    [InlineData("release")]
    [InlineData("opening")]
    [InlineData("terminal")]
    public async Task Event_only_cold_recovery_installs_recorded_context_without_controller_and_survives_another_cold_open(string boundary) {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var expected = PlaygroundEngine.CreateKernel(Memory(initial.World), characterController: new Script());
        for (int step = 0; ; step++) {
            Assert.True(step < 150, "Requested context boundary was not reached.");
            await Advance(expected);
            if (Matches(Last(expected), boundary)) { break; }
        }
        var expectedInteraction = expected.World.Business.InteractionStates["a"];
        PlaygroundWorld before = initial.World;
        KernelCursor previousCursor = InitialCursor(initial.World);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, previousCursor)) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterEvent(history, fact => Matches(fact, boundary)), characterController: new Script());
            for (int step = 0; ; step++) {
                Assert.True(step < 150);
                before = history.State;
                previousCursor = history.Cursor;
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { break; }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.Same(before, history.State);
            AssertSemanticEqual(Last(expected).InteractionChanges, Assert.Single(history.PendingEvent!.Facts).InteractionChanges);
        }

        using (var history = PlaygroundDurableHistory.Open(store.StorePath)) {
            Assert.Equal(previousCursor, history.Cursor);
            AssertSemanticEqual(before.Business.InteractionStates, history.State.Business.InteractionStates);
            PlaygroundFact recorded = Assert.Single(history.PendingEvent!.Facts);
            AssertSemanticEqual(Last(expected).InteractionChanges, recorded.InteractionChanges);
            var controller = new ForbiddenController();
            var recovered = PlaygroundEngine.CreateKernel(history, characterController: controller);
            Assert.True(recovered.RecoverPending());
            Assert.Equal(0, controller.Calls);
            Assert.Null(history.PendingEvent);
            Assert.Equal(expected.Cursor, recovered.Cursor);
            Assert.Equal(expected.World.DescribeState(), recovered.World.DescribeState());
            AssertSemanticEqual(expectedInteraction, recovered.World.Business.InteractionStates["a"]);
            foreach (var change in recorded.InteractionChanges) {
                Assert.Same(change, recovered.World.Business.InteractionStates[change.ActorId]);
            }
            Assert.False(recovered.RecoverPending());
            if (boundary == "terminal") {
                var terminal = recovered.World.Business.InteractionStates["a"].CurrentContext!;
                Assert.Equal(CharacterContextStatus.Terminal, terminal.Status);
                Assert.True(terminal.ContextSequence > before.Business.InteractionStates["a"].CurrentContext!.ContextSequence);
                Assert.Contains(terminal.Opening.Experiences, item => item.Event.Kind == PlaygroundObservedEventKind.SurvivalFailed);
                Assert.Equal(StepStatus.Exhausted, await recovered.StepAsync(Hour(240)));
                Assert.Equal(0, controller.Calls);
            }
        }

        using var final = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(expected.Cursor, final.Cursor);
        AssertSemanticEqual(expectedInteraction, final.State.Business.InteractionStates["a"]);
    }

    private static bool Matches(PlaygroundFact fact, string boundary) => boundary switch {
        "instant" => fact.Kind == PlaygroundEventKind.HandoffEdited,
        "release" => fact.Kind == PlaygroundEventKind.WaitStarted,
        "opening" => fact.Kind == PlaygroundEventKind.DecisionContextOpened &&
            fact.InteractionChanges.Any(change => change.CurrentContext?.ContextSequence == 2),
        "terminal" => fact.Kind == PlaygroundEventKind.SurvivalFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    private sealed class Script : IPlaygroundCharacterController {
        private int _calls;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            int call = ++_calls;
            var command = call switch {
                1 => PlaygroundCommand.Handoff("观察等待后的身体变化"),
                2 => PlaygroundCommand.Wait(24),
                _ => PlaygroundCommand.Continue()
            };
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(new PlaygroundControllerOperation($"endpoint.original/{call}", PlaygroundCommandParser.Format(command), $"context/operation-{call}"));
        }
    }

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("RecoverPending must install recorded interaction material without choosing again.");
        }
    }

    private sealed class ResumedController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Operation(PlaygroundCommand.Handoff("冷开后继续写"), 3));
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, Func<PlaygroundFact, bool> shouldStop)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(shouldStop)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }

    private static PlaygroundFact Last(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));

    // Compare the complete public semantic graph, including every saved observation, effect and identity.
    // This is a persistence oracle, independent of ToString coverage and presentation placement.
    internal static void AssertSemanticEqual(object? expected, object? actual) {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || expected is string or decimal or PlaygroundVisibleValue) {
            Assert.Equal(expected, actual);
            return;
        }
        if (expected is IDictionary expectedMap) {
            IDictionary actualMap = Assert.IsAssignableFrom<IDictionary>(actual);
            Assert.Equal(expectedMap.Count, actualMap.Count);
            foreach (DictionaryEntry entry in expectedMap) {
                Assert.True(actualMap.Contains(entry.Key));
                AssertSemanticEqual(entry.Value, actualMap[entry.Key]);
            }
            return;
        }
        if (expected is IEnumerable expectedItems) {
            object?[] left = expectedItems.Cast<object?>().ToArray();
            object?[] right = Assert.IsAssignableFrom<IEnumerable>(actual).Cast<object?>().ToArray();
            Assert.Equal(left.Length, right.Length);
            for (int index = 0; index < left.Length; index++) { AssertSemanticEqual(left[index], right[index]); }
            return;
        }
        Assert.Equal(type, actual.GetType());
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) {
                AssertSemanticEqual(property.GetValue(expected), property.GetValue(actual));
            }
        }
    }
}
