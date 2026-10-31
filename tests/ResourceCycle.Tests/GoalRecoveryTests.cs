using DramaBoard.Goals;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using System.Reflection;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Real E/S files with publication faults; goal effects and identities are never regenerated during recovery.</summary>
public sealed class GoalRecoveryTests {
    private const string Create = """create-goal {"title":"original promise","criteria":"deliver water"}""";
    private const string Finish = """finish-goal {"id":"G1","outcome":"completed","note":"believed delivered"}""";

    public static IEnumerable<object[]> PublicationCases() {
        foreach (string scenario in new[] { "create-link-focus", "reopen", "show", "forget" }) {
            foreach (string boundary in new[] { "before-event", "after-event", "before-state", "after-state" }) {
                yield return [scenario, boundary];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PublicationCases))]
    public async Task Published_goal_operation_recovers_exactly_once_with_stable_identity_and_material(string scenario, string boundary) {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        string[] setup = scenario is "reopen" or "forget" ? [Create, Finish] : [Create];
        string target = scenario switch {
            "create-link-focus" => """create-goal {"title":"shared enquiry","criteria":"record answer","parent":"G1","linkReason":"choose method","focus":true}""",
            "reopen" => """reopen-goal {"id":"G1","reason":"delivery never happened"}""",
            "show" => """show-goal {"id":"G1"}""",
            "forget" => """forget-goal {"id":"G1"}""",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var baselineController = new CountedController(setup, target);
        var baseline = CycleEngine.CreateKernel(Memory(initial), characterController: baselineController);
        await UntilTarget(baseline);
        string expectedGoals = MaterialSnapshot(baseline.World.Business.InteractionStates["a"].Goals);
        string expectedMaterial = MaterialSnapshot(baseline.World.Business.InteractionStates["a"]);
        CycleFact expectedFact = Assert.Single(baseline.LastCompletion!.Event.Facts);
        string expectedResult = CycleOperationResults.Describe(expectedFact);
        string? beforeGoals = null;
        KernelCursor beforeCursor = default;
        var first = new CountedController(setup, target);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(new CrashAtTarget(history, boundary), characterController: first);
            for (int step = 0; step < 30 && !kernel.IsFaulted; step++) {
                beforeGoals = MaterialSnapshot(history.State.Business.InteractionStates["a"].Goals);
                beforeCursor = history.Cursor;
                try { await Advance(kernel); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(kernel.IsFaulted);
            Assert.Equal(setup.Length + 1, first.Calls);
            Assert.Equal(boundary == "after-state" ? expectedGoals : beforeGoals,
                MaterialSnapshot(history.State.Business.InteractionStates["a"].Goals));
        }

        string recoveredMaterial;
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            bool pending = boundary is "after-event" or "before-state";
            Assert.Equal(pending, history.PendingEvent is not null);
            if (boundary != "after-state") { Assert.Equal(beforeCursor, history.Cursor); }
            var forbidden = new ForbiddenController();
            var recovery = CycleEngine.CreateKernel(history, characterController: forbidden);
            Assert.Equal(pending, recovery.RecoverPending());
            Assert.Equal(0, forbidden.Calls);
            Assert.False(recovery.RecoverPending());
            if (boundary == "before-event") {
                Assert.Equal(beforeGoals, MaterialSnapshot(recovery.World.Business.InteractionStates["a"].Goals));
                Assert.Empty(history.ReadCommittedOperations(["goal/target"]));
                var retry = new CountedController([], target);
                recovery = CycleEngine.CreateKernel(history, characterController: retry);
                await UntilTarget(recovery);
                Assert.Equal(1, retry.Calls);
            }
            Assert.Equal(expectedGoals, MaterialSnapshot(recovery.World.Business.InteractionStates["a"].Goals));
            Assert.Equal(expectedMaterial, MaterialSnapshot(recovery.World.Business.InteractionStates["a"]));
            Assert.Equal(baseline.Cursor, recovery.Cursor);
            var commit = Assert.Single(history.ReadCommittedOperations(["goal/target"]));
            Assert.Equal("goal-call/target", commit.ToolCallId);
            Assert.Equal(expectedResult, commit.Result);
            Assert.Equal(CycleOperationDisposition.Succeeded, commit.Disposition);
            Assert.Equal(MaterialSnapshot(initial.GetCharacter("a").Backpack), MaterialSnapshot(recovery.World.GetCharacter("a").Backpack));
            Assert.Equal(initial.Time, recovery.World.Time);
            Assert.Equal(CycleEngine.Rebuild(recovery.World.Business, recovery.World.Time).Forecasts.ToString(), recovery.World.Forecasts.ToString());
            recoveredMaterial = MaterialSnapshot(recovery.World.Business.InteractionStates["a"]);
        }

        using var final = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(final.PendingEvent);
        Assert.Equal(recoveredMaterial, MaterialSnapshot(final.State.Business.InteractionStates["a"]));
        var noController = new ForbiddenController();
        var finalKernel = CycleEngine.CreateKernel(final, characterController: noController);
        Assert.False(finalKernel.RecoverPending());
        Assert.Equal(0, noController.Calls);
        GoalSet savedGoals = final.State.Business.InteractionStates["a"].Goals;
        Assert.Equal(baseline.World.Business.InteractionStates["a"].Goals.NextGoalId, savedGoals.NextGoalId);
        Assert.Equal(baseline.World.Business.InteractionStates["a"].Goals.NextLinkId, savedGoals.NextLinkId);
        if (scenario == "reopen") { Assert.Single(Assert.Single(savedGoals.Goals).Revisions); }
        if (scenario == "create-link-focus") {
            Assert.Equal(new[] { "G1", "G2" }, savedGoals.Focus);
            Assert.Equal("L1", Assert.Single(savedGoals.Links).Id);
        }
    }

    [Fact]
    public async Task S_cold_open_retains_local_counters_after_forget_and_frozen_old_observations() {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        CharacterContext old;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var controller = new CountedController([Create, Finish], """forget-goal {"id":"G1"}""");
            var kernel = CycleEngine.CreateKernel(history, characterController: controller);
            await UntilTarget(kernel);
            old = kernel.World.Business.InteractionStates["a"].CurrentContext!;
            Assert.Empty(kernel.World.Business.InteractionStates["a"].Goals.Goals);
            Assert.Equal(2, kernel.World.Business.InteractionStates["a"].Goals.NextGoalId);
            Assert.Equal("G1", Assert.Single(old.Items[0].Observation!.Goals!.Goals).Id);
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        var next = new CountedController([], Create, "next-target");
        var resumed = CycleEngine.CreateKernel(reopened, characterController: next);
        Assert.False(resumed.RecoverPending());
        await UntilTarget(resumed, "goal/next-target");
        GoalSet goals = resumed.World.Business.InteractionStates["a"].Goals;
        Assert.Equal("G2", Assert.Single(goals.Goals).Id);
        Assert.Equal(3, goals.NextGoalId);
        Assert.Equal(1, goals.NextLinkId);
        Assert.Equal("G1", Assert.Single(old.Items[0].Observation!.Goals!.Goals).Id);
        Assert.Equal("G1", Assert.Single(resumed.World.Business.InteractionStates["a"].CurrentContext!.Items[0].Observation!.Goals!.Goals).Id);
    }

    [Theory]
    [InlineData("finish")]
    [InlineData("revision")]
    public async Task Cold_open_rejects_goal_history_from_beyond_the_frozen_context_boundary(string marker) {
        using var store = new TemporaryStore();
        CycleWorld initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        await Advance(kernel); // Context 1 opening has operation boundary zero.
        CharacterInteractionState state = kernel.World.Business.InteractionStates["a"];
        CharacterContext context = state.CurrentContext!;
        Assert.Equal(0, context.LastOperationSequence);
        Goal goal = marker == "finish"
            ? new("G1", "claim", "result", GoalStatus.Completed, endNote: "future conclusion",
                finishedContextSequence: context.ContextSequence, finishedOperationSequence: 1)
            : new("G1", "revised", "result", revisions: [new(GoalStatus.Open, "prior", "result", null,
                "future correction", context.ContextSequence, 1)]);
        var goals = new GoalSet([goal], nextGoalId: 2);
        goals.Validate(); // Values and graph are locally legal; only the containing time boundary makes them invalid.
        var observation = (CycleObservation)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Opening.Observation, null)!;
        typeof(CycleObservation).GetField("_goals", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(observation, goals);
        var opening = new CharacterContextOpening(context.Opening.Background, observation, context.Opening.PreviousAdvance,
            context.Opening.DeliveredAt, context.Opening.ActivityState, context.Opening.Experiences);
        var badContext = new CharacterContext(context.ContextSequence, context.Status, opening,
            context.Items, context.NextItemSequence, context.LastOperationSequence);
        var replacement = new CharacterInteractionState(state.ActorId, badContext, state.PendingAdvance,
            state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence, goals);
        CycleBusinessState invalid = kernel.World.Business.Replace(kernel.World.Time, interactionChanges: [replacement]);
        Assert.Throws<InvalidOperationException>(() => new CycleDurableRoot(invalid, kernel.Cursor));

        // A lower-level repository can hydrate data without running domain constructors. The application
        // loader must repeat boundary validation instead of trusting a structurally valid DG graph.
        var candidate = CycleDurableRoot.CreateRevisionCandidate(invalid, kernel.Cursor, CycleGameRevision.Current);
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        using (var repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("future-goal-history", candidate);
        }
        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.Open(store.StorePath));
        Assert.Empty(kernel.World.Business.InteractionStates["a"].Goals.Goals);
        Assert.Empty(context.Opening.Observation.Goals!.Goals);
    }

    private static async Task UntilTarget(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, string targetId = "goal/target") {
        for (int i = 0; i < 30; i++) {
            await Advance(kernel);
            if (kernel.LastCompletion!.Event.Facts.Any(fact => fact.OperationRecordId == targetId)) { return; }
        }
        Assert.Fail("Goal target operation was not reached.");
    }
    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));

    private sealed class CountedController(IEnumerable<string> setup, string target, string targetId = "target") : ICycleCharacterController {
        private readonly Queue<string> _setup = new(setup);
        private bool _targetIssued;
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            if (_setup.TryDequeue(out string? command)) {
                return ValueTask.FromResult(new CycleControllerOperation($"goal-call/setup/{Calls}", command, $"goal/setup/{Calls}"));
            }
            if (_targetIssued) { throw new InvalidOperationException("Target operation requested twice."); }
            _targetIssued = true;
            return ValueTask.FromResult(new CycleControllerOperation("goal-call/" + targetId, target, "goal/" + targetId));
        }
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Recovery must not invoke the character controller.");
        }
    }
    private sealed class InjectedCrash : IOException { }
    private sealed class CrashAtTarget(IOccurrenceHistory<CycleWorld, CycleFact> inner, string boundary)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        private bool _target;
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            _target = occurrence.Facts.Any(fact => fact.OperationRecordId == "goal/target");
            if (_target && boundary == "before-event") { throw new InjectedCrash(); }
            inner.CommitEvent(occurrence);
            if (_target && boundary == "after-event") { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) {
            if (_target && boundary == "before-state") { throw new InjectedCrash(); }
            inner.CommitState(state, cursor);
            if (_target && boundary == "after-state") { throw new InjectedCrash(); }
        }
    }
}
