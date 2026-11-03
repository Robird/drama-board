using DramaBoard.Goals;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using System.Reflection;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Raw goal commands exercise the same authoritative decision path as both controllers.</summary>
public sealed class GoalIntegrationTests {
    private const string Create = """create-goal {"title":"private promise","criteria":"deliver two waters","focus":true}""";

    [Fact]
    public async Task Editing_query_noop_and_rejection_record_feedback_without_physical_effects() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: true).World;
        var commands = new RawController([Create,
            """update-goal {"id":"G1","progress":"waiting for companion"}""",
            """update-goal {"id":"G1","progress":"waiting for companion"}""",
            """show-goal {"id":"G1"}""",
            """create-goal {"title":"bad parent","criteria":"anything","parent":"G999","linkReason":"missing","focus":true}"""]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: commands);
        string physical = Physical(initial);
        await NextOperation(kernel);
        var afterCreate = Goals(kernel.World);
        Assert.Equal("G1", Assert.Single(afterCreate.Goals).Id);
        Assert.Equal(new[] { "G1" }, afterCreate.Focus);
        Assert.Equal(2, afterCreate.NextGoalId);
        CharacterContext first = Context(kernel.World);
        Assert.Empty(first.Opening.Observation.Goals!.Goals);
        Assert.Single(first.LatestObservation.Goals!.Goals);
        await NextOperation(kernel);
        string edited = MaterialSnapshot(Goals(kernel.World));
        foreach (bool rejected in new[] { false, false, true }) {
            await NextOperation(kernel);
            PlaygroundFact fact = Last(kernel);
            Assert.Equal(rejected ? PlaygroundOperationDisposition.Rejected : PlaygroundOperationDisposition.Succeeded,
                fact.OperationDisposition);
            Assert.Equal(edited, MaterialSnapshot(Goals(kernel.World)));
            Assert.NotNull(fact.GoalOperation);
            Assert.Equal(!rejected, fact.GoalOperation.Success);
            Assert.False(fact.GoalOperation.Changed);
            Assert.Empty(fact.ObservedEvents);
            Assert.False(string.IsNullOrWhiteSpace(PlaygroundOperationResults.Describe(fact)));
        }
        Assert.Equal(physical, Physical(kernel.World));
        Assert.Empty(afterCreate.Goals[0].Revisions);
        Assert.Null(afterCreate.Goals[0].Progress); // A later edit cannot mutate the older material.
        Assert.Equal(5, Context(kernel.World).LastOperationSequence);
        Assert.Equal(CharacterContextStatus.Open, Context(kernel.World).Status);
        Assert.Equal(1, Context(kernel.World).ContextSequence);
        Assert.All(Context(kernel.World).Items, item => Assert.NotNull(item.OperationRecordId));
        Assert.Equal(1, Goals(kernel.World).NextLinkId);
    }

    [Fact]
    public async Task Success_rotates_rejection_does_not_and_private_edits_do_not_notify_other_actor() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: 2).World;
        var commands = new RawController([Create, """focus-goal {"id":"G999"}""", "show-goal {}"],
            ["""create-goal {"title":"b-private","criteria":"b-only"}""", "show-goal {}"]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: commands);
        await NextOperation(kernel); // A creates, B will be served next.
        await NextOperation(kernel); // B creates its own G1.
        CharacterInteractionState bBefore = kernel.World.Business.InteractionStates["b"];
        string bMaterial = MaterialSnapshot(bBefore);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        await NextOperation(kernel); // A rejected; it must be served again.
        Assert.Equal(PlaygroundOperationDisposition.Rejected, Last(kernel).OperationDisposition);
        Assert.Equal("b", kernel.World.Business.LastServedActorId);
        await NextOperation(kernel); // A's successful show rotates.
        Assert.Equal("a", kernel.World.Business.LastServedActorId);
        Assert.Equal(bMaterial, MaterialSnapshot(kernel.World.Business.InteractionStates["b"]));
        Assert.DoesNotContain(Last(kernel).InteractionChanges, change => change.ActorId == "b");
        Assert.Empty(Last(kernel).ObservedEvents);
        await NextOperation(kernel); // B's show.
        Assert.Equal(new[] { "a", "b", "a", "a", "b" }, commands.Actors);
        Assert.Equal("private promise", Assert.Single(Goals(kernel.World, "a").Goals).Title);
        Assert.Equal("b-private", Assert.Single(Goals(kernel.World, "b").Goals).Title);
        Assert.DoesNotContain("private promise", MaterialSnapshot(kernel.World.Business.InteractionStates["b"]), StringComparison.Ordinal);
        Assert.DoesNotContain("b-private", MaterialSnapshot(kernel.World.Business.InteractionStates["a"]), StringComparison.Ordinal);
        Assert.Equal(Physical(initial), Physical(kernel.World));
    }

    [Fact]
    public async Task Shared_sources_focus_correction_and_cleanup_follow_command_contract() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false).World;
        var commands = new RawController([
            """create-goal {"title":"parent A","criteria":"A"}""",
            """create-goal {"title":"parent B","criteria":"B"}""",
            """create-goal {"title":"shared investigation","criteria":"obtain answer","parent":"G1","linkReason":"A needs answer","focus":true}""",
            """link-goal {"parent":"G2","child":"G3","reason":"B needs answer"}""",
            """pause-goal {"id":"G1","reason":"wait for partner"}""",
            """unlink-goal {"id":"L1"}""",
            """finish-goal {"id":"G3","outcome":"completed","note":"No water here"}""",
            """forget-goal {"id":"G3"}""",
            """reopen-goal {"id":"G3","reason":"original answer unconfirmed"}""",
            """update-goal {"id":"G3","criteria":"obtain a reliable answer","reason":"clarify required evidence"}"""
        ]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: commands);
        for (int i = 0; i < 5; i++) { await NextOperation(kernel); }
        Assert.Equal(new[] { "G1", "G3" }, Goals(kernel.World).Focus);
        Assert.Equal(GoalStatus.Open, Goals(kernel.World).Goals.Single(goal => goal.Id == "G3").Status);
        await NextOperation(kernel);
        Assert.Equal(new[] { "G3" }, Goals(kernel.World).Focus);
        await NextOperation(kernel);
        Assert.Empty(Goals(kernel.World).Focus);
        string completed = MaterialSnapshot(Goals(kernel.World));
        await NextOperation(kernel);
        Assert.Equal(PlaygroundOperationDisposition.Rejected, Last(kernel).OperationDisposition);
        Assert.Equal(completed, MaterialSnapshot(Goals(kernel.World)));
        await NextOperation(kernel);
        await NextOperation(kernel);
        Goal goal = Goals(kernel.World).Goals.Single(goal => goal.Id == "G3");
        Assert.Equal(GoalStatus.Open, goal.Status);
        Assert.Null(goal.EndNote);
        Assert.Equal(2, goal.Revisions.Count);
        Assert.Equal("No water here", goal.Revisions[0].OldEndNote);
        Assert.Equal("obtain answer", goal.Revisions[1].OldCriteria);
        Assert.Equal("G2", Assert.Single(Goals(kernel.World).Links).ParentId);
        Assert.Equal(4, Goals(kernel.World).NextGoalId);
        Assert.Equal(3, Goals(kernel.World).NextLinkId);
        Assert.Equal(Physical(initial), Physical(kernel.World));
    }

    [Fact]
    public async Task Goals_survive_handoff_supply_release_and_next_context_with_old_snapshot_frozen() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var commands = new RawController([Create,
            """pause-goal {"id":"G1","reason":"companion absent","revisit":"when reunited"}""",
            "handoff one-time route hint", "use item.water", "wait 1", "show-goal {}"]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: commands);
        await NextOperation(kernel);
        CharacterContext saved = Context(kernel.World);
        await NextOperation(kernel);
        string paused = MaterialSnapshot(Goals(kernel.World));
        for (int i = 0; i < 3; i++) { await NextOperation(kernel); }
        Assert.Equal(CharacterContextStatus.Closed, Context(kernel.World).Status);
        Assert.Equal(paused, MaterialSnapshot(Goals(kernel.World)));
        await NextOperation(kernel);
        Assert.Equal(2, Context(kernel.World).ContextSequence);
        Assert.Equal(paused, MaterialSnapshot(Context(kernel.World).Opening.Observation.Goals));
        Assert.Equal(paused, MaterialSnapshot(Goals(kernel.World)));
        Assert.Equal("one-time route hint", Context(kernel.World).Opening.PreviousAdvance!.Handoff);
        Assert.Equal(GoalStatus.Open, Assert.Single(saved.LatestObservation.Goals!.Goals).Status);
        Assert.Equal(new[] { "G1" }, saved.LatestObservation.Goals.Focus);
        Assert.Equal(Hour(9), kernel.World.Time);
    }

    [Fact]
    public async Task Goal_edit_during_travel_warning_preserves_arrival_pending_warning_and_physical_state() {
        PlaygroundWorld original = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        PlaygroundCharacter actor = original.GetCharacter("a");
        PlaygroundWorld initial = PlaygroundEngine.Rebuild(original.Business.Replace(original.Time,
            changedCharacters: [actor.WithSurvival(PlaygroundSurvival.Create(original.Time, food: 80, water: 33, warmth: 80))]), original.Time);
        var commands = new RawController(["go e-b9", Create, "show-goal {}"]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: commands);
        await NextOperation(kernel);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.SurvivalWarning);
        Assert.True(PlaygroundActions.IsTravelling(kernel.World, "a"));
        var journey = Assert.IsType<TraversingLocation>(PlaygroundActions.Location(kernel.World, "a"));
        Assert.Equal(Hour(10), journey.ArrivalDue);
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        string physical = Physical(kernel.World);
        await NextOperation(kernel);
        Assert.Equal(PlaygroundOperationDisposition.Succeeded, Last(kernel).OperationDisposition);
        Assert.Equal(physical, Physical(kernel.World));
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        await NextOperation(kernel);
        Assert.Equal(physical, Physical(kernel.World));
        Assert.Equal(3, commands.Actors.Count);
        Assert.Single(Goals(kernel.World).Goals);
    }

    [Fact]
    public async Task Basic_gathering_mode_rejects_goal_commands_without_enabling_the_capability() {
        PlaygroundWorld initial = Solo(Hour(8));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new RawController([Create]));
        await NextOperation(kernel);
        Assert.Equal(PlaygroundOperationDisposition.Rejected, Last(kernel).OperationDisposition);
        Assert.Empty(Goals(kernel.World).Goals);
        Assert.Null(PlaygroundDecisionContext.Observe(kernel.World, "a").Goals);
        Assert.Equal(Physical(initial), Physical(kernel.World));
    }

    [Theory]
    [InlineData("raw-input")]
    [InlineData("receipt-changed")]
    [InlineData("receipt-id")]
    [InlineData("after-goals")]
    public async Task Fold_rejects_goal_facts_whose_recorded_command_receipt_or_after_state_disagree(string corruption) {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new RawController([Create]));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48))); // Commit the empty opening.
        PlaygroundWorld before = kernel.World;
        string untouched = MaterialSnapshot(before.Business.InteractionStates);
        await NextOperation(kernel);
        PlaygroundFact original = Last(kernel);
        var instant = kernel.LastCompletion!.Event.TargetInstant;
        Assert.Equal(MaterialSnapshot(kernel.World.Business.InteractionStates),
            MaterialSnapshot(PlaygroundEngine.Fold(before, instant, original).Business.InteractionStates));

        PlaygroundFact forged;
        if (corruption == "raw-input") {
            forged = original.WithOperation(new(original.ToolCallId!,
                """create-goal {"title":"different promise","criteria":"other outcome","focus":true}""",
                original.OperationRecordId!));
        } else if (corruption == "after-goals") {
            CharacterInteractionState state = Assert.Single(original.InteractionChanges);
            GoalSet goals = new([new("G1", "silently substituted purpose", "different result")],
                focus: ["G1"], nextGoalId: 2);
            goals.Validate(); // The forgery is structurally valid; it is not the submitted effect.
            CharacterContext context = state.CurrentContext!;
            CharacterContextItem item = Assert.Single(context.Items);
            PlaygroundObservation observation = CopyObservationWithGoals(item.Observation!, goals);
            var changedItem = new CharacterContextItem(item.ItemSequence, item.OperationSequence, item.RawInput,
                item.ToolCallId, item.OperationRecordId, item.Result, item.Experiences, observation,
                item.IsRejected, item.GoalOperation);
            var changedContext = new CharacterContext(context.ContextSequence, context.Status, context.Opening,
                [changedItem], context.NextItemSequence, context.LastOperationSequence);
            forged = original.WithInteractions([new(state.ActorId, changedContext, state.PendingAdvance,
                state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence, goals)]);
        } else {
            GoalReceipt receipt = original.GoalOperation!;
            GoalReceipt changed = corruption == "receipt-changed"
                ? new(receipt.Kind, true, false, receipt.Message, receipt.GoalId,
                    focusBefore: receipt.FocusAfter, focusAfter: receipt.FocusAfter)
                : new(receipt.Kind, receipt.Success, receipt.Changed, receipt.Message, "G99", receipt.LinkId,
                    receipt.ChangedFields, receipt.FocusBefore, receipt.FocusAfter);
            changed.Validate();
            forged = (PlaygroundFact)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
            typeof(PlaygroundFact).GetField("_goalOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(forged, changed);
        }
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before, instant, forged));
        Assert.Equal(untouched, MaterialSnapshot(before.Business.InteractionStates));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversize-prefix")]
    public async Task Malformed_goal_input_gets_a_typed_persistent_refusal_without_world_effects(string malformed) {
        string raw = malformed switch {
            "duplicate" => """create-goal {"title":"one","title":"two","criteria":"answer"}""",
            "unknown" => """create-goal {"title":"one","criteria":"answer","otherActor":"b"}""",
            _ => new string(' ', GoalLimits.MaximumCommandCodeUnits + 1) + Create
        };
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new RawController([raw]));
        await NextOperation(kernel);
        PlaygroundFact fact = Last(kernel);
        Assert.Equal(PlaygroundOperationDisposition.Rejected, fact.OperationDisposition);
        Assert.NotNull(fact.GoalOperation);
        Assert.False(fact.GoalOperation.Success);
        Assert.False(fact.GoalOperation.Changed);
        Assert.Equal(raw, fact.RawInput);
        Assert.Empty(fact.ObservedEvents);
        Assert.Empty(Goals(kernel.World).Goals);
        Assert.Equal(1, Goals(kernel.World).NextGoalId);
        Assert.Equal(Physical(initial), Physical(kernel.World));
        Assert.Equal(raw, Assert.Single(Context(kernel.World).Items).RawInput);
    }

    // Clone a frozen material only for a corruption fixture; keep unrelated fields exact without
    // invoking observation projection or mutating the valid baseline's shared immutable instance.
    private static PlaygroundObservation CopyObservationWithGoals(PlaygroundObservation source, GoalSet goals) {
        var copy = (PlaygroundObservation)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, null)!;
        typeof(PlaygroundObservation).GetField("_goals", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(copy, goals);
        return copy;
    }

    private static GoalSet Goals(PlaygroundWorld world, string actor = "a") => world.Business.InteractionStates[actor].Goals;
    private static CharacterContext Context(PlaygroundWorld world) => world.Business.InteractionStates["a"].CurrentContext!;
    private static PlaygroundFact Last(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static string Physical(PlaygroundWorld world) => MaterialSnapshot(new {
        world.Time, world.Light, world.Resources, world.Script, world.Expedition,
        Actors = world.Characters.Select(actor => new { actor.Id, actor.Backpack, actor.Activity, actor.NextActivityId,
            actor.Survival, actor.Communication, actor.Notices, actor.Ending })
    });
    private static Task NextOperation(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        UntilFact(kernel, fact => fact.OperationRecordId is not null);
    private static async Task UntilFact(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundFact, bool> stop) {
        for (int i = 0; i < 100; i++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
            PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
            Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
            if (kernel.LastCompletion!.Event.Facts.Any(stop)) { return; }
        }
        Assert.Fail("Expected goal operation boundary was not reached.");
    }
    private sealed class RawController(IEnumerable<string> a, IEnumerable<string>? b = null) : IPlaygroundCharacterController {
        private readonly Dictionary<string, Queue<string>> _commands = new() { ["a"] = new(a), ["b"] = new(b ?? []) };
        public List<string> Actors { get; } = [];
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            Actors.Add(actor);
            if (!_commands[actor].TryDequeue(out string? command)) { throw new InvalidOperationException($"Unexpected goal decision for {actor}."); }
            return ValueTask.FromResult(new PlaygroundControllerOperation($"goal-call/{Actors.Count}", command, $"goal-operation/{Actors.Count}"));
        }
    }
}
