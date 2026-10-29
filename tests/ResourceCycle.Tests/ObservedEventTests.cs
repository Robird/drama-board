using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ObservedEventTests {
    [Fact]
    public async Task Clamped_supply_records_actual_gain_and_consumption_without_a_time_process() {
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        CycleFact fact = await Advance(kernel);
        CycleObservedEvent used = Assert.Single(fact.ObservedEvents);
        Assert.Equal(CycleObservedEventKind.ItemUsed, used.Kind);
        Assert.Equal("item.water", used.SubjectId);
        Assert.Equal(initial.Time, used.From);
        Assert.Equal(initial.Time, used.At);
        AssertNumeric(Effect(used, "body", "a", "water"), 90, 100);
        AssertNumeric(Effect(used, "inventory", "item.water", "count"), 1, 0);
        Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.Equal("call_fixture_1", fact.ToolCallId);
        Assert.Equal("fixture/1", fact.OperationRecordId);
    }

    [Theory]
    [InlineData(CycleCommandKind.Wait, CycleObservedEventKind.WaitStarted)]
    [InlineData(CycleCommandKind.Gather, CycleObservedEventKind.GatherStarted)]
    [InlineData(CycleCommandKind.Go, CycleObservedEventKind.MoveStarted)]
    [InlineData(CycleCommandKind.Cancel, CycleObservedEventKind.ActivityCancelled)]
    [InlineData(CycleCommandKind.Continue, CycleObservedEventKind.AttentionAcknowledged)]
    public async Task Commands_that_acknowledge_warnings_own_the_confirmation_exactly_once(CycleCommandKind commandKind, CycleObservedEventKind expectedKind) {
        CycleCommand command = commandKind switch {
            CycleCommandKind.Wait => CycleCommand.Wait(1),
            CycleCommandKind.Gather => CycleCommand.Gather("water"),
            CycleCommandKind.Go => CycleCommand.Go("trail-1"),
            CycleCommandKind.Cancel => CycleCommand.Cancel(),
            _ => CycleCommand.Continue()
        };
        CycleActivity? activity = commandKind == CycleCommandKind.Cancel ? Wait(Hour(8), 2) : null;
        CycleWorld initial = World(PendingWater(), activity: activity, place: "stream");
        Assert.Null(CycleActions.Check(initial, "a", command));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(command));
        CycleFact fact = await Advance(kernel);
        CycleObservedEvent action = Assert.Single(fact.ObservedEvents);
        Assert.Equal(expectedKind, action.Kind);
        CycleObservedEffect acknowledgement = Effect(action, "body", "a", "warning-water");
        Assert.Equal(CycleVisibleValue.Flag(true), acknowledgement.Before);
        Assert.Equal(CycleVisibleValue.Flag(false), acknowledgement.After);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supply_and_handoff_preserve_pending_warning_and_do_not_invent_acknowledgement(bool handoff) {
        CycleWorld initial = World(PendingWater(), items: new() { ["item.water"] = 1 });
        CycleCommand command = handoff ? CycleCommand.Handoff("记住水源") : CycleCommand.Use("item.water");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(command));
        CycleFact fact = await Advance(kernel);
        Assert.Equal(handoff ? CycleObservedEventKind.HandoffEdited : CycleObservedEventKind.ItemUsed,
            Assert.Single(fact.ObservedEvents).Kind);
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(e => e.Effects), effect => effect.Key.Field.StartsWith("warning-", StringComparison.Ordinal));
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.Equal(initial.Time, kernel.World.Time);
    }

    [Fact]
    public async Task Weather_keeps_elapsed_change_and_direct_loss_as_separate_chained_effects() {
        CycleWorld initial = World(Needs(warmth: 60), activity: Wait(Hour(8), 4), environment: new(0, 0, Hour(10)));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.WeatherResolved, fact.Kind);
        CycleObservedEvent process = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.BodyChanged);
        CycleObservedEvent weather = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.WeatherResolved);
        Assert.True(process.EventOrdinal < weather.EventOrdinal);
        Assert.Equal(Hour(8), process.From);
        Assert.Equal(Hour(10), process.At);
        Assert.Equal(weather.From, weather.At);
        CycleObservedEffect elapsed = Effect(process, "body", "a", "warmth");
        CycleObservedEffect exposure = Effect(weather, "body", "a", "warmth");
        AssertNumeric(elapsed, 60, 72);
        AssertNumeric(exposure, 72, 62);
        Assert.Equal(elapsed.After, exposure.Before);
        Assert.Equal(10 * CycleSurvival.Scale, Assert.Single(fact.Environment!.LastEffects, effect => effect.ActorId == "a").WarmthLossUnits);
        Assert.Null(fact.ToolCallId);
        Assert.Null(fact.OperationRecordId);
    }

    [Fact]
    public async Task Dusk_records_old_rate_integration_before_new_rate_takes_effect() {
        CycleWorld initial = World(CycleSurvival.Create(Hour(17), 100, 100, 60), activity: Wait(Hour(17), 4));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.LightChanged, fact.Kind);
        CycleObservedEvent elapsed = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.BodyChanged);
        CycleObservedEvent light = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.LightChanged);
        AssertNumeric(Effect(elapsed, "body", "a", "warmth"), 60, 66);
        AssertNumeric(Effect(light, "body", "a", "warmth-rate"), 6, -12);
        Assert.DoesNotContain(light.Effects, e => e.Key == new CycleStateKey("body", "a", "warmth"));
        Assert.True(elapsed.EventOrdinal < light.EventOrdinal);
    }

    [Fact]
    public async Task Final_gather_yield_owns_transfer_then_finish_owns_activity_removal() {
        CycleWorld initial = World(Needs(), place: "stream");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Gather("water")));
        await Advance(kernel);
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.GatherYielded, fact.Kind);
        CycleObservedEvent yielded = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.GatherYielded);
        CycleObservedEvent finished = Assert.Single(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.GatherFinished);
        Assert.True(yielded.EventOrdinal < finished.EventOrdinal);
        AssertNumeric(Effect(yielded, "inventory", "item.water", "count"), 0, 1);
        AssertNumeric(Effect(yielded, "resource", "water", "stock"), 12, 11);
        CycleObservedEffect progress = Effect(yielded, "activity", "a", "completed");
        AssertNumeric(progress, 0, 1);
        CycleObservedEffect removedProgress = Effect(finished, "activity", "a", "completed");
        Assert.Equal(progress.After, removedProgress.Before);
        Assert.Equal(CycleVisibleValue.KnownAbsent, removedProgress.After);
        Assert.Equal(CycleVisibleValue.KnownAbsent, Effect(finished, "activity", "a", "kind").After);
        Assert.DoesNotContain(finished.Effects, e => e.Key.Category is "inventory" or "resource");
        Assert.Null(kernel.World.GetCharacter("a").Activity);
    }

    [Fact]
    public async Task Distant_regeneration_does_not_reveal_its_event_or_stock() {
        CycleWorld initial = World(Needs(), activity: Wait(Hour(8), 4));
        var depleted = new CycleResource("berries", "野浆果", "item.food", true, CycleTime.Hour, 6, 5, Hour(8), harvestTicks: CycleTime.Hour / 2);
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedResources: [depleted]), initial.Time);
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        CycleFact fact = await Advance(kernel);
        Assert.Equal(CycleEventKind.ResourceRegenerated, fact.Kind);
        Assert.Equal("berries", fact.ResourceId);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
        Assert.DoesNotContain(fact.ObservedEvents, e => e.Kind == CycleObservedEventKind.ResourceRegenerated || e.SubjectId == "berries");
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(e => e.Effects), e => e.Key.Category == "resource");
        Assert.All(fact.ObservedEvents, e => Assert.Equal("a", e.ObserverId));
    }

    [Fact]
    public async Task Pending_E_restores_identical_explanation_and_provenance_without_replanning() {
        using var store = new TemporaryStore();
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var planned = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await planned.StepAsync(Hour(240)));
        CycleWorld opened = planned.World;
        KernelCursor openedCursor = planned.Cursor;
        CycleFact expected = await Advance(planned);
        using (var history = CycleDurableHistory.Create(store.StorePath, opened, openedCursor)) {
            history.CommitEvent(planned.LastCompletion!.Event);
        }
        using (var reopened = CycleDurableHistory.Open(store.StorePath)) {
            CycleFact restored = Assert.Single(reopened.PendingEvent!.Facts);
            AssertMaterialsEqual(expected, restored);
            Assert.Empty(reopened.ReadCommittedOperations(["fixture/1"]));
            var recovery = new SimulationKernel<CycleWorld, CyclePrediction, CycleFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
                (before, instant, facts, folded) => CycleEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental), CycleEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(planned.World.DescribeState(), recovery.World.DescribeState());
            Assert.Equal(planned.Cursor, recovery.Cursor);
            CycleCommittedOperation receipt = Assert.Single(reopened.ReadCommittedOperations(["fixture/1"]));
            Assert.Equal(expected.ToolCallId, receipt.ToolCallId);
            Assert.Equal(CycleOperationResults.Describe(expected), receipt.Result);
            Assert.Contains("+10", receipt.Result);
            Assert.DoesNotContain("+45", receipt.Result);
        }
        using var complete = CycleDurableHistory.Open(store.StorePath);
        Assert.Null(complete.PendingEvent);
        Assert.Equal(CycleSurvival.MaximumUnits, complete.State.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal(0, complete.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Fact]
    public async Task Completed_S_lookup_uses_recorded_actual_gain_even_after_later_body_changes() {
        using var store = new TemporaryStore();
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        string result;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Use("item.water"), CycleCommand.Wait(1)));
            result = CycleOperationResults.Describe(await Advance(kernel));
            await Advance(kernel);
            await Advance(kernel);
            Assert.Equal(92 * CycleSurvival.Scale, kernel.World.GetCharacter("a").Survival!.Project(kernel.World.Time).WaterUnits);
        }
        using var reopened = CycleDurableHistory.Open(store.StorePath);
        CycleCommittedOperation recovered = Assert.Single(reopened.ReadCommittedOperations(["fixture/1"]));
        Assert.Equal("call_fixture_1", recovered.ToolCallId);
        Assert.Equal(result, recovered.Result);
        Assert.Contains("+10", recovered.Result);
        Assert.DoesNotContain("+45", recovered.Result);
        Assert.Equal(0, reopened.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Theory]
    [InlineData(CycleCommandKind.Wait, CycleObservedEventKind.WaitStarted)]
    [InlineData(CycleCommandKind.Gather, CycleObservedEventKind.GatherStarted)]
    [InlineData(CycleCommandKind.Go, CycleObservedEventKind.MoveStarted)]
    [InlineData(CycleCommandKind.Continue, CycleObservedEventKind.AttentionAcknowledged)]
    public async Task Accepted_timed_action_owns_handoff_draft_consumption(CycleCommandKind commandKind, CycleObservedEventKind expectedKind) {
        const string handoff = "下一次先查看水源。";
        CycleCommand command = commandKind switch {
            CycleCommandKind.Wait => CycleCommand.Wait(1),
            CycleCommandKind.Gather => CycleCommand.Gather("water"),
            CycleCommandKind.Go => CycleCommand.Go("trail-1"),
            _ => CycleCommand.Continue()
        };
        CycleWorld initial = World(PendingWater(), activity: commandKind == CycleCommandKind.Continue ? Wait(Hour(8), 2) : null,
            place: "stream");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Handoff(handoff), command));
        CycleFact edited = await Advance(kernel);
        CycleObservedEffect written = Effect(Assert.Single(edited.ObservedEvents), "continuity", "a", "draft");
        Assert.Equal(CycleVisibleValue.KnownAbsent, written.Before);
        Assert.Equal(CycleVisibleValue.Literal(handoff), written.After);
        CycleFact accepted = await Advance(kernel);
        CycleObservedEvent action = Assert.Single(accepted.ObservedEvents);
        Assert.Equal(expectedKind, action.Kind);
        CycleObservedEffect consumed = Effect(action, "continuity", "a", "draft");
        Assert.Equal(written.After, consumed.Before);
        Assert.Equal(CycleVisibleValue.KnownAbsent, consumed.After);
        CharacterInteractionState continuity = kernel.World.Business.InteractionStates["a"];
        Assert.Null(continuity.DraftHandoff);
        Assert.Equal(handoff, continuity.PendingAdvance!.Handoff);
        Assert.Equal(initial.Time, kernel.World.Time);
    }

    [Theory]
    [InlineData("after")]
    [InlineData("before")]
    [InlineData("missing-effect")]
    [InlineData("missing-event")]
    [InlineData("ordinal")]
    [InlineData("observer")]
    [InlineData("ownership")]
    [InlineData("duplicate-key")]
    public async Task Fold_rejects_corrupt_observed_material_before_publishing(string corruption) {
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        CycleFact valid = await Advance(kernel);
        CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        CycleObservedEvent original = Assert.Single(valid.ObservedEvents);
        CycleObservedEffect water = Effect(original, "body", "a", "water");
        var effects = original.Effects.ToList();
        switch (corruption) {
            case "after":
                effects[effects.IndexOf(water)] = new(water.Key, water.Before, CycleVisibleValue.Numeric(99 * CycleSurvival.Scale, "点", CycleSurvival.Scale));
                break;
            case "before":
                effects[effects.IndexOf(water)] = new(water.Key, CycleVisibleValue.Numeric(89 * CycleSurvival.Scale, "点", CycleSurvival.Scale), water.After);
                break;
            case "missing-effect": effects.Remove(water); break;
            case "ownership":
                effects.Add(new(new("body", "a", "warmth"), CycleVisibleValue.Numeric(80 * CycleSurvival.Scale, "点", CycleSurvival.Scale),
                    CycleVisibleValue.Numeric(79 * CycleSurvival.Scale, "点", CycleSurvival.Scale)));
                break;
            case "duplicate-key": effects.Add(water); break;
        }
        var corrupted = new CycleObservedEvent(corruption == "ordinal" ? 1 : original.EventOrdinal,
            corruption == "observer" ? "another-character" : original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, effects);
        CycleFact invalid = valid.WithObservedEvents(corruption == "missing-event" ? [] : [corrupted]);
        string before = initial.DescribeState();
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, invalid));
        Assert.Equal(before, initial.DescribeState());
    }

    [Fact]
    public async Task Fold_rejects_invented_intermediate_gain_even_when_effect_chain_reaches_the_correct_endpoint() {
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        CycleFact valid = await Advance(kernel);
        CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        CycleObservedEvent original = Assert.Single(valid.ObservedEvents);
        CycleObservedEffect inventory = Effect(original, "inventory", "item.water", "count");
        CycleVisibleValue invented = CycleVisibleValue.Numeric(2, "份");
        var firstEffects = original.Effects.Where(e => e.Key != inventory.Key).Append(new(inventory.Key, inventory.Before, invented));
        var first = new CycleObservedEvent(0, original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, firstEffects);
        var second = new CycleObservedEvent(1, original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, [new(inventory.Key, invented, inventory.After)]);
        CycleFact invalid = valid.WithObservedEvents([first, second]);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, invalid));
    }

    [Fact]
    public async Task Fold_rejects_event_parameters_that_disagree_with_recorded_supply() {
        CycleWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        CycleFact valid = await Advance(kernel);
        CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        CycleObservedEvent original = Assert.Single(valid.ObservedEvents);
        CycleObservedEvent[] corruptions = [
            new(0, original.ObserverId, original.Kind, original.From, original.At, "item.food", original.SubjectName, original.Detail, original.Effects),
            new(0, original.ObserverId, original.Kind, original.From, original.At, original.SubjectId, "错误的食物名称", original.Detail, original.Effects),
            new(0, original.ObserverId, original.Kind, original.From, original.At, original.SubjectId, original.SubjectName, "invented-detail", original.Effects)
        ];
        foreach (CycleObservedEvent corruption in corruptions) {
            Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
                valid.WithObservedEvents([corruption])));
        }
    }

    [Fact]
    public async Task Fold_requires_a_visible_calm_weather_event_even_when_it_has_no_state_effects() {
        CycleWorld initial = World(Needs(), activity: Wait(Hour(8), 2), environment: new(1, 0, Hour(8)));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        CycleFact valid = await Advance(kernel);
        Assert.Equal(CycleEventKind.WeatherResolved, valid.Kind);
        Assert.False(valid.Environment!.LastCold);
        CycleObservedEvent weather = Assert.Single(valid.ObservedEvents);
        Assert.Equal(CycleObservedEventKind.WeatherResolved, weather.Kind);
        Assert.Equal("Calm", weather.Detail);
        Assert.Empty(weather.Effects);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents([])));
    }

    [Fact]
    public async Task Fold_rejects_invented_final_gather_progress_even_when_finish_removes_it() {
        CycleWorld initial = World(Needs(), place: "stream");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Gather("water")));
        await Advance(kernel);
        CycleWorld beforeYield = kernel.World;
        CycleFact valid = await Advance(kernel);
        CycleStateKey completed = new("activity", "a", "completed");
        CycleVisibleValue invented = CycleVisibleValue.Numeric(5, "份");
        var corrupted = valid.ObservedEvents.Select(entry => {
            var effects = entry.Effects.Select(effect => effect.Key != completed ? effect : entry.Kind switch {
                CycleObservedEventKind.GatherYielded => new CycleObservedEffect(completed, effect.Before, invented),
                CycleObservedEventKind.GatherFinished => new CycleObservedEffect(completed, invented, effect.After),
                _ => effect
            });
            return new CycleObservedEvent(entry.EventOrdinal, entry.ObserverId, entry.Kind, entry.From, entry.At,
                entry.SubjectId, entry.SubjectName, entry.Detail, effects);
        });
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(beforeYield, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents(corrupted)));
    }

    [Fact]
    public async Task Fold_rejects_direct_event_before_elapsed_process_even_when_their_keys_are_disjoint() {
        CycleWorld initial = World(CycleSurvival.Create(Hour(17), 100, 100, 60), activity: Wait(Hour(17), 4));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        CycleFact valid = await Advance(kernel);
        Assert.Equal(CycleEventKind.LightChanged, valid.Kind);
        CycleObservedEvent process = Assert.Single(valid.ObservedEvents, entry => entry.Kind == CycleObservedEventKind.BodyChanged);
        CycleObservedEvent light = Assert.Single(valid.ObservedEvents, entry => entry.Kind == CycleObservedEventKind.LightChanged);
        Assert.Empty(process.Effects.Select(effect => effect.Key).Intersect(light.Effects.Select(effect => effect.Key)));
        CycleObservedEvent[] reversed = new[] { light, process }.Select((entry, ordinal) =>
            new CycleObservedEvent(ordinal, entry.ObserverId, entry.Kind, entry.From, entry.At,
                entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects)).ToArray();
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents(reversed)));
    }
    private static CycleObservedEffect Effect(CycleObservedEvent observed, string category, string objectId, string field) =>
        Assert.Single(observed.Effects, effect => effect.Key == new CycleStateKey(category, objectId, field));
    private static void AssertNumeric(CycleObservedEffect effect, decimal before, decimal after) {
        Assert.Equal(CycleVisibleValueKind.Number, effect.Before.Kind);
        Assert.Equal(CycleVisibleValueKind.Number, effect.After.Kind);
        Assert.Equal(before, effect.Before.Number);
        Assert.Equal(after, effect.After.Number);
    }
    private static void AssertMaterialsEqual(CycleFact expected, CycleFact actual) {
        Assert.Equal(expected.ToolCallId, actual.ToolCallId);
        Assert.Equal(expected.OperationRecordId, actual.OperationRecordId);
        Assert.Equal(expected.ObservedEvents.Count, actual.ObservedEvents.Count);
        for (int index = 0; index < expected.ObservedEvents.Count; index++) {
            CycleObservedEvent left = expected.ObservedEvents[index], right = actual.ObservedEvents[index];
            Assert.Equal((left.EventOrdinal, left.ObserverId, left.Kind, left.From, left.At, left.SubjectId, left.SubjectName, left.Detail),
                (right.EventOrdinal, right.ObserverId, right.Kind, right.From, right.At, right.SubjectId, right.SubjectName, right.Detail));
            Assert.Equal(left.Effects.Select(effect => (effect.Key, effect.Before, effect.After)),
                right.Effects.Select(effect => (effect.Key, effect.Before, effect.After)));
        }
    }
    private static CycleSurvival Needs(int water = 100, int warmth = 80) => CycleSurvival.Create(Hour(8), 100, water, warmth);
    private static CycleSurvival PendingWater() => new(Hour(8), 100 * CycleSurvival.Scale, 25 * CycleSurvival.Scale,
        80 * CycleSurvival.Scale, CycleSurvivalRules.FoodRate, CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(false, true), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
    private static CycleActivity Wait(ModelTime at, int hours) => new(CycleActivityKind.Wait, 1, at, hours * CycleTime.Hour, new(at.Ticks + hours * CycleTime.Hour));
    private static CycleWorld World(CycleSurvival needs, CycleActivity? activity = null, string place = "camp",
        Dictionary<string, int>? items = null, CycleEnvironment? environment = null) {
        ModelTime time = needs.BaselineAt;
        var initial = CycleGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false);
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            [new("a", place, time, initial.World.Resources.Where(r => CycleExpeditionMap.GetResourcePlace(r.Id) == place))], environment: environment);
        var actor = new CycleCharacter("a", "Observed event fixture", backpack: new ItemInventory(items), activity: activity,
            nextActivityId: activity is null ? 1 : 2, survival: needs);
        return CycleEngine.Rebuild(initial.World.Business.Replace(time, changedCharacters: [actor], expedition: expedition), time);
    }
    private static async Task<CycleFact> Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        await StepWorldEvent(kernel);
        kernel.World.Validate();
        return Assert.Single(kernel.LastCompletion!.Event.Facts);
    }
    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This event must not request controller input.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending recovery must not forecast.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world, OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending recovery must not plan or call a controller.");
    }
}
