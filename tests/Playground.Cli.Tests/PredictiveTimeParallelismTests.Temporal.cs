using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

public sealed partial class PredictiveTimeParallelismTests {
    [Fact]
    public async Task Departure_from_the_visible_place_discards_same_time_same_context_candidate() {
        using var run = new Run(TemporalInitial(TemporalHour(10), ["camp", "camp"]));
        PlaygroundDecisionContext expected = Opening(run.History.State, "b");
        Assert.Contains(expected.Observation.OtherCharacters, actor => actor.Id == "a");
        var provider = new HeldClient(Act("handoff stale-before-departure"));
        await using var candidate = new Candidate(expected, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fallback = new ImmediateClient(Act("handoff fresh-after-departure"));
        var gate = new AdoptionClient(candidate, fallback);
        var kernel = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient(Act("go trail-1"))), Controller(gate)));
        await TemporalUntil(kernel, () => run.History.State.Business.InteractionStates["b"].DraftHandoff == "fresh-after-departure");
        PlaygroundDecisionContext observed = PlaygroundDecisionContext.Create(run.History.State, "b");
        Assert.Equal(expected.Observation.Time, observed.Observation.Time);
        Assert.Equal(expected.Context.ContextSequence, observed.Context.ContextSequence);
        Assert.DoesNotContain(observed.Observation.OtherCharacters, actor => actor.Id == "a");
        Assert.IsType<TraversingLocation>(PlaygroundActions.Location(run.History.State, "a"));
        Assert.Equal(4, run.History.Cursor.Version.TransitionCount);
        Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(Assert.Single(fallback.Requests)));
        Assert.Equal(1, gate.Discarded);
        Assert.Equal(0, gate.Adopted);
        Assert.Equal("handoff fresh-after-departure", Assert.Single(kernel.LastCompletion!.Event.Facts).RawInput);
        Assert.DoesNotContain("stale-before-departure", run.FinalMaterial());
    }

    [Fact]
    public async Task Remote_staggered_service_discards_input_after_a_new_actor_arrives_at_the_exact_tick() {
        using var run = new Run(TemporalInitial(TemporalHour(10), ["camp", "cabin"]));
        var prelude = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient(Act("go trail-2"), Act("go trail-5"), Act("wait 3"))),
            Controller(new ImmediateClient(Act("wait 5")))));
        await TemporalUntil(prelude, () => run.History.State.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundDecisionContext oldContext = PlaygroundDecisionContext.Create(run.History.State, "b");
        Assert.Equal(TemporalHour(10), oldContext.Observation.Time);
        Assert.Empty(oldContext.Observation.OtherCharacters);
        Assert.Equal("cabin", PlaygroundActions.CurrentPlace(run.History.State, "b"));
        var provider = new HeldClient(Act("handoff stale-before-entrant"));
        await using var candidate = new Candidate(oldContext, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await TemporalUntil(prelude, () => run.History.State.Time == TemporalHour(12) &&
            PlaygroundActions.Location(run.History.State, "a") is TraversingLocation travel && travel.TargetPlaceId.Value == "cabin");
        TraversingLocation travelling = Assert.IsType<TraversingLocation>(PlaygroundActions.Location(run.History.State, "a"));
        Assert.Equal(TemporalHour(14), travelling.ArrivalDue);
        Assert.Equal(StepStatus.BoundaryReached, await prelude.StepAsync(new ModelTime(TemporalHour(14).Ticks - 1)));
        Assert.Same(travelling, PlaygroundActions.Location(run.History.State, "a"));
        Assert.DoesNotContain(PlaygroundDecisionContext.Observe(run.History.State, "b").OtherCharacters, actor => actor.Id == "a");
        Assert.Equal(StepStatus.Committed, await prelude.StepAsync(TemporalHour(14)));
        PlaygroundFact arrival = Assert.Single(prelude.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.MoveArrived, arrival.Kind);
        Assert.Equal("a", arrival.ActorId);
        Assert.Equal(TemporalHour(14), arrival.At);
        Assert.Equal("cabin", PlaygroundActions.CurrentPlace(run.History.State, "a"));
        Assert.Contains(PlaygroundDecisionContext.Observe(run.History.State, "b").OtherCharacters, actor => actor.Id == "a");
        Assert.Equal(CharacterContextStatus.Closed, run.History.State.Business.InteractionStates["b"].CurrentContext!.Status);

        await TemporalUntil(prelude, () => run.History.State.GetCharacter("a").Activity is { Kind: PlaygroundActivityKind.Wait } &&
            run.History.State.Time == TemporalHour(14));
        var fallback = new ImmediateClient(Act("handoff fresh-after-entrant"));
        var gate = new AdoptionClient(candidate, fallback);
        var actual = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient()), Controller(gate)));
        await TemporalUntil(actual, () => run.History.State.Time == TemporalHour(15) &&
            run.History.State.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundObservation fresh = PlaygroundDecisionContext.Create(run.History.State, "b").Observation;
        Assert.Contains(fresh.OtherCharacters, actor => actor.Id == "a");
        Assert.Equal(TemporalHour(15), fresh.Time);
        await TemporalUntil(actual, () => run.History.State.Business.InteractionStates["b"].DraftHandoff == "fresh-after-entrant");
        Assert.Equal(1, gate.Discarded);
        Assert.Equal(0, gate.Adopted);
        Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(Assert.Single(fallback.Requests)));
        Assert.DoesNotContain("stale-before-entrant", run.FinalMaterial());
        Assert.Equal("handoff fresh-after-entrant", Assert.Single(actual.LastCompletion!.Event.Facts).RawInput);
        // This verifies place membership at arrival, not a new mid-route contact notification.
    }

    [Fact]
    public async Task Known_body_and_sunset_evolution_can_be_projected_by_scratch_kernel_without_drawing_future_weather() {
        using var run = new Run(TemporalInitial(TemporalHour(17), ["camp"], bodies: true, weatherAt: TemporalHour(20), seed: 0));
        var prelude = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient(Act("wait 1"))), Controller(new ImmediateClient())));
        await TemporalUntil(prelude, () => run.History.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Closed);
        PlaygroundWorld source = run.History.State;
        KernelCursor sourceCursor = run.History.Cursor;
        PlaygroundSurvival body = source.GetCharacter("a").Survival!;
        PlaygroundEnvironment environment = source.Expedition!.Environment!;
        CharacterContext savedContext = source.Business.InteractionStates["a"].CurrentContext!;
        string sourceState = source.DescribeState();
        string sourceCommit = PlaygroundDurableHistory.FormatCommitId(run.History.CompletedStateCommitId);
        var scratchHistory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(source, sourceCursor);
        var forbidden = new ForbiddenController();
        var scratch = PlaygroundEngine.CreateKernel(scratchHistory, characterController: forbidden);
        await TemporalUntil(scratch, () => scratch.World.Time == TemporalHour(18) &&
            scratch.World.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
        Assert.Equal(0, forbidden.Calls);
        Assert.Contains(scratchHistory.CompletedEvents.SelectMany(occurrence => occurrence.Facts), fact => fact.Kind == PlaygroundEventKind.LightChanged);
        Assert.DoesNotContain(scratchHistory.CompletedEvents.SelectMany(occurrence => occurrence.Facts), fact => fact.Kind == PlaygroundEventKind.WeatherResolved);
        PlaygroundDecisionContext predicted = PlaygroundDecisionContext.Create(scratch.World, "a");
        Assert.False(predicted.Observation.IsDay);
        Assert.Equal(66m, predicted.Observation.Survival!.Warmth);
        Assert.Equal(92m, predicted.Observation.Survival.Water);
        Assert.Equal(-12, predicted.Observation.Survival.WarmthPerHour);
        Assert.Equal(TemporalHour(18), predicted.Observation.Time);
        Assert.Equal(TemporalHour(17), savedContext.LatestObservation.Time);
        Assert.Equal(60m, savedContext.LatestObservation.Survival!.Warmth);
        Assert.Same(source, run.History.State);
        Assert.Equal(sourceCursor, run.History.Cursor);
        Assert.Equal(sourceCommit, PlaygroundDurableHistory.FormatCommitId(run.History.CompletedStateCommitId));
        Assert.Same(body, source.GetCharacter("a").Survival);
        Assert.Same(savedContext, source.Business.InteractionStates["a"].CurrentContext);
        Assert.Same(environment, source.Expedition.Environment);
        Assert.Equal(0UL, environment.Seed);
        Assert.Equal(0, environment.Sequence);
        Assert.Equal(TemporalHour(20), environment.NextDue);
        Assert.Equal(sourceState, source.DescribeState());

        var provider = new HeldClient(Act("handoff projected-sunset"));
        await using var candidate = new Candidate(predicted, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var fallback = new ImmediateClient();
        var gate = new AdoptionClient(candidate, fallback);
        var actual = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(gate), Controller(new ImmediateClient())));
        await TemporalUntil(actual, () => run.History.State.Business.InteractionStates["a"].DraftHandoff == "projected-sunset");
        Assert.Equal(1, gate.Adopted);
        Assert.Equal(0, gate.Discarded);
        Assert.Empty(fallback.Requests);
        Assert.Equal(TemporalHour(18), run.History.State.Time);
        Assert.Equal(66 * PlaygroundSurvival.Scale, run.History.State.GetCharacter("a").Survival!.WarmthUnits);
        Assert.Equal(0, run.History.State.Expedition!.Environment!.Sequence);
        Assert.Equal(TemporalHour(20), run.History.State.Expedition.Environment.NextDue);
    }

    [Fact]
    public async Task Same_visible_current_input_hides_weather_seed_and_actual_cold_or_calm_invalidates_old_candidates() {
        var oldInputs = new List<string>();
        var actualWarmth = new List<decimal>();
        foreach (ulong seed in new[] { 0UL, 1UL }) {
            using var run = new Run(TemporalInitial(TemporalHour(17), ["camp"], bodies: true, weatherAt: TemporalHour(18), seed: seed));
            var prelude = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
                Controller(new ImmediateClient(Act("wait 1"))), Controller(new ImmediateClient())));
            await TemporalUntil(prelude, () => run.History.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
            PlaygroundWorld source = run.History.State;
            PlaygroundEnvironment environment = source.Expedition!.Environment!;
            var provider = new HeldClient(Act("handoff stale-pre-weather"));
            await using var candidate = new Candidate(PlaygroundDecisionContext.Create(source, "a"), provider);
            await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Release.SetResult();
            await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            oldInputs.Add(RequestValue(candidate.Request!));
            Assert.Same(source, run.History.State);
            Assert.Same(environment, source.Expedition.Environment);
            Assert.Equal(seed, environment.Seed);
            Assert.Equal(0, environment.Sequence);
            Assert.Equal(TemporalHour(18), environment.NextDue);
            Assert.Null(environment.LastAt);

            await TemporalUntil(prelude, () => run.History.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Closed);
            var fallback = new ImmediateClient(Act("handoff fresh-weather"));
            var gate = new AdoptionClient(candidate, fallback);
            var actual = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
                Controller(gate), Controller(new ImmediateClient())));
            await TemporalUntil(actual, () => run.History.State.Time == TemporalHour(18) &&
                run.History.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
            PlaygroundObservation observation = PlaygroundDecisionContext.Create(run.History.State, "a").Observation;
            PlaygroundWeatherObservation weather = observation.Weather!;
            Assert.Equal(seed == 0 ? PlaygroundWeatherKind.ColdWind : PlaygroundWeatherKind.Calm, weather.LastKind);
            Assert.Equal(TemporalHour(18), weather.LastAt);
            Assert.Equal(seed == 0 ? 10m : 0m, weather.LastActualWarmthLoss);
            Assert.False(observation.IsDay);
            Assert.Equal(seed == 0 ? 56m : 66m, observation.Survival!.Warmth);
            actualWarmth.Add(observation.Survival.Warmth);
            PlaygroundEnvironment installed = run.History.State.Expedition!.Environment!;
            Assert.Equal(seed, installed.Seed);
            Assert.Equal(1, installed.Sequence);
            Assert.Equal(TemporalHour(20), installed.NextDue);
            Assert.Equal(0, environment.Sequence); // Real Plan installed a replacement, not a source mutation.
            Assert.Equal(TemporalHour(18), environment.NextDue);
            await TemporalUntil(actual, () => run.History.State.Business.InteractionStates["a"].DraftHandoff == "fresh-weather");
            Assert.Equal(0, gate.Adopted);
            Assert.Equal(1, gate.Discarded);
            Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(Assert.Single(fallback.Requests)));
            Assert.DoesNotContain("stale-pre-weather", run.FinalMaterial());
            Assert.Equal("handoff fresh-weather", Assert.Single(actual.LastCompletion!.Event.Facts).RawInput);
        }
        Assert.Equal(oldInputs[0], oldInputs[1]);
        Assert.Equal(new[] { 56m, 66m }, actualWarmth);
        // The random address is deliberately absent from model input. Equal visible material
        // alone cannot certify equal future material; the host can deterministically project
        // weather when it uses the actual seed, stream and sequence on an independent scratch.
    }

    [Fact]
    public async Task Same_target_time_and_context_still_discard_a_counterfactual_that_misses_the_new_entrant() {
        using var run = new Run(TemporalInitial(TemporalHour(10), ["camp", "cabin"]));
        var prelude = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient(Act("go trail-2"), Act("go trail-5"), Act("wait 3"))),
            Controller(new ImmediateClient(Act("wait 5")))));
        await TemporalUntil(prelude, () => run.History.State.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Closed);
        PlaygroundWorld source = run.History.State;
        KernelCursor sourceCursor = run.History.Cursor;
        string sourceState = source.DescribeState();
        string sourceCommit = PlaygroundDurableHistory.FormatCommitId(run.History.CompletedStateCommitId);
        Assert.Equal(TemporalHour(10), source.Time);
        Assert.Equal("ridge", Assert.IsType<TraversingLocation>(PlaygroundActions.Location(source, "a")).TargetPlaceId.Value);

        // Explicit speculative counterfactual: once a reaches ridge at 12 it waits there.
        // This is an assumption about an unknown future choice, not a certified safe projection.
        var counterHistory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(source, sourceCursor);
        var counterfactual = PlaygroundEngine.CreateKernel(counterHistory,
            characterController: Controller(new ImmediateClient(Act("wait 4"))));
        await TemporalUntil(counterfactual, () => counterfactual.World.Time == TemporalHour(15) &&
            counterfactual.World.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundDecisionContext expected = PlaygroundDecisionContext.Create(counterfactual.World, "b");
        Assert.Equal("ridge", PlaygroundActions.CurrentPlace(counterfactual.World, "a"));
        Assert.Empty(expected.Observation.OtherCharacters);
        Assert.Equal(2, expected.Context.ContextSequence);
        Assert.Same(source, run.History.State);
        Assert.Equal(sourceCursor, run.History.Cursor);
        Assert.Equal(sourceCommit, PlaygroundDurableHistory.FormatCommitId(run.History.CompletedStateCommitId));
        Assert.Equal(sourceState, source.DescribeState());

        var provider = new HeldClient(Act("handoff stale-counterfactual-membership"));
        await using var candidate = new Candidate(expected, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The real a chooses go instead, arriving at b's place at 14 before b wakes at 15.
        await TemporalUntil(prelude, () => run.History.State.Time == TemporalHour(14) &&
            run.History.State.GetCharacter("a").Activity is { Kind: PlaygroundActivityKind.Wait });
        Assert.Equal("cabin", PlaygroundActions.CurrentPlace(run.History.State, "a"));
        var fallback = new ImmediateClient(Act("handoff fresh-membership-same-target"));
        var gate = new AdoptionClient(candidate, fallback);
        var actual = PlaygroundEngine.CreateKernel(run.History, characterController: new ActualController(run,
            Controller(new ImmediateClient()), Controller(gate)));
        await TemporalUntil(actual, () => run.History.State.Time == TemporalHour(15) &&
            run.History.State.Business.InteractionStates["b"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundDecisionContext observed = PlaygroundDecisionContext.Create(run.History.State, "b");
        Assert.Equal(expected.Observation.Time, observed.Observation.Time);
        Assert.Equal(expected.Context.ContextSequence, observed.Context.ContextSequence);
        Assert.Equal(expected.Context.LastOperationSequence, observed.Context.LastOperationSequence);
        Assert.Contains(observed.Observation.OtherCharacters, actor => actor.Id == "a");
        await TemporalUntil(actual, () => run.History.State.Business.InteractionStates["b"].DraftHandoff == "fresh-membership-same-target");
        Assert.Equal(0, gate.Adopted);
        Assert.Equal(1, gate.Discarded);
        Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(Assert.Single(fallback.Requests)));
        Assert.DoesNotContain("stale-counterfactual-membership", run.FinalMaterial());
        Assert.Equal("handoff fresh-membership-same-target", Assert.Single(actual.LastCompletion!.Event.Facts).RawInput);
    }

    [Fact]
    public async Task Same_target_time_and_context_still_discard_calm_counterfactual_when_actual_seed_produces_cold() {
        using var actualRun = new Run(TemporalInitial(TemporalHour(17), ["camp"], bodies: true, weatherAt: TemporalHour(18), seed: 0));
        PlaygroundWorld actualSource = actualRun.History.State;
        PlaygroundEnvironment actualSourceEnvironment = actualSource.Expedition!.Environment!;
        KernelCursor sourceCursor = actualRun.History.Cursor;
        string sourceCommit = PlaygroundDurableHistory.FormatCommitId(actualRun.History.CompletedStateCommitId);
        // Independent replacement initial state, deliberately assuming a different random seed.
        // The host's real seeded weather is deterministic; this is an incorrect speculative
        // branch used to test full-input validation, not evidence that weather is unprojectable.
        using var alternative = new Run(TemporalInitial(TemporalHour(17), ["camp"], bodies: true, weatherAt: TemporalHour(18), seed: 1));
        var alternateKernel = PlaygroundEngine.CreateKernel(alternative.History, characterController: new ActualController(alternative,
            Controller(new ImmediateClient(Act("wait 1"))), Controller(new ImmediateClient())));
        await TemporalUntil(alternateKernel, () => alternateKernel.World.Time == TemporalHour(18) &&
            alternateKernel.World.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundDecisionContext expected = PlaygroundDecisionContext.Create(alternative.History.State, "a");
        Assert.Equal(PlaygroundWeatherKind.Calm, expected.Observation.Weather!.LastKind);
        Assert.Equal(66m, expected.Observation.Survival!.Warmth);
        Assert.Equal(2, expected.Context.ContextSequence);
        Assert.Same(actualSource, actualRun.History.State);
        Assert.Same(actualSourceEnvironment, actualRun.History.State.Expedition!.Environment);
        Assert.Equal(sourceCursor, actualRun.History.Cursor);
        Assert.Equal(sourceCommit, PlaygroundDurableHistory.FormatCommitId(actualRun.History.CompletedStateCommitId));
        Assert.Equal(0UL, actualSourceEnvironment.Seed);
        Assert.Equal(0, actualSourceEnvironment.Sequence);
        Assert.Equal(TemporalHour(18), actualSourceEnvironment.NextDue);
        Assert.Null(actualSourceEnvironment.LastAt);

        var provider = new HeldClient(Act("handoff stale-calm-same-target"));
        await using var candidate = new Candidate(expected, provider);
        await provider.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Release.SetResult();
        await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var actualPrelude = PlaygroundEngine.CreateKernel(actualRun.History, characterController: new ActualController(actualRun,
            Controller(new ImmediateClient(Act("wait 1"))), Controller(new ImmediateClient())));
        await TemporalUntil(actualPrelude, () => actualPrelude.World.Time == TemporalHour(18) &&
            actualPrelude.World.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
        PlaygroundDecisionContext observed = PlaygroundDecisionContext.Create(actualRun.History.State, "a");
        Assert.Equal(expected.Observation.Time, observed.Observation.Time);
        Assert.Equal(expected.Context.ContextSequence, observed.Context.ContextSequence);
        Assert.Equal(expected.Context.LastOperationSequence, observed.Context.LastOperationSequence);
        Assert.Equal(PlaygroundWeatherKind.ColdWind, observed.Observation.Weather!.LastKind);
        Assert.Equal(56m, observed.Observation.Survival!.Warmth);
        Assert.Equal(expected.Observation.Survival.Water, observed.Observation.Survival.Water);
        Assert.Equal(expected.Observation.Survival.Food, observed.Observation.Survival.Food);
        Assert.Equal(expected.Observation.IsDay, observed.Observation.IsDay);
        Assert.Equal(0, actualSourceEnvironment.Sequence);
        var fallback = new ImmediateClient(Act("handoff fresh-cold-same-target"));
        var gate = new AdoptionClient(candidate, fallback);
        var actual = PlaygroundEngine.CreateKernel(actualRun.History, characterController: new ActualController(actualRun,
            Controller(gate), Controller(new ImmediateClient())));
        await TemporalUntil(actual, () => actualRun.History.State.Business.InteractionStates["a"].DraftHandoff == "fresh-cold-same-target");
        Assert.Equal(0, gate.Adopted);
        Assert.Equal(1, gate.Discarded);
        Assert.NotEqual(RequestValue(candidate.Request!), RequestValue(Assert.Single(fallback.Requests)));
        Assert.DoesNotContain("stale-calm-same-target", actualRun.FinalMaterial());
        Assert.Equal("handoff fresh-cold-same-target", Assert.Single(actual.LastCompletion!.Event.Facts).RawInput);
    }

    private static ModelTime TemporalHour(int hour) => new(hour * PlaygroundTime.Hour);

    private static (PlaygroundWorld World, KernelCursor Cursor) TemporalInitial(ModelTime at, string[] places,
        bool bodies = false, ModelTime? weatherAt = null, ulong seed = 0) {
        var initial = PlaygroundGameFactory.CreateSurvival(at, enableNeeds: false, enableWeather: false, characterCount: places.Length);
        PlaygroundCharacter[] actors = places.Select((place, index) => new PlaygroundCharacter(new[] { "a", "b", "c" }[index],
            "Temporal " + index, survival: bodies ? PlaygroundSurvival.Create(at, food: 100, water: 100, warmth: 60,
                warmthRate: PlaygroundSurvivalRules.WarmthRate(PlaygroundExpeditionMap.GetPlace(place).Sheltered, PlaygroundTime.IsDay(at))) : null)).ToArray();
        GraphSpatialState spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph,
            actors.Select((actor, index) => new EntityPlacement(new(actor.Id), new(places[index]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((actor, index) => new PlaygroundVisit(actor.Id, places[index], at,
                initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[index]))),
            environment: weatherAt is { } next ? new(seed, 0, next) : null);
        var business = new PlaygroundBusinessState(at, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
        return (PlaygroundEngine.Rebuild(business, at), initial.Cursor);
    }

    private static async Task TemporalUntil(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<bool> done) {
        for (int step = 0; step < 80; step++) {
            if (done()) { return; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        Assert.Fail("Expected temporal boundary was not reached within the declared transition bound.");
    }
}
