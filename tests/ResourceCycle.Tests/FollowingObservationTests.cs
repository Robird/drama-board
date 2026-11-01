using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class FollowingObservationTests {
    [Fact]
    public async Task Group_departure_records_each_travellers_experience_without_transferring_operation_or_private_map() {
        CycleWorld initial = Group();
        CycleExpedition expedition = initial.Expedition!;
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: new(expedition.ScenarioId,
            expedition.Spatial, expedition.Visits.Append(new("a", "ridge", Hour(7), [])))), initial.Time);
        CycleObservation frozen = CycleDecisionContext.Observe(initial, "a");
        Assert.Equal(new[] { "b", "c" }, frozen.Followers.Keys.Order().ToArray());
        Assert.All(frozen.OtherCharacters, other => Assert.Equal("a", other.FollowingLeaderId));
        Assert.Equal("风口山脊", Assert.Single(frozen.Passages, passage => passage.Id == "trail-2").KnownDestinationName);

        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-2")));
        var (before, departure) = await UntilFact(kernel, CycleEventKind.MoveStarted);
        Assert.Equal("a", departure.ActorId);
        Assert.Equal("call_fixture_1", departure.ToolCallId);
        Assert.Equal(3, departure.SpatialFacts.OfType<TraversalStartedFact>().Count());
        foreach (string actor in new[] { "a", "b", "c" }) {
            CycleObservedEvent moved = Assert.Single(departure.ObservedEvents,
                entry => entry.ObserverId == actor && entry.Kind == CycleObservedEventKind.MoveStarted);
            Assert.Contains(moved.Effects, effect => effect.Key == new CycleStateKey("location", actor, "arrival"));
            Assert.Null(moved.SubjectName);
            CycleObservation observation = CycleDecisionContext.Observe(kernel.World, actor);
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
        Assert.Equal("风口山脊", CycleDecisionContext.Observe(kernel.World, "a").Journey!.KnownDestinationName);
        Assert.Null(frozen.ArrivalDue); // Previously delivered material remains frozen.
        Assert.Equal("camp", frozen.PlaceId);

        // Even a structurally well-formed event stream cannot omit a carried traveller's experience.
        CycleObservedEvent[] missingFollower = departure.ObservedEvents.Where(entry => entry.ObserverId != "b")
            .Select((entry, ordinal) => Copy(entry, ordinal)).ToArray();
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before,
            kernel.LastCompletion!.Event.TargetInstant, departure.WithObservedEvents(missingFollower)));
    }

    [Fact]
    public async Task Joining_delivers_relation_changes_to_busy_leader_without_waking_or_forging_their_operation() {
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: 2).World;
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController:
            Commands(CycleCommand.Wait(2), CycleCommand.Follow("a")));
        var (before, joined) = await UntilFact(kernel, CycleEventKind.FollowStarted);
        Assert.Equal("b", joined.ActorId);
        Assert.False(CycleActions.NeedsDecision(kernel.World, "a"));
        Assert.False(CycleActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(CycleCommandKind.Wait, kernel.World.Business.InteractionStates["a"].PendingAdvance!.Command.Kind);
        Assert.Equal(CycleCommandKind.Follow, kernel.World.Business.InteractionStates["b"].PendingAdvance!.Command.Kind);
        CycleObservedEvent leader = Assert.Single(joined.ObservedEvents, entry => entry.ObserverId == "a");
        Assert.Equal(CycleObservedEventKind.FollowStarted, leader.Kind);
        Assert.Equal(new CycleStateKey("following", "a", "follower:b"), Assert.Single(leader.Effects).Key);
        Assert.Contains(joined.ObservedEvents, entry => entry.ObserverId == "b" && entry.Effects.Any(effect =>
            effect.Key == new CycleStateKey("following", "b", "leader") && effect.After.Text == "a"));
        Assert.Equal("阿白", CycleDecisionContext.Observe(kernel.World, "a").Followers["b"]);
        Assert.Empty(CycleDecisionContext.Observe(before, "a").Followers);
    }

    [Fact]
    public async Task Dismissal_changes_all_member_material_without_assigning_them_a_command() {
        CycleWorld initial = Group();
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.DismissFollowers()));
        var (before, dismissed) = await UntilFact(kernel, CycleEventKind.FollowersDismissed);
        Assert.Equal(3, dismissed.ObservedEvents.Count);
        Assert.All(dismissed.ObservedEvents, entry => Assert.Equal(CycleObservedEventKind.FollowersDismissed, entry.Kind));
        foreach (string actor in new[] { "a", "b", "c" }) {
            CycleObservation observation = CycleDecisionContext.Observe(kernel.World, actor);
            Assert.Null(observation.FollowingLeaderId);
            Assert.Empty(observation.Followers);
            Assert.True(CycleActions.NeedsDecision(kernel.World, actor));
        }
        Assert.Equal(2, CycleDecisionContext.Observe(before, "a").Followers.Count);
        Assert.Equal("a", dismissed.ActorId);
    }

    [Fact]
    public async Task Leaders_death_ends_relationship_but_is_never_reported_as_followers_death() {
        CycleWorld initial = Group(characterCount: 2);
        CycleCharacter a = initial.GetCharacter("a");
        CycleSurvival needs = new(Hour(8), 100 * CycleSurvival.Scale, 8 * CycleSurvival.Scale,
            100 * CycleSurvival.Scale, Rate(0), CycleSurvivalRules.WaterRate,
            CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2);
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters:
            [new CycleCharacter(a.Id, a.Name, survival: needs)]), initial.Time);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Go("trail-2")));
        var (_, death) = await UntilFact(kernel, CycleEventKind.SurvivalFailed);
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        Assert.Null(kernel.World.GetCharacter("b").FollowingLeaderId);
        Assert.IsType<TraversingLocation>(CycleActions.Location(kernel.World, "b"));
        CycleObservedEvent ended = Assert.Single(death.ObservedEvents, entry => entry.ObserverId == "b");
        Assert.Equal(CycleObservedEventKind.FollowEnded, ended.Kind);
        Assert.Equal("Terminal", ended.Detail);
        Assert.DoesNotContain(death.ObservedEvents, entry => entry.ObserverId == "b" &&
            entry.Kind is CycleObservedEventKind.SurvivalFailed or CycleObservedEventKind.ExpeditionEnded);
    }

    private static CycleWorld Group(int characterCount = 3) {
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: characterCount).World;
        return CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters:
            initial.Characters.Where(character => character.Id != "a").Select(character =>
                new CycleCharacter(character.Id, character.Name, followingLeaderId: "a"))), initial.Time);
    }

    private static async Task<(CycleWorld Before, CycleFact Fact)> UntilFact(
        SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, CycleEventKind kind) {
        for (int step = 0; step < 40; step++) {
            CycleWorld before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            kernel.World.Validate();
            Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
            CycleFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
            if (fact.Kind == kind) { return (before, fact); }
        }
        throw new InvalidOperationException($"Did not reach {kind}.");
    }

    private static CycleObservedEvent Copy(CycleObservedEvent entry, int ordinal) => new(ordinal, entry.ObserverId,
        entry.Kind, entry.From, entry.At, entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects, entry.Communication);
}
