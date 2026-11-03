using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class ObservedEventTests {
    [Fact]
    public async Task Clamped_supply_records_actual_gain_and_consumption_without_a_time_process() {
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        PlaygroundFact fact = await Advance(kernel);
        PlaygroundObservedEvent used = Assert.Single(fact.ObservedEvents);
        Assert.Equal(PlaygroundObservedEventKind.ItemUsed, used.Kind);
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
    [InlineData(PlaygroundCommandKind.Wait, PlaygroundObservedEventKind.WaitStarted)]
    [InlineData(PlaygroundCommandKind.Gather, PlaygroundObservedEventKind.GatherStarted)]
    [InlineData(PlaygroundCommandKind.Go, PlaygroundObservedEventKind.MoveStarted)]
    [InlineData(PlaygroundCommandKind.Cancel, PlaygroundObservedEventKind.ActivityCancelled)]
    [InlineData(PlaygroundCommandKind.Continue, PlaygroundObservedEventKind.AttentionAcknowledged)]
    public async Task Commands_that_acknowledge_warnings_own_the_confirmation_exactly_once(PlaygroundCommandKind commandKind, PlaygroundObservedEventKind expectedKind) {
        PlaygroundCommand command = commandKind switch {
            PlaygroundCommandKind.Wait => PlaygroundCommand.Wait(1),
            PlaygroundCommandKind.Gather => PlaygroundCommand.Gather("water"),
            PlaygroundCommandKind.Go => PlaygroundCommand.Go("e-n4"),
            PlaygroundCommandKind.Cancel => PlaygroundCommand.Cancel(),
            _ => PlaygroundCommand.Continue()
        };
        PlaygroundActivity? activity = commandKind == PlaygroundCommandKind.Cancel ? Wait(Hour(8), 2) : null;
        PlaygroundWorld initial = World(PendingWater(), activity: activity, place: "stream");
        Assert.Null(PlaygroundActions.Check(initial, "a", command));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(command));
        PlaygroundFact fact = await Advance(kernel);
        PlaygroundObservedEvent action = Assert.Single(fact.ObservedEvents);
        Assert.Equal(expectedKind, action.Kind);
        PlaygroundObservedEffect acknowledgement = Effect(action, "body", "a", "warning-water");
        Assert.Equal(PlaygroundVisibleValue.Flag(true), acknowledgement.Before);
        Assert.Equal(PlaygroundVisibleValue.Flag(false), acknowledgement.After);
        Assert.Equal(0, kernel.World.GetCharacter("a").Survival!.PendingMask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supply_and_handoff_preserve_pending_warning_and_do_not_invent_acknowledgement(bool handoff) {
        PlaygroundWorld initial = World(PendingWater(), items: new() { ["item.water"] = 1 });
        PlaygroundCommand command = handoff ? PlaygroundCommand.Handoff("记住水源") : PlaygroundCommand.Use("item.water");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(command));
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(handoff ? PlaygroundObservedEventKind.HandoffEdited : PlaygroundObservedEventKind.ItemUsed,
            Assert.Single(fact.ObservedEvents).Kind);
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(e => e.Effects), effect => effect.Key.Field.StartsWith("warning-", StringComparison.Ordinal));
        Assert.Equal(2, kernel.World.GetCharacter("a").Survival!.PendingMask);
        Assert.Equal(initial.Time, kernel.World.Time);
    }

    [Fact]
    public async Task Weather_keeps_elapsed_change_and_direct_loss_as_separate_chained_effects() {
        PlaygroundWorld initial = World(Needs(warmth: 60), activity: Wait(Hour(8), 4), environment: new(0, 0, Hour(10)));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, fact.Kind);
        PlaygroundObservedEvent process = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.BodyChanged);
        PlaygroundObservedEvent weather = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.WeatherResolved);
        Assert.True(process.EventOrdinal < weather.EventOrdinal);
        Assert.Equal(Hour(8), process.From);
        Assert.Equal(Hour(10), process.At);
        Assert.Equal(weather.From, weather.At);
        PlaygroundObservedEffect elapsed = Effect(process, "body", "a", "warmth");
        PlaygroundObservedEffect exposure = Effect(weather, "body", "a", "warmth");
        AssertNumeric(elapsed, 60, 72);
        AssertNumeric(exposure, 72, 62);
        Assert.Equal(elapsed.After, exposure.Before);
        Assert.Equal(10 * PlaygroundSurvival.Scale, Assert.Single(fact.Environment!.LastEffects, effect => effect.ActorId == "a").WarmthLossUnits);
        Assert.Null(fact.ToolCallId);
        Assert.Null(fact.OperationRecordId);
    }

    [Fact]
    public async Task Dusk_records_old_rate_integration_before_new_rate_takes_effect() {
        PlaygroundWorld initial = World(PlaygroundSurvival.Create(Hour(17), 100, 100, 60), activity: Wait(Hour(17), 4));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.LightChanged, fact.Kind);
        PlaygroundObservedEvent elapsed = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.BodyChanged);
        PlaygroundObservedEvent light = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.LightChanged);
        AssertNumeric(Effect(elapsed, "body", "a", "warmth"), 60, 66);
        AssertNumeric(Effect(light, "body", "a", "warmth-rate"), 6, -12);
        Assert.DoesNotContain(light.Effects, e => e.Key == new PlaygroundStateKey("body", "a", "warmth"));
        Assert.True(elapsed.EventOrdinal < light.EventOrdinal);
    }

    [Fact]
    public async Task Final_gather_yield_owns_transfer_then_finish_owns_activity_removal() {
        PlaygroundWorld initial = World(Needs(), place: "stream");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Gather("water")));
        await Advance(kernel);
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.GatherYielded, fact.Kind);
        PlaygroundObservedEvent yielded = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.GatherYielded);
        PlaygroundObservedEvent finished = Assert.Single(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.GatherFinished);
        Assert.True(yielded.EventOrdinal < finished.EventOrdinal);
        AssertNumeric(Effect(yielded, "inventory", "item.water", "count"), 0, 1);
        AssertNumeric(Effect(yielded, "resource", "water", "stock"), 12, 11);
        PlaygroundObservedEffect progress = Effect(yielded, "activity", "a", "completed");
        AssertNumeric(progress, 0, 1);
        PlaygroundObservedEffect removedProgress = Effect(finished, "activity", "a", "completed");
        Assert.Equal(progress.After, removedProgress.Before);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, removedProgress.After);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, Effect(finished, "activity", "a", "kind").After);
        Assert.DoesNotContain(finished.Effects, e => e.Key.Category is "inventory" or "resource");
        Assert.Null(kernel.World.GetCharacter("a").Activity);
    }

    [Fact]
    public async Task Distant_regeneration_does_not_reveal_its_event_or_stock() {
        PlaygroundWorld initial = World(Needs(), activity: Wait(Hour(8), 4));
        var depleted = new PlaygroundResource("berries", "野浆果", "item.food", true, PlaygroundTime.Hour, 6, 5, Hour(8), harvestTicks: PlaygroundTime.Hour / 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedResources: [depleted]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        PlaygroundFact fact = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.ResourceRegenerated, fact.Kind);
        Assert.Equal("berries", fact.ResourceId);
        Assert.Equal(6, kernel.World.GetResource("berries").Stock);
        Assert.DoesNotContain(fact.ObservedEvents, e => e.Kind == PlaygroundObservedEventKind.ResourceRegenerated || e.SubjectId == "berries");
        Assert.DoesNotContain(fact.ObservedEvents.SelectMany(e => e.Effects), e => e.Key.Category == "resource");
        Assert.All(fact.ObservedEvents, e => Assert.Equal("a", e.ObserverId));
    }

    [Fact]
    public async Task Pending_E_restores_identical_explanation_and_provenance_without_replanning() {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var planned = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await planned.StepAsync(Hour(240)));
        PlaygroundWorld opened = planned.World;
        KernelCursor openedCursor = planned.Cursor;
        PlaygroundFact expected = await Advance(planned);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, opened, openedCursor)) {
            history.CommitEvent(planned.LastCompletion!.Event);
        }
        using (var reopened = PlaygroundDurableHistory.Open(store.StorePath)) {
            PlaygroundFact restored = Assert.Single(reopened.PendingEvent!.Facts);
            AssertMaterialsEqual(expected, restored);
            Assert.Empty(reopened.ReadCommittedOperations(["fixture/1"]));
            var recovery = new SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact>(reopened,
                new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
                (before, instant, facts, folded) => PlaygroundEngine.Complete(before, instant, facts, folded, PredictionMode.Incremental), PlaygroundEngine.Validate);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(planned.World.DescribeState(), recovery.World.DescribeState());
            Assert.Equal(planned.Cursor, recovery.Cursor);
            PlaygroundCommittedOperation receipt = Assert.Single(reopened.ReadCommittedOperations(["fixture/1"]));
            Assert.Equal(expected.ToolCallId, receipt.ToolCallId);
            Assert.Equal(PlaygroundOperationResults.Describe(expected), receipt.Result);
            Assert.Contains("+10", receipt.Result);
            Assert.DoesNotContain("+45", receipt.Result);
        }
        using var complete = PlaygroundDurableHistory.Open(store.StorePath);
        Assert.Null(complete.PendingEvent);
        Assert.Equal(PlaygroundSurvival.MaximumUnits, complete.State.GetCharacter("a").Survival!.WaterUnits);
        Assert.Equal(0, complete.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Fact]
    public async Task Completed_S_lookup_uses_recorded_actual_gain_even_after_later_body_changes() {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        string result;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Use("item.water"), PlaygroundCommand.Wait(1)));
            result = PlaygroundOperationResults.Describe(await Advance(kernel));
            await Advance(kernel);
            await Advance(kernel);
            Assert.Equal(92 * PlaygroundSurvival.Scale, kernel.World.GetCharacter("a").Survival!.Project(kernel.World.Time).WaterUnits);
        }
        using var reopened = PlaygroundDurableHistory.Open(store.StorePath);
        PlaygroundCommittedOperation recovered = Assert.Single(reopened.ReadCommittedOperations(["fixture/1"]));
        Assert.Equal("call_fixture_1", recovered.ToolCallId);
        Assert.Equal(result, recovered.Result);
        Assert.Contains("+10", recovered.Result);
        Assert.DoesNotContain("+45", recovered.Result);
        Assert.Equal(0, reopened.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Theory]
    [InlineData(PlaygroundCommandKind.Wait, PlaygroundObservedEventKind.WaitStarted)]
    [InlineData(PlaygroundCommandKind.Gather, PlaygroundObservedEventKind.GatherStarted)]
    [InlineData(PlaygroundCommandKind.Go, PlaygroundObservedEventKind.MoveStarted)]
    [InlineData(PlaygroundCommandKind.Continue, PlaygroundObservedEventKind.AttentionAcknowledged)]
    public async Task Accepted_timed_action_owns_handoff_draft_consumption(PlaygroundCommandKind commandKind, PlaygroundObservedEventKind expectedKind) {
        const string handoff = "下一次先查看水源。";
        PlaygroundCommand command = commandKind switch {
            PlaygroundCommandKind.Wait => PlaygroundCommand.Wait(1),
            PlaygroundCommandKind.Gather => PlaygroundCommand.Gather("water"),
            PlaygroundCommandKind.Go => PlaygroundCommand.Go("e-n4"),
            _ => PlaygroundCommand.Continue()
        };
        PlaygroundWorld initial = World(PendingWater(), activity: commandKind == PlaygroundCommandKind.Continue ? Wait(Hour(8), 2) : null,
            place: "stream");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Handoff(handoff), command));
        PlaygroundFact edited = await Advance(kernel);
        PlaygroundObservedEffect written = Effect(Assert.Single(edited.ObservedEvents), "continuity", "a", "draft");
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, written.Before);
        Assert.Equal(PlaygroundVisibleValue.Literal(handoff), written.After);
        PlaygroundFact accepted = await Advance(kernel);
        PlaygroundObservedEvent action = Assert.Single(accepted.ObservedEvents);
        Assert.Equal(expectedKind, action.Kind);
        PlaygroundObservedEffect consumed = Effect(action, "continuity", "a", "draft");
        Assert.Equal(written.After, consumed.Before);
        Assert.Equal(PlaygroundVisibleValue.KnownAbsent, consumed.After);
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
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        PlaygroundFact valid = await Advance(kernel);
        PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        PlaygroundObservedEvent original = Assert.Single(valid.ObservedEvents);
        PlaygroundObservedEffect water = Effect(original, "body", "a", "water");
        var effects = original.Effects.ToList();
        switch (corruption) {
            case "after":
                effects[effects.IndexOf(water)] = new(water.Key, water.Before, PlaygroundVisibleValue.Numeric(99 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale));
                break;
            case "before":
                effects[effects.IndexOf(water)] = new(water.Key, PlaygroundVisibleValue.Numeric(89 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale), water.After);
                break;
            case "missing-effect": effects.Remove(water); break;
            case "ownership":
                effects.Add(new(new("body", "a", "warmth"), PlaygroundVisibleValue.Numeric(80 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale),
                    PlaygroundVisibleValue.Numeric(79 * PlaygroundSurvival.Scale, "点", PlaygroundSurvival.Scale)));
                break;
            case "duplicate-key": effects.Add(water); break;
        }
        var corrupted = new PlaygroundObservedEvent(corruption == "ordinal" ? 1 : original.EventOrdinal,
            corruption == "observer" ? "another-character" : original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, effects);
        PlaygroundFact invalid = valid.WithObservedEvents(corruption == "missing-event" ? [] : [corrupted]);
        string before = initial.DescribeState();
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, invalid));
        Assert.Equal(before, initial.DescribeState());
    }

    [Fact]
    public async Task Fold_rejects_invented_intermediate_gain_even_when_effect_chain_reaches_the_correct_endpoint() {
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        PlaygroundFact valid = await Advance(kernel);
        PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        PlaygroundObservedEvent original = Assert.Single(valid.ObservedEvents);
        PlaygroundObservedEffect inventory = Effect(original, "inventory", "item.water", "count");
        PlaygroundVisibleValue invented = PlaygroundVisibleValue.Numeric(2, "份");
        var firstEffects = original.Effects.Where(e => e.Key != inventory.Key).Append(new(inventory.Key, inventory.Before, invented));
        var first = new PlaygroundObservedEvent(0, original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, firstEffects);
        var second = new PlaygroundObservedEvent(1, original.ObserverId, original.Kind, original.From, original.At,
            original.SubjectId, original.SubjectName, original.Detail, [new(inventory.Key, invented, inventory.After)]);
        PlaygroundFact invalid = valid.WithObservedEvents([first, second]);
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, invalid));
    }

    [Fact]
    public async Task Fold_rejects_event_parameters_that_disagree_with_recorded_supply() {
        PlaygroundWorld initial = World(Needs(water: 90), items: new() { ["item.water"] = 1 });
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Use("item.water")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));
        initial = kernel.World;
        PlaygroundFact valid = await Advance(kernel);
        PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant, valid); // Validate the uncorrupted fixture first.
        PlaygroundObservedEvent original = Assert.Single(valid.ObservedEvents);
        PlaygroundObservedEvent[] corruptions = [
            new(0, original.ObserverId, original.Kind, original.From, original.At, "item.food", original.SubjectName, original.Detail, original.Effects),
            new(0, original.ObserverId, original.Kind, original.From, original.At, original.SubjectId, "错误的食物名称", original.Detail, original.Effects),
            new(0, original.ObserverId, original.Kind, original.From, original.At, original.SubjectId, original.SubjectName, "invented-detail", original.Effects)
        ];
        foreach (PlaygroundObservedEvent corruption in corruptions) {
            Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
                valid.WithObservedEvents([corruption])));
        }
    }

    [Fact]
    public async Task Fold_requires_a_visible_calm_weather_event_even_when_it_has_no_state_effects() {
        PlaygroundWorld initial = World(Needs(), activity: Wait(Hour(8), 2), environment: new(1, 0, Hour(8)));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        PlaygroundFact valid = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.WeatherResolved, valid.Kind);
        Assert.False(valid.Environment!.LastCold);
        PlaygroundObservedEvent weather = Assert.Single(valid.ObservedEvents);
        Assert.Equal(PlaygroundObservedEventKind.WeatherResolved, weather.Kind);
        Assert.Equal("Calm", weather.Detail);
        Assert.Empty(weather.Effects);
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents([])));
    }

    [Fact]
    public async Task Fold_rejects_invented_final_gather_progress_even_when_finish_removes_it() {
        PlaygroundWorld initial = World(Needs(), place: "stream");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Gather("water")));
        await Advance(kernel);
        PlaygroundWorld beforeYield = kernel.World;
        PlaygroundFact valid = await Advance(kernel);
        PlaygroundStateKey completed = new("activity", "a", "completed");
        PlaygroundVisibleValue invented = PlaygroundVisibleValue.Numeric(5, "份");
        var corrupted = valid.ObservedEvents.Select(entry => {
            var effects = entry.Effects.Select(effect => effect.Key != completed ? effect : entry.Kind switch {
                PlaygroundObservedEventKind.GatherYielded => new PlaygroundObservedEffect(completed, effect.Before, invented),
                PlaygroundObservedEventKind.GatherFinished => new PlaygroundObservedEffect(completed, invented, effect.After),
                _ => effect
            });
            return new PlaygroundObservedEvent(entry.EventOrdinal, entry.ObserverId, entry.Kind, entry.From, entry.At,
                entry.SubjectId, entry.SubjectName, entry.Detail, effects);
        });
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(beforeYield, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents(corrupted)));
    }

    [Fact]
    public async Task Fold_rejects_direct_event_before_elapsed_process_even_when_their_keys_are_disjoint() {
        PlaygroundWorld initial = World(PlaygroundSurvival.Create(Hour(17), 100, 100, 60), activity: Wait(Hour(17), 4));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new ForbiddenController());
        PlaygroundFact valid = await Advance(kernel);
        Assert.Equal(PlaygroundEventKind.LightChanged, valid.Kind);
        PlaygroundObservedEvent process = Assert.Single(valid.ObservedEvents, entry => entry.Kind == PlaygroundObservedEventKind.BodyChanged);
        PlaygroundObservedEvent light = Assert.Single(valid.ObservedEvents, entry => entry.Kind == PlaygroundObservedEventKind.LightChanged);
        Assert.Empty(process.Effects.Select(effect => effect.Key).Intersect(light.Effects.Select(effect => effect.Key)));
        PlaygroundObservedEvent[] reversed = new[] { light, process }.Select((entry, ordinal) =>
            new PlaygroundObservedEvent(ordinal, entry.ObserverId, entry.Kind, entry.From, entry.At,
                entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects)).ToArray();
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(initial, kernel.LastCompletion!.Event.TargetInstant,
            valid.WithObservedEvents(reversed)));
    }
    private static PlaygroundObservedEffect Effect(PlaygroundObservedEvent observed, string category, string objectId, string field) =>
        Assert.Single(observed.Effects, effect => effect.Key == new PlaygroundStateKey(category, objectId, field));
    private static void AssertNumeric(PlaygroundObservedEffect effect, decimal before, decimal after) {
        Assert.Equal(PlaygroundVisibleValueKind.Number, effect.Before.Kind);
        Assert.Equal(PlaygroundVisibleValueKind.Number, effect.After.Kind);
        Assert.Equal(before, effect.Before.Number);
        Assert.Equal(after, effect.After.Number);
    }
    private static void AssertMaterialsEqual(PlaygroundFact expected, PlaygroundFact actual) {
        Assert.Equal(expected.ToolCallId, actual.ToolCallId);
        Assert.Equal(expected.OperationRecordId, actual.OperationRecordId);
        Assert.Equal(expected.ObservedEvents.Count, actual.ObservedEvents.Count);
        for (int index = 0; index < expected.ObservedEvents.Count; index++) {
            PlaygroundObservedEvent left = expected.ObservedEvents[index], right = actual.ObservedEvents[index];
            Assert.Equal((left.EventOrdinal, left.ObserverId, left.Kind, left.From, left.At, left.SubjectId, left.SubjectName, left.Detail),
                (right.EventOrdinal, right.ObserverId, right.Kind, right.From, right.At, right.SubjectId, right.SubjectName, right.Detail));
            Assert.Equal(left.Effects.Select(effect => (effect.Key, effect.Before, effect.After)),
                right.Effects.Select(effect => (effect.Key, effect.Before, effect.After)));
        }
    }
    private static PlaygroundSurvival Needs(int water = 100, int warmth = 80) => PlaygroundSurvival.Create(Hour(8), 100, water, warmth);
    private static PlaygroundSurvival PendingWater() => new(Hour(8), 100 * PlaygroundSurvival.Scale, 25 * PlaygroundSurvival.Scale,
        80 * PlaygroundSurvival.Scale, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 2, pendingMask: 2, pendingSince: Hour(8));
    private static PlaygroundActivity Wait(ModelTime at, int hours) => new(PlaygroundActivityKind.Wait, 1, at, hours * PlaygroundTime.Hour, new(at.Ticks + hours * PlaygroundTime.Hour));
    private static PlaygroundWorld World(PlaygroundSurvival needs, PlaygroundActivity? activity = null, string place = "camp",
        Dictionary<string, int>? items = null, PlaygroundEnvironment? environment = null) {
        ModelTime time = needs.BaselineAt;
        var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false);
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new(place))]);
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            [new("a", place, time, initial.World.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == place))], environment: environment);
        var actor = new PlaygroundCharacter("a", "Observed event fixture", backpack: new ItemInventory(items), activity: activity,
            nextActivityId: activity is null ? 1 : 2, survival: needs);
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.FromLegacy(time, initial.World.Resources, [actor], expedition);
        var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script,
            [actor], expedition: expedition, definition: definition);
        return PlaygroundEngine.Rebuild(business, time);
    }
    private static async Task<PlaygroundFact> Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        await StepWorldEvent(kernel);
        kernel.World.Validate();
        return Assert.Single(kernel.LastCompletion!.Event.Facts);
    }
    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This event must not request controller input.");
    }
    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending recovery must not forecast.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world, OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pending recovery must not plan or call a controller.");
    }
}
