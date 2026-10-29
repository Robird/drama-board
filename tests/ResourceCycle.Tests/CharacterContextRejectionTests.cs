using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CharacterContextRejectionTests {
    [Theory]
    [InlineData("  use  ")]
    [InlineData("use item.food")]
    [InlineData(":exit")]
    public async Task Invalid_submission_commits_exact_input_without_acknowledging_warning_or_changing_world(string raw) {
        var seed = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var actor = seed.GetCharacter("a");
        var warned = CycleSurvival.Create(seed.Time, food: 25).Warn(seed.Time);
        var body = actor.WithSurvival(warned);
        var world = CycleEngine.Rebuild(seed.Business.Replace(seed.Time, changedCharacters: [body]), seed.Time);
        var source = new RawController(raw);
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel);
        var before = kernel.World;
        var opening = Context(before);
        Assert.NotEqual(0, before.GetCharacter("a").Survival!.PendingMask);

        await Advance(kernel);

        CycleFact fact = Last(kernel);
        Assert.Equal(CycleEventKind.OperationRejected, fact.Kind);
        Assert.Equal(CycleOperationDisposition.Rejected, fact.OperationDisposition);
        Assert.Equal(raw, fact.RawInput);
        Assert.Equal(before.Time, kernel.World.Time);
        Assert.Equal(before.Business.LastServedActorId, kernel.World.Business.LastServedActorId);
        Assert.Equal(MaterialSnapshot(before.Expedition), MaterialSnapshot(kernel.World.Expedition));
        Assert.Same(before.Expedition!.Spatial, kernel.World.Expedition!.Spatial);
        Assert.Same(before.Expedition.Environment, kernel.World.Expedition.Environment);
        Assert.Same(before.Light, kernel.World.Light);
        Assert.Same(before.Script, kernel.World.Script);
        Assert.Equal(before.Resources, kernel.World.Resources);
        var afterActor = kernel.World.GetCharacter("a");
        Assert.Equal(before.GetCharacter("a").DecisionGeneration + 1, afterActor.DecisionGeneration);
        Assert.Same(before.GetCharacter("a").Survival, afterActor.Survival);
        Assert.Same(before.GetCharacter("a").Backpack, afterActor.Backpack);
        Assert.Same(before.GetCharacter("a").Activity, afterActor.Activity);
        Assert.Equal(before.GetCharacter("a").Notices, afterActor.Notices);
        Assert.Equal(before.GetCharacter("a").LastResult, afterActor.LastResult);
        Assert.Empty(fact.ObservedEvents);
        Assert.Empty(fact.Visits);
        Assert.Empty(fact.SpatialFacts);
        var context = Context(kernel.World);
        Assert.Equal(opening.ContextSequence, context.ContextSequence);
        Assert.Equal(CharacterContextStatus.Open, context.Status);
        Assert.Same(opening.LatestObservation, context.LatestObservation);
        var item = Assert.Single(context.Items);
        Assert.True(item.IsRejected);
        Assert.Equal(raw, item.RawInput);
        Assert.Equal("provider/1", item.ToolCallId);
        Assert.Equal("raw/1", item.OperationRecordId);
        Assert.Equal(CycleOperationResults.Describe(fact), item.Result);
        Assert.Null(item.Observation);
        Assert.Empty(item.Experiences);
        Assert.Equal(1, item.OperationSequence);
        Assert.Equal(1, source.Calls);
        Assert.Equal(CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
    }

    [Fact]
    public async Task Distant_and_unknown_targets_receive_same_visible_rejection() {
        var world = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var source = new RawController("gather berries 1", "gather secret-missing 1", "go trail-7", "go secret-missing");
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel);
        var results = new List<string>();
        for (int index = 0; index < 4; index++) {
            await Advance(kernel);
            Assert.Equal(CycleOperationDisposition.Rejected, Last(kernel).OperationDisposition);
            results.Add(Context(kernel.World).Items[^1].Result!);
        }
        Assert.Equal(results[0], results[1]);
        Assert.Equal(results[2], results[3]);
        Assert.DoesNotContain("溪流湾", string.Join('\n', results));
        Assert.DoesNotContain("secret-missing", string.Join('\n', results));
    }

    [Fact]
    public async Task Window_counts_success_and_release_but_preserves_successes_and_stable_item_identities() {
        var world = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var source = new RawController("bad-1", "bad-2", "bad-3", "bad-4", "  use   item.water  ",
            "bad-6", "handoff  留给下轮的记录", "handoff  更新后的记录", "  go   trail-1  ");
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel);
        for (int index = 0; index < 3; index++) { await Advance(kernel); }
        CharacterContext firstThree = Context(kernel.World);
        Assert.Equal(new long?[] { 1, 2, 3 }, firstThree.Items.Select(item => item.OperationSequence));
        await Advance(kernel);
        Assert.Equal(new long?[] { 2, 3, 4 }, Context(kernel.World).Items.Select(item => item.OperationSequence));
        Assert.Same(firstThree.Items[1], Context(kernel.World).Items[0]);
        Assert.Same(firstThree.Items[2], Context(kernel.World).Items[1]);
        await Advance(kernel);
        var supply = Context(kernel.World).Items[^1];
        Assert.False(supply.IsRejected);
        Assert.Equal("  use   item.water  ", supply.RawInput);
        Assert.Equal(new long?[] { 3, 4, 5 }, Context(kernel.World).Items.Select(item => item.OperationSequence));
        Assert.NotEmpty(supply.Experiences);
        await Advance(kernel);
        Assert.Equal(new long?[] { 4, 5, 6 }, Context(kernel.World).Items.Select(item => item.OperationSequence));
        await Advance(kernel);
        Assert.Equal(new long?[] { 5, 6, 7 }, Context(kernel.World).Items.Select(item => item.OperationSequence));
        await Advance(kernel);
        Assert.Equal(new long?[] { 5, 6, 7, 8 }, Context(kernel.World).Items.Select(item => item.OperationSequence));
        await Advance(kernel); // Release is operation 9 and must prune rejection 6 before closing.
        CharacterInteractionState state = kernel.World.Business.InteractionStates["a"];
        Assert.Equal(CharacterContextStatus.Closed, state.CurrentContext!.Status);
        Assert.Equal(9, state.CurrentContext.LastOperationSequence);
        Assert.Equal(new long?[] { 5, 7, 8 }, state.CurrentContext.Items.Select(item => item.OperationSequence));
        Assert.Equal(new long[] { 5, 7, 8 }, state.CurrentContext.Items.Select(item => item.ItemSequence));
        Assert.Same(supply, state.CurrentContext.Items[0]);
        Assert.Equal(9, state.CurrentContext.NextItemSequence);
        Assert.Equal("  go   trail-1  ", state.PendingAdvance!.RawInput);
        Assert.Equal("provider/9", state.PendingAdvance.ToolCallId);
        Assert.Equal("raw/9", state.PendingAdvance.OperationRecordId);
        Assert.Equal("更新后的记录", state.PendingAdvance.Handoff);
        Assert.DoesNotContain(state.CurrentContext.Items, item => item.OperationSequence == 9);
        Assert.Equal(3, firstThree.Items.Count);
    }

    [Fact]
    public async Task Rejection_keeps_service_opportunity_and_other_actor_feedback_does_not_age_rejections() {
        var at = Hour(8);
        var notice = new CycleNotice(CycleNoticeKind.ResourceDepleted, "sunflower", null, "fixture", at, "Past warning");
        var waiting = new CycleActivity(CycleActivityKind.Wait, 1, at, 2 * CycleTime.Hour, Hour(10));
        var world = CycleWorld.CreatePlayable(at, [
            new("a", "Alice", activity: waiting, notices: [notice], nextActivityId: 2),
            new("b", "Bob", activity: waiting, notices: [notice], nextActivityId: 2)
        ], [Sun(at)]);
        var source = new RawController("bad-1", "bad-2", "cancel", "bad-b", "cancel");
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel); // A opens.
        await Advance(kernel); // A rejects twice without rotating to B.
        await Advance(kernel);
        Assert.Equal(new[] { "a", "a" }, source.Actors);
        Assert.Null(kernel.World.Business.LastServedActorId);
        Assert.Single(kernel.World.GetCharacter("a").Notices);
        await Advance(kernel); // A succeeds; only now B may be served.
        Assert.Equal("a", kernel.World.Business.LastServedActorId);
        var aBefore = Context(kernel.World);
        Assert.Equal(new long?[] { 1, 2, 3 }, aBefore.Items.Select(item => item.OperationSequence));
        await Advance(kernel); // B opens; no controller call.
        await Advance(kernel); // B rejects without changing A.
        Assert.Same(aBefore, Context(kernel.World));
        Assert.Equal("a", kernel.World.Business.LastServedActorId);
        await Advance(kernel); // B cancels; A gets a public snapshot-only update.
        Assert.Equal(new[] { "a", "a", "a", "b", "b" }, source.Actors);
        var aAfter = Context(kernel.World);
        Assert.Equal(aBefore.LastOperationSequence, aAfter.LastOperationSequence);
        Assert.Equal(new long?[] { 1, 2, 3, null }, aAfter.Items.Select(item => item.OperationSequence));
        Assert.Same(aBefore.Items[0], aAfter.Items[0]);
        Assert.Same(aBefore.Items[1], aAfter.Items[1]);
        Assert.Null(aAfter.Items[^1].ToolCallId);
        Assert.False(Assert.Single(aAfter.LatestObservation.OtherCharacters).Waiting);
    }

    [Fact]
    public async Task Natural_public_change_does_not_advance_operation_window() {
        var world = Solo(Hour(8), [Sun(Hour(8), stock: 4)]);
        var source = new RawController("bad-1", "bad-2", "bad-3");
        var kernel = CycleEngine.CreateKernel(Memory(world), characterController: source);
        await Advance(kernel);
        for (int index = 0; index < 3; index++) { await Advance(kernel); }
        var before = kernel.World;
        var oldContext = Context(before);
        // Exercise the independent world -> perception path at the same model instant.
        // Scheduling still grants the Open controller its opportunity; no controller call is fabricated.
        var regenerated = before.GetResource("sunflower").Change(5, 0, null);
        var fact = new CycleFact(CycleEventKind.ResourceRegenerated, before.Time, resourceId: "sunflower", changedResources: [regenerated]);
        fact = CycleInteractionRules.Enrich(before, fact);
        var after = CycleEngine.Fold(before, new LogicalInstant(before.Time, kernel.Cursor.LastInstant!.Value.CausalOrdinal + 1), fact);
        var current = Context(after);
        Assert.Equal(oldContext.LastOperationSequence, current.LastOperationSequence);
        Assert.Equal(new long?[] { 1, 2, 3, null }, current.Items.Select(item => item.OperationSequence));
        Assert.All(current.Items.Take(3), item => Assert.True(item.IsRejected));
        Assert.Contains(current.Items[^1].Experiences, item => item.Event.Kind == CycleObservedEventKind.ResourceRegenerated);
        Assert.Equal(3, source.Calls);
    }

    internal sealed class RawController(params string[] commands) : ICycleCharacterController {
        private readonly Queue<string> _remaining = new(commands);
        public int Calls { get; private set; }
        public List<string> Actors { get; } = [];
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            Actors.Add(context.Observation.Actor.Id);
            return ValueTask.FromResult(new CycleControllerOperation($"provider/{Calls}", _remaining.Dequeue(), $"raw/{Calls}"));
        }
    }
    private static CharacterContext Context(CycleWorld world) => world.Business.InteractionStates["a"].CurrentContext!;
    private static CycleFact Last(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
}
