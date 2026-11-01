using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Two-character publication and cold-open boundaries exercised through the production kernel.</summary>
public sealed class DuoRecoveryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Personal_death_event_only_recovery_retires_only_its_actor_and_other_actor_continues(bool longRoute) {
        using var store = new TemporaryStore();
        CycleWorld initial = EndingWorld(longRoute);
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new DepartureScript(longRoute));
        await UntilState(baseline, world => !world.GetCharacter("a").IsActive);
        CycleFact endingFact = Assert.Single(baseline.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.SurvivalFailed, endingFact.Kind);
        Assert.Equal(longRoute ? Hour(19) : Hour(17), baseline.World.Time);
        string terminal = MaterialSnapshot(baseline.World.Business.InteractionStates["a"]);
        string frozenBody = MaterialSnapshot(baseline.World.GetCharacter("a").Survival);

        KernelCursor beforeCursor = default;
        string? beforeState = null;
        ModelTime? originalArrival = null;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterEvent(history,
                fact => fact.ChangedCharacters.Any(actor => actor.Id == "a" && !actor.IsActive)),
                characterController: new DepartureScript(longRoute));
            for (int index = 0; index < 50 && !interrupted.IsFaulted; index++) {
                beforeCursor = history.Cursor;
                beforeState = MaterialSnapshot(history.State.Business.InteractionStates);
                if (history.State.Expedition!.Spatial.Entities.Single(entity => entity.Id.Value == "a").Location is TraversingLocation moving) {
                    originalArrival = moving.ArrivalDue;
                }
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.True(history.State.GetCharacter("a").IsActive);
            Assert.NotNull(history.PendingEvent);
            Assert.Equal(Describe(baseline.LastCompletion.Event), Describe(history.PendingEvent));
        }
        Assert.Equal(longRoute ? Hour(19) : Hour(18), originalArrival);

        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(beforeCursor, history.Cursor);
            Assert.Equal(beforeState, MaterialSnapshot(history.State.Business.InteractionStates));
            var recovery = RecoveryWithoutPlanning(history);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Null(history.PendingEvent);
            AssertWorldEqual(baseline, recovery);
            Assert.Equal(CycleExpeditionOutcome.Dead, recovery.World.GetCharacter("a").Ending!.Outcome);
            Assert.Equal("b", Assert.Single(recovery.World.Expedition!.Spatial.Entities).Id.Value);
            Assert.Equal(CycleExpeditionSummary.Exploring, recovery.World.ExpeditionSummary);
            Assert.Null(recovery.World.GetCharacter("a").Activity);
            Assert.Equal(CharacterContextStatus.Terminal, recovery.World.Business.InteractionStates["a"].CurrentContext!.Status);
        }

        // Reopen a committed S before the surviving actor has finished: both Contexts and the
        // survivor's pending wait must survive without re-asking either controller.
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(terminal, MaterialSnapshot(history.State.Business.InteractionStates["a"]));
            Assert.Equal("private-b", history.State.Business.InteractionStates["b"].PendingAdvance!.Handoff);
            Assert.Equal("private-a", history.State.Business.InteractionStates["a"].CurrentContext!.Opening.PreviousAdvance!.Handoff);
            var resumed = CycleEngine.CreateKernel(history, characterController: new ForbiddenController());
            Assert.False(resumed.RecoverPending());
            await UntilState(resumed, world => world.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
            await UntilState(baseline, world => world.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
            AssertWorldEqual(baseline, resumed);
            Assert.Equal(Hour(24), resumed.World.Time);
            Assert.True(resumed.World.Time > originalArrival!.Value);
            Assert.Equal(terminal, MaterialSnapshot(resumed.World.Business.InteractionStates["a"]));
            Assert.Equal(frozenBody, MaterialSnapshot(resumed.World.GetCharacter("a").Survival));
            Assert.Equal(longRoute ? Hour(19) : Hour(17), CycleDecisionContext.Observe(resumed.World, "a").Time);
            CharacterContext other = resumed.World.Business.InteractionStates["b"].CurrentContext!;
            Assert.Equal("b", other.LatestObservation.Actor.Id);
            Assert.Equal(CycleExpeditionOutcome.Exploring, other.LatestObservation.Outcome);
            Assert.Null(other.LatestObservation.EndingReason);
            Assert.Equal("private-b", other.Opening.PreviousAdvance!.Handoff);
            Assert.DoesNotContain(other.Opening.Experiences, experience =>
                experience.Event.Kind is CycleObservedEventKind.ExpeditionEnded or CycleObservedEventKind.SurvivalFailed);
            Assert.All(other.Opening.Experiences, experience => Assert.Equal("b", experience.Event.ObserverId));
            Assert.DoesNotContain(other.LatestObservation.Memories, memory => memory.PlaceId is "ravine" or "road" or "camp");
            Assert.DoesNotContain(resumed.World.Forecasts.Predictions, prediction => prediction.TargetId == "a");
        }

        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(Hour(24), final.State.Time);
        Assert.Equal(terminal, MaterialSnapshot(final.State.Business.InteractionStates["a"]));
        Assert.Equal(MaterialSnapshot(baseline.World.Business.InteractionStates), MaterialSnapshot(final.State.Business.InteractionStates));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_weather_event_restores_both_exposures_and_optional_personal_death_without_redraw(bool fatal) {
        using var store = new TemporaryStore();
        CycleWorld initial = WeatherWorld(fatal);
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(baseline);
        Assert.Equal(CycleEventKind.WeatherResolved, Assert.Single(baseline.LastCompletion!.Event.Facts).Kind);
        Assert.Null(Assert.Single(baseline.LastCompletion.Event.Facts).ActorId);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterEvent(history, fact => fact.Kind == CycleEventKind.WeatherResolved),
                characterController: new ForbiddenController());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Advance(interrupted));
            Assert.IsType<InjectedCrash>(error.InnerException);
            Assert.Equal(0, history.State.Expedition!.Environment!.Sequence);
            Assert.All(history.State.Characters, actor => Assert.True(actor.IsActive));
        }

        string terminal = "";
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(0, history.State.Expedition!.Environment!.Sequence);
            Assert.Equal(Describe(baseline.LastCompletion.Event), Describe(history.PendingEvent!));
            var recovery = RecoveryWithoutPlanning(history);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            AssertWorldEqual(baseline, recovery);
            CycleEnvironment weather = recovery.World.Expedition!.Environment!;
            Assert.Equal(1, weather.Sequence);
            Assert.True(weather.LastCold);
            Assert.Equal(Hour(10), weather.NextDue);
            Assert.Equal(new[] { "a", "b" }, weather.LastEffects.Select(effect => effect.ActorId));
            CycleWeatherEffect exposed = weather.LastEffects[0], sheltered = weather.LastEffects[1];
            Assert.False(exposed.Sheltered);
            Assert.Equal((fatal ? 9 : 10) * CycleSurvival.Scale, exposed.WarmthLossUnits);
            Assert.True(sheltered.Sheltered);
            Assert.Equal(0, sheltered.WarmthLossUnits);
            Assert.Equal((fatal ? 0 : 19) * CycleSurvival.Scale, recovery.World.GetCharacter("a").Survival!.WarmthUnits);
            Assert.Equal(60 * CycleSurvival.Scale, recovery.World.GetCharacter("b").Survival!.WarmthUnits);
            Assert.Equal(!fatal, recovery.World.GetCharacter("a").IsActive);
            Assert.True(recovery.World.GetCharacter("b").IsActive);
            Assert.Equal(fatal ? 1 : 2, recovery.World.Expedition.Spatial.Entities.Count);
            terminal = MaterialSnapshot(recovery.World.Business.InteractionStates["a"]);
            Assert.DoesNotContain(recovery.World.Business.InteractionStates["b"].UndeliveredExperiences,
                experience => experience.Event.Kind == CycleObservedEventKind.ExpeditionEnded);
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(1, reopened.State.Expedition!.Environment!.Sequence);
        Assert.Equal(MaterialSnapshot(baseline.World.Business.InteractionStates), MaterialSnapshot(reopened.State.Business.InteractionStates));
        if (fatal) {
            var resumed = CycleEngine.CreateKernel(reopened, characterController: new ForbiddenController());
            await UntilState(resumed, world => world.Expedition!.Environment!.Sequence == 2);
            Assert.Equal(Hour(10), resumed.World.Time);
            Assert.Equal("b", Assert.Single(resumed.World.Expedition!.Environment!.LastEffects).ActorId);
            Assert.Equal(terminal, MaterialSnapshot(resumed.World.Business.InteractionStates["a"]));
            Assert.Equal(0, resumed.World.GetCharacter("a").Survival!.WarmthUnits);
        }
    }

    private static CycleWorld EndingWorld(bool longRoute) {
        ModelTime at = Hour(16);
        var initial = CycleGameFactory.CreateSurvival(at, enableNeeds: false, enableWeather: false, characterCount: 2);
        var a = new CycleCharacter("a", "Distant explorer", survival: new CycleSurvival(at, 100 * CycleSurvival.Scale,
            (longRoute ? 24 : 8) * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2));
        var b = new CycleCharacter("b", "Sheltered explorer", survival: CycleSurvival.Create(at, 100, 100, 100,
            CycleSurvivalRules.WarmthRate(true, true)));
        return Place(initial.World, a, b, longRoute ? "ravine" : "camp", "cabin");
    }

    private static CycleWorld WeatherWorld(bool fatal) {
        ModelTime at = Hour(8);
        var initial = CycleGameFactory.CreateSurvival(at, enableNeeds: true, enableWeather: true, weatherSeed: 0, characterCount: 2);
        var activity = new CycleActivity(CycleActivityKind.Wait, 1, at, 4 * CycleTime.Hour, Hour(12));
        var a = new CycleCharacter("a", "Exposed explorer", activity: activity, nextActivityId: 2,
            survival: new CycleSurvival(at, 100 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
                (fatal ? 9 : 29) * CycleSurvival.Scale, CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate,
                CycleSurvivalRules.WarmthRate(false, true), warnedMask: fatal ? 4 : 0));
        var b = new CycleCharacter("b", "Sheltered explorer", activity: activity, nextActivityId: 2,
            survival: CycleSurvival.Create(at, 100, 100, 60, CycleSurvivalRules.WarmthRate(true, true)));
        return Place(initial.World, a, b, "camp", "cabin", new CycleEnvironment(0, 0, at));
    }

    private static CycleWorld Place(CycleWorld initial, CycleCharacter a, CycleCharacter b, string aPlace, string bPlace,
        CycleEnvironment? weather = null) {
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph,
            [new(new("a"), new(aPlace)), new(new("b"), new(bPlace))]);
        CycleVisit Visit(string actor, string place) => new(actor, place, initial.Time,
            initial.Resources.Where(resource => CycleExpeditionMap.GetResourcePlace(resource.Id) == place));
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [Visit("a", aPlace), Visit("b", bPlace)], environment: weather);
        return CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [a, b], expedition: expedition), initial.Time);
    }

    private static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> RecoveryWithoutPlanning(CycleDurableHistory history) =>
        new(history, new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
            (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental), CycleEngine.Validate);

    private static void AssertWorldEqual(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> expected,
        SimulationKernel<CycleWorld, CyclePrediction, CycleFact> actual) {
        Assert.Equal(expected.Cursor, actual.Cursor);
        Assert.Equal(expected.World.DescribeState(), actual.World.DescribeState());
        Assert.Equal(MaterialSnapshot(expected.World.Business.InteractionStates), MaterialSnapshot(actual.World.Business.InteractionStates));
        Assert.Equal(expected.World.Expedition!.Spatial, actual.World.Expedition!.Spatial);
        Assert.Equal(expected.World.Spatial!.Contacts, actual.World.Spatial!.Contacts);
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
    }

    private static async Task UntilState(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> predicate) {
        for (int steps = 0; steps < 100; steps++) {
            if (predicate(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The expected two-character boundary was not reached within 100 commits.");
    }

    private sealed class DepartureScript(bool longRoute) : ICycleCharacterController {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            int call = _calls.GetValueOrDefault(actor) + 1;
            _calls[actor] = call;
            CycleCommand command = call == 1 ? CycleCommand.Handoff("private-" + actor) : call == 2
                ? actor == "a" ? CycleCommand.Go(longRoute ? "trail-4" : "trail-2") : CycleCommand.Wait(8)
                : throw new InvalidOperationException("The fixture must stop before requesting another decision.");
            Assert.Null(context.Check(command));
            return ValueTask.FromResult(new CycleControllerOperation($"call/{actor}/{call}", CycleCommandParser.Format(command), $"duo/{actor}/{call}"));
        }
    }

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This recovery and pending activity must not request any new decision.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending recovery must not forecast, select participants or draw weather.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending recovery must apply the recorded fact without replanning.");
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, Func<CycleFact, bool> matches)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(matches)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
