using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CharacterMapKnowledgeTests {
    [Fact]
    public async Task Visiting_both_endpoints_does_not_confirm_the_connection_or_reveal_a_journey_destination() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        PlaygroundExpedition expedition = initial.Expedition!;
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: new(expedition.ScenarioId,
            expedition.Spatial, expedition.Visits.Append(new("a", "ridge", Hour(7), [])))), initial.Time);
        PlaygroundObservation frozen = PlaygroundDecisionContext.Observe(initial, "a");
        Assert.Equal(new[] { "camp", "ridge" }, frozen.KnownMap.Select(p => p.Id));
        Assert.All(frozen.KnownMap.SelectMany(p => p.Entrances), entrance => Assert.Null(entrance.KnownDestinationId));
        Assert.Null(Assert.Single(frozen.Passages, e => e.Id == "e-b9").KnownDestinationName);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-b9")));
        await Until(kernel, PlaygroundEventKind.MoveStarted);
        Assert.Empty(kernel.World.Expedition!.ConfirmedPassages);
        for (int draw = 0; draw < 3; draw++) {
            PlaygroundObservation moving = PlaygroundDecisionContext.Observe(kernel.World, "a");
            Assert.Null(moving.Journey!.KnownDestinationName);
            Assert.All(moving.KnownMap.SelectMany(p => p.Entrances), entrance => Assert.Null(entrance.KnownDestinationId));
        }
        PlaygroundWorld departed = kernel.World;
        await Until(kernel, PlaygroundEventKind.MoveArrived);
        Assert.True(kernel.World.Expedition!.KnowsPassage("a", "trail-2"));
        Assert.Empty(departed.Expedition!.ConfirmedPassages); // A branch/root held before exploration remains unchanged.
        PlaygroundObservation arrived = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Equal("林间营地", Assert.Single(arrived.Passages, e => e.Id == "e-f2").KnownDestinationName);
        Assert.Equal("ridge", Assert.Single(arrived.KnownMap.Single(p => p.Id == "camp").Entrances, e => e.Id == "e-b9").KnownDestinationId);
        Assert.Equal("camp", Assert.Single(arrived.KnownMap.Single(p => p.Id == "ridge").Entrances, e => e.Id == "e-f2").KnownDestinationId);
        Assert.Null(Assert.Single(frozen.Passages, e => e.Id == "e-b9").KnownDestinationName);
    }

    [Fact]
    public async Task Actually_reaching_both_ends_via_a_detour_does_not_confirm_an_untravelled_shortcut() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(
            PlaygroundCommand.Go("e-c7"), PlaygroundCommand.Go("e-y2"), PlaygroundCommand.Go("e-t4")));
        for (int leg = 0; leg < 3; leg++) { await Until(kernel, PlaygroundEventKind.MoveArrived); }
        Assert.Equal("ridge", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.False(kernel.World.Expedition!.KnowsPassage("a", "trail-2"));
        PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.Null(Assert.Single(observation.Passages, e => e.Id == "e-f2").KnownDestinationName);
        Assert.Null(Assert.Single(observation.KnownMap.Single(p => p.Id == "camp").Entrances, e => e.Id == "e-b9").KnownDestinationId);
    }

    [Theory]
    [InlineData("e north", false)]
    [InlineData("e\tnorth", false)]
    [InlineData("e north", true)]
    [InlineData("e\tnorth", true)]
    [InlineData("trail-1", false)]
    [InlineData("trail-1", true)]
    public void Content_rejects_unparseable_or_internal_entrance_identity(string id, bool reverse) {
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.LivingWorld();
        Assert.Throws<InvalidDataException>(() => definition.Change(passages: definition.Passages.Select(p => p.Id != "trail-1" ? p :
            reverse ? p with { ReverseEntranceId = id } : p with { EntranceId = id })));
    }

    [Fact]
    public void Entrances_are_independent_stable_ids_and_remote_or_internal_guesses_share_one_rejection() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        string? Check(PlaygroundCommand command) => PlaygroundActions.Check(world, "a", command);
        string? expected = Check(PlaygroundCommand.Go("missing"));
        Assert.NotNull(expected);
        Assert.Equal(expected, Check(PlaygroundCommand.Go("trail-1")));
        Assert.Equal(expected, Check(PlaygroundCommand.Go("e-n4")));
        PlaygroundPassageDefinition forward = world.Definition.GetEntrances("camp").Single(p => p.Id == "trail-1");
        PlaygroundPassageDefinition reverse = world.Definition.GetEntrances("stream").Single(p => p.Id == "trail-1");
        Assert.Equal(forward.EntranceId, reverse.ReverseEntranceId);
        Assert.Equal(forward.ReverseEntranceId, reverse.EntranceId);
        Assert.NotEqual(forward.EntranceId, reverse.EntranceId);
        Assert.All(world.Definition.Passages, passage => {
            Assert.DoesNotContain(passage.Id, passage.EntranceId, StringComparison.Ordinal);
            Assert.DoesNotContain(passage.Id, passage.ReverseEntranceId, StringComparison.Ordinal);
        });
        Assert.Throws<InvalidDataException>(() => world.Definition.Change(passages: world.Definition.Passages.Select(p =>
            p.Id == "trail-1" ? p with { ReverseEntranceId = p.EntranceId } : p)));
        Assert.NotEqual(world.Definition.Fingerprint, world.Definition.Change(passages: world.Definition.Passages.Select(p =>
            p.Id == "trail-1" ? p with { ReverseEntranceId = "e-new" } : p)).Fingerprint);
    }

    [Fact]
    public void Saved_confirmation_without_personal_visits_to_both_endpoints_is_rejected() {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        PlaygroundExpedition expedition = world.Expedition!;
        PlaygroundBusinessState invalid = world.Business.Replace(world.Time, expedition: new(expedition.ScenarioId,
            expedition.Spatial, expedition.Visits, confirmedPassages: [new("a", "trail-1")]));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Rebuild(invalid, world.Time));
        Assert.Contains("own visits", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other-actor")]
    [InlineData("other-passage")]
    [InlineData("missing")]
    public async Task Arrival_knowledge_delta_is_causally_validated_before_installation(string tampering) {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-c7")));
        await Until(kernel, PlaygroundEventKind.MoveStarted);
        await Until(kernel, PlaygroundEventKind.MoveArrived, (before, fact) => {
            PlaygroundConfirmedPassage[] delta = tampering switch {
                "other-actor" => [new("b", "trail-1")],
                "other-passage" => [new("a", "trail-2")],
                _ => []
            };
            PlaygroundFact forged = CopyKnowledge(fact, delta);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
                kernel.LastCompletion!.Event.TargetInstant, forged));
            Assert.Contains("complete arrival", error.Message, StringComparison.Ordinal);
        });
    }

    private static PlaygroundFact CopyKnowledge(PlaygroundFact f, IEnumerable<PlaygroundConfirmedPassage> delta) => new(
        f.Kind, f.At, f.ActorId, f.ResourceId, f.Light, f.ChangedResources, f.Script, f.ChangedCharacters,
        f.LastServedActorId, f.Result, f.SpatialFacts, f.Visits, f.Environment, f.ToolCallId, f.OperationRecordId,
        f.GatherStopReason, f.ObservedEvents, f.InteractionChanges, f.RawInput, f.OperationDisposition,
        f.TargetActorId, f.SpeechText, f.ItemId, f.ItemCount, f.GoalOperation, f.Economy, f.MoneyAmount, f.TradeChanges, delta);

    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        PlaygroundEventKind kind, Action<PlaygroundWorld, PlaygroundFact>? inspect = null) {
        for (int step = 0; step < 40; step++) {
            PlaygroundWorld before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            PlaygroundFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
            if (fact.Kind == kind) { inspect?.Invoke(before, fact); return; }
        }
        Assert.Fail($"Did not reach {kind}.");
    }
}
