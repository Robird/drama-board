using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class FollowingObservationTests {
    [Fact]
    public async Task Group_departure_records_each_travellers_experience_without_transferring_operation_or_private_map() {
        PlaygroundWorld initial = Group();
        PlaygroundExpedition expedition = initial.Expedition!;
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: new(expedition.ScenarioId,
            expedition.Spatial, expedition.Visits.Append(new("a", "ridge", Hour(7), [])))), initial.Time);
        PlaygroundObservation frozen = PlaygroundDecisionContext.Observe(initial, "a");
        Assert.Equal(new[] { "b", "c" }, frozen.Followers.Keys.Order().ToArray());
        Assert.All(frozen.OtherCharacters, other => Assert.Equal("a", other.FollowingLeaderId));
        Assert.Null(Assert.Single(frozen.Passages, passage => passage.Id == "e-b9").KnownDestinationName);

        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-b9")));
        var (before, departure) = await UntilFact(kernel, PlaygroundEventKind.MoveStarted);
        Assert.Equal("a", departure.ActorId);
        Assert.Equal("call_fixture_1", departure.ToolCallId);
        Assert.Equal(3, departure.SpatialFacts.OfType<TraversalStartedFact>().Count());
        foreach (string actor in new[] { "a", "b", "c" }) {
            PlaygroundObservedEvent moved = Assert.Single(departure.ObservedEvents,
                entry => entry.ObserverId == actor && entry.Kind == PlaygroundObservedEventKind.MoveStarted);
            Assert.Contains(moved.Effects, effect => effect.Key == new PlaygroundStateKey("location", actor, "arrival"));
            Assert.Null(moved.SubjectName);
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, actor);
            Assert.Empty(observation.OtherCharacters);
            Assert.NotNull(observation.Journey);
            if (actor != "a") {
                Assert.Equal("a", observation.FollowingLeaderId);
                Assert.Equal("阿青", observation.FollowingLeaderName);
                Assert.Null(observation.Journey!.KnownDestinationName);
                Assert.DoesNotContain(moved.Effects, effect => effect.Key.Category == "continuity");
                Assert.Null(kernel.World.Business.InteractionStates[actor].PendingAdvance);
            }
        }
        Assert.Null(PlaygroundDecisionContext.Observe(kernel.World, "a").Journey!.KnownDestinationName);
        Assert.Null(frozen.ArrivalDue); // Previously delivered material remains frozen.
        Assert.Equal("camp", frozen.PlaceId);

        // Even a structurally well-formed event stream cannot omit a carried traveller's experience.
        PlaygroundObservedEvent[] missingFollower = departure.ObservedEvents.Where(entry => entry.ObserverId != "b")
            .Select((entry, ordinal) => Copy(entry, ordinal)).ToArray();
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion!.Event.TargetInstant, departure.WithObservedEvents(missingFollower)));
    }

    [Fact]
    public async Task Joining_delivers_relation_changes_to_busy_leader_without_waking_or_forging_their_operation() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: 2).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            Commands(PlaygroundCommand.Wait(2), PlaygroundCommand.Follow("a")));
        var (before, joined) = await UntilFact(kernel, PlaygroundEventKind.FollowStarted);
        Assert.Equal("b", joined.ActorId);
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "a"));
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(PlaygroundCommandKind.Wait, kernel.World.Business.InteractionStates["a"].PendingAdvance!.Command.Kind);
        Assert.Equal(PlaygroundCommandKind.Follow, kernel.World.Business.InteractionStates["b"].PendingAdvance!.Command.Kind);
        PlaygroundObservedEvent leader = Assert.Single(joined.ObservedEvents, entry => entry.ObserverId == "a");
        Assert.Equal(PlaygroundObservedEventKind.FollowStarted, leader.Kind);
        Assert.Equal(new PlaygroundStateKey("following", "a", "follower:b"), Assert.Single(leader.Effects).Key);
        Assert.Contains(joined.ObservedEvents, entry => entry.ObserverId == "b" && entry.Effects.Any(effect =>
            effect.Key == new PlaygroundStateKey("following", "b", "leader") && effect.After.Text == "a"));
        Assert.Equal("阿白", PlaygroundDecisionContext.Observe(kernel.World, "a").Followers["b"]);
        Assert.Empty(PlaygroundDecisionContext.Observe(before, "a").Followers);
    }

    [Fact]
    public async Task Dismissal_changes_all_member_material_without_assigning_them_a_command() {
        PlaygroundWorld initial = Group();
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.DismissFollowers()));
        var (before, dismissed) = await UntilFact(kernel, PlaygroundEventKind.FollowersDismissed);
        Assert.Equal(3, dismissed.ObservedEvents.Count);
        Assert.All(dismissed.ObservedEvents, entry => Assert.Equal(PlaygroundObservedEventKind.FollowersDismissed, entry.Kind));
        foreach (string actor in new[] { "a", "b", "c" }) {
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(kernel.World, actor);
            Assert.Null(observation.FollowingLeaderId);
            Assert.Empty(observation.Followers);
            Assert.True(PlaygroundActions.NeedsDecision(kernel.World, actor));
        }
        Assert.Equal(2, PlaygroundDecisionContext.Observe(before, "a").Followers.Count);
        Assert.Equal("a", dismissed.ActorId);
    }

    [Fact]
    public async Task Leaders_death_ends_relationship_but_is_never_reported_as_followers_death() {
        PlaygroundWorld initial = Group(characterCount: 2);
        PlaygroundCharacter a = initial.GetCharacter("a");
        PlaygroundSurvival needs = new(Hour(8), 100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate,
            PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters:
            [new PlaygroundCharacter(a.Id, a.Name, survival: needs)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Go("e-b9")));
        var (_, death) = await UntilFact(kernel, PlaygroundEventKind.SurvivalFailed);
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        Assert.Null(kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.IsType<TraversingLocation>(PlaygroundActions.Location(kernel.World, "b"));
        PlaygroundObservedEvent ended = Assert.Single(death.ObservedEvents, entry => entry.ObserverId == "b");
        Assert.Equal(PlaygroundObservedEventKind.FollowEnded, ended.Kind);
        Assert.Equal("Terminal", ended.Detail);
        Assert.DoesNotContain(death.ObservedEvents, entry => entry.ObserverId == "b" &&
            entry.Kind is PlaygroundObservedEventKind.SurvivalFailed or PlaygroundObservedEventKind.ExpeditionEnded);
    }

    private static PlaygroundWorld Group(int characterCount = 3) {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: characterCount).World;
        return PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters:
            initial.Characters.Where(character => character.Id != "a").Select(character =>
                new PlaygroundCharacter(character.Id, character.Name, followingLeaderId: "a"))), initial.Time);
    }

    private static async Task<(PlaygroundWorld Before, PlaygroundFact Fact)> UntilFact(
        SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, PlaygroundEventKind kind) {
        for (int step = 0; step < 40; step++) {
            PlaygroundWorld before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            kernel.World.Validate();
            Assert.Equal(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
            PlaygroundFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
            if (fact.Kind == kind) { return (before, fact); }
        }
        throw new InvalidOperationException($"Did not reach {kind}.");
    }

    private static PlaygroundObservedEvent Copy(PlaygroundObservedEvent entry, int ordinal) => new(ordinal, entry.ObserverId,
        entry.Kind, entry.From, entry.At, entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects, entry.Communication);
}
