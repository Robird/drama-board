using System.Collections;
using System.Reflection;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CharacterContextLifecycleTests {
    [Fact]
    public async Task Instant_feedback_belongs_to_open_context_but_release_and_arrival_are_delivered_in_next_opening() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var controller = Commands(CycleCommand.Use("item.water"), CycleCommand.Handoff("抵达后观察"), CycleCommand.Go("trail-1"));
        var kernel = CycleEngine.CreateKernel(Memory(initial.World), characterController: controller);
        Assert.Null(initial.World.Business.InteractionStates["a"].CurrentContext);

        await Advance(kernel);
        Assert.Equal(0, controller.Calls);
        var opening = Context(kernel.World);
        Assert.Equal(CharacterContextStatus.Open, opening.Status);
        Assert.Equal(1, opening.ContextSequence);
        Assert.Empty(opening.Items);
        Assert.Null(opening.Opening.PreviousAdvance);
        Assert.NotEmpty(opening.Opening.Background.Rules);
        Assert.Equal("camp", opening.Opening.Observation.PlaceId);
        Assert.Null(kernel.World.Business.LastServedActorId);

        CycleWorld beforeSupply = kernel.World;
        await Advance(kernel);
        var supplied = Context(kernel.World);
        var supply = Assert.Single(supplied.Items);
        Assert.Equal(1, supply.OperationSequence);
        Assert.Equal("call_fixture_1", supply.ToolCallId);
        Assert.Equal("fixture/1", supply.OperationRecordId);
        Assert.Contains(supply.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.ItemUsed);
        Assert.NotNull(supply.Result);
        Assert.Equal(45, opening.Opening.Observation.Survival!.Water);
        Assert.Equal(90, supplied.LatestObservation.Survival!.Water);
        var staleBody = CopyObservation(supplied.LatestObservation, survival: opening.LatestObservation.Survival);
        Assert.Equal(0, staleBody.Actor.Backpack.Count("item.water"));
        Assert.Equal(45, staleBody.Survival!.Water);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(beforeSupply, kernel.LastCompletion!.Event.TargetInstant,
            WithLastObservation(Last(kernel), "a", staleBody)));

        await Advance(kernel);
        var beforeRelease = Context(kernel.World);
        Assert.Equal(2, beforeRelease.Items.Count);
        Assert.Equal("抵达后观察", kernel.World.Business.InteractionStates["a"].DraftHandoff);
        CycleWorld beforeMove = kernel.World;
        await Advance(kernel);
        var closedState = kernel.World.Business.InteractionStates["a"];
        var closed = closedState.CurrentContext!;
        Assert.Equal(CharacterContextStatus.Closed, closed.Status);
        Assert.Equal(beforeRelease.Items, closed.Items);
        Assert.DoesNotContain(closed.Items, item => item.ToolCallId == "call_fixture_3");
        Assert.Equal("call_fixture_3", closedState.PendingAdvance!.ToolCallId);
        Assert.Equal("fixture/3", closedState.PendingAdvance.OperationRecordId);
        Assert.Equal("抵达后观察", closedState.PendingAdvance.Handoff);
        Assert.Equal(opening.Opening.Observation.PlaceName, closedState.PendingAdvance.OriginPlaceName);
        Assert.Contains(opening.Opening.Observation.Passages.Single(passage => passage.Id == "trail-1").Description,
            closedState.PendingAdvance.Description);
        Assert.DoesNotContain("溪流湾", closedState.PendingAdvance.Description);
        Assert.Equal(1, closedState.PendingAdvance.SourceContextSequence);
        Assert.Contains(closedState.UndeliveredExperiences, experience => experience.Event.Kind == CycleObservedEventKind.MoveStarted);
        Assert.Null(closedState.DraftHandoff);
        var falseDescription = CopyAdvance(closedState.PendingAdvance, description: "已知终点：溪流湾");
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(beforeMove, kernel.LastCompletion!.Event.TargetInstant,
            WithAdvance(Last(kernel), falseDescription)));

        for (int steps = 0; Context(kernel.World).ContextSequence == 1; steps++) {
            Assert.True(steps < 12);
            await Advance(kernel);
            // Captured old immutable contexts never acquire the release/settlement results later.
            Assert.Equal(2, closed.Items.Count);
            Assert.Equal("camp", closed.LatestObservation.PlaceId);
        }
        var current = Context(kernel.World);
        Assert.Equal(3, controller.Calls);
        Assert.Equal(2, current.ContextSequence);
        Assert.Equal(CharacterContextStatus.Open, current.Status);
        Assert.Empty(current.Items);
        Assert.Equal(0, current.LastOperationSequence);
        Assert.Equal("stream", current.Opening.Observation.PlaceId);
        Assert.Equal("call_fixture_3", current.Opening.PreviousAdvance!.ToolCallId);
        Assert.Equal("抵达后观察", current.Opening.PreviousAdvance.Handoff);
        Assert.Equal(closedState.PendingAdvance.Description, current.Opening.PreviousAdvance.Description);
        Assert.DoesNotContain(current.Opening.Observation.Passages.Single(passage => passage.Id == "trail-1").Description,
            current.Opening.PreviousAdvance.Description);
        Assert.Contains(current.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.MoveStarted);
        Assert.Contains(current.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.MoveArrived);
        Assert.Contains(current.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.BodyChanged);
        Assert.Contains(current.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.SurvivalWarning);
        Assert.Null(kernel.World.Business.InteractionStates["a"].PendingAdvance);
        Assert.Empty(kernel.World.Business.InteractionStates["a"].UndeliveredExperiences);
        long[] identities = beforeRelease.Items.SelectMany(item => item.Experiences)
            .Concat(current.Opening.Experiences).Select(experience => experience.ExperienceSequence).ToArray();
        Assert.Equal(identities.Length, identities.Distinct().Count());
        Assert.Equal(identities.Order(), identities);
        AssertFrozenVisibleGraph(current);
    }

    [Fact]
    public async Task Other_actor_public_activity_change_appends_snapshot_only_item_without_private_effects() {
        ModelTime at = Hour(8);
        CycleNotice notice = new(CycleNoticeKind.ResourceDepleted, "sunflower", null, "fixture", at, "Known notification.");
        var initial = CycleWorld.CreatePlayable(at, [
            new("a", "Alice", activity: Waiting(at), notices: [notice], nextActivityId: 2),
            new("b", "Bob", backpack: new ItemInventory(new Dictionary<string, int> { ["secret.gem"] = 9 }),
                activity: Waiting(at), notices: [notice], nextActivityId: 2)
        ], [Sun(at)]);
        Assert.Equal(new[] { "a", "b" }, initial.Business.InteractionStates.Keys.Order());
        var controller = Commands(CycleCommand.Cancel(), CycleCommand.Cancel());
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await Advance(kernel); // Open A.
        await Advance(kernel); // A cancels; B has not yet been served.
        Assert.Null(kernel.World.Business.InteractionStates["b"].CurrentContext);
        await Advance(kernel); // Open B without advancing time or asking the controller.
        Assert.Equal(1, controller.Calls);
        var before = Context(kernel.World);
        Assert.True(Assert.Single(before.LatestObservation.OtherCharacters).Waiting);
        CycleWorld previousWorld = kernel.World;
        await Advance(kernel); // B cancels; A is still Open.
        var fact = Last(kernel);
        Assert.DoesNotContain(fact.ObservedEvents, entry => entry.ObserverId == "a");
        Assert.Contains(fact.InteractionChanges, change => change.ActorId == "a");
        var after = Context(kernel.World);
        Assert.Equal(before.ContextSequence, after.ContextSequence);
        Assert.Equal(before.LastOperationSequence, after.LastOperationSequence);
        var update = Assert.Single(after.Items.Skip(before.Items.Count));
        Assert.Empty(update.Experiences);
        Assert.Null(update.ToolCallId);
        Assert.Null(update.OperationRecordId);
        Assert.Null(update.OperationSequence);
        Assert.NotNull(update.Observation);
        Assert.False(Assert.Single(update.Observation.OtherCharacters).Waiting);
        Assert.All(after.Items.SelectMany(item => item.Experiences), experience => Assert.Equal("a", experience.Event.ObserverId));
        Assert.DoesNotContain(after.LatestObservation.Commands, command => command.Command.Kind == CycleCommandKind.Handoff);
        AssertFrozenVisibleGraph(after);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(new[] { "a", "b" }, controller.Requests.Select(request => request.Actor));

        // A different arbitrary field is not a substitute for the actual changed public endpoint.
        var staleOther = CopyObservation(after.LatestObservation, otherCharacters: before.LatestObservation.OtherCharacters,
            commands: after.LatestObservation.Commands.Select((command, index) => index == 0
                ? command with { Warning = "not-the-recorded-public-change" } : command).ToArray());
        Assert.True(Assert.Single(staleOther.OtherCharacters).Waiting);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(previousWorld, kernel.LastCompletion!.Event.TargetInstant,
            WithLastObservation(fact, "a", staleOther)));

        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(previousWorld, kernel.LastCompletion!.Event.TargetInstant,
            fact.WithInteractions(fact.InteractionChanges.Where(change => change.ActorId != "a"))));
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(previousWorld, kernel.LastCompletion!.Event.TargetInstant,
            fact.WithInteractions(fact.InteractionChanges.Concat(fact.InteractionChanges.Take(1)))));
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(previousWorld, kernel.LastCompletion!.Event.TargetInstant,
            fact.WithInteractions(fact.InteractionChanges.Append(new CharacterInteractionState("unknown")))));
        var b = fact.InteractionChanges.Single(change => change.ActorId == "b");
        var wrongOwner = new CharacterInteractionState("a", b.CurrentContext, b.PendingAdvance,
            b.UndeliveredExperiences, b.DraftHandoff, b.NextExperienceSequence);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(previousWorld, kernel.LastCompletion!.Event.TargetInstant,
            fact.WithInteractions(fact.InteractionChanges.Where(change => change.ActorId != "a").Append(wrongOwner))));
    }

    [Fact]
    public async Task Unserved_basic_characters_collect_public_experiences_then_receive_their_own_first_openings() {
        ModelTime at = Hour(17);
        var initial = CycleWorld.CreatePlayable(at, [
            new("a", "Alice", activity: new(CycleActivityKind.Wait, 1, at, 3 * CycleTime.Hour, Hour(20)), nextActivityId: 2),
            new("b", "Bob", activity: new(CycleActivityKind.Wait, 1, at, 3 * CycleTime.Hour, Hour(20)), nextActivityId: 2)
        ], [Sun(at, stock: 5)]);
        var controller = Commands(CycleCommand.Wait(1));
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: controller);
        await Advance(kernel);
        Assert.Equal(CycleEventKind.LightChanged, Last(kernel).Kind);
        Assert.Empty(Last(kernel).ChangedCharacters);
        Assert.Equal(2, Last(kernel).InteractionChanges.Count);
        foreach (var (actorId, interaction) in kernel.World.Business.InteractionStates) {
            Assert.Null(interaction.CurrentContext);
            var experience = Assert.Single(interaction.UndeliveredExperiences);
            Assert.Equal(actorId, experience.Event.ObserverId);
            Assert.Equal(CycleObservedEventKind.LightChanged, experience.Event.Kind);
            Assert.Equal(1, experience.ExperienceSequence);
        }
        for (int steps = 0; kernel.World.Business.InteractionStates["b"].CurrentContext is null; steps++) {
            Assert.True(steps < 12);
            await Advance(kernel);
        }
        foreach (var (actorId, interaction) in kernel.World.Business.InteractionStates) {
            Assert.Equal(1, interaction.CurrentContext!.ContextSequence);
            Assert.Contains(interaction.CurrentContext.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.LightChanged);
            Assert.All(interaction.CurrentContext.Opening.Experiences, experience => Assert.Equal(actorId, experience.Event.ObserverId));
            Assert.False(interaction.CurrentContext.Opening.Observation.IsDay);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_gather_command_must_match_accepted_quantity_and_all_semantics(bool selectedAll) {
        CycleWorld initial = Solo(Hour(8), [Sun(Hour(8), stock: 3)]);
        var command = selectedAll ? CycleCommand.GatherAll("sunflower") : CycleCommand.Gather("sunflower");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(command));
        await Advance(kernel);
        CycleWorld before = kernel.World;
        await Advance(kernel);
        PendingAdvance pending = kernel.World.Business.InteractionStates["a"].PendingAdvance!;
        // For all, keep the same quantity but falsify how it was selected. For count, falsify only the quantity.
        var falseCommand = selectedAll ? CycleCommand.Gather("sunflower", 3) : CycleCommand.Gather("sunflower", 2);
        Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before, kernel.LastCompletion!.Event.TargetInstant,
            WithAdvance(Last(kernel), CopyAdvance(pending, command: falseCommand))));
    }

    [Fact]
    public async Task Natural_terminal_from_open_context_consumes_draft_without_inventing_a_handoff_effect() {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableWeather: false);
        var preparation = CycleEngine.CreateKernel(Memory(initial.World), characterController: Commands(CycleCommand.Handoff("尚未放行的计划")));
        await Advance(preparation);
        await Advance(preparation);
        var prior = preparation.World.Business.InteractionStates["a"];
        Assert.Equal(CharacterContextStatus.Open, prior.CurrentContext!.Status);
        Assert.Equal("尚未放行的计划", prior.DraftHandoff);
        Assert.Null(prior.PendingAdvance);

        // Put a physical fatal boundary under an already committed Open context. This is a fixture
        // for natural-event routing, not a new player operation or a change to normal time arbitration.
        var original = preparation.World.GetCharacter("a");
        var body = original.Survival!;
        var exhausted = new CycleSurvival(preparation.World.Time, 0, body.WaterUnits, body.WarmthUnits,
            body.FoodRate, body.WaterRate, body.WarmthRate);
        var business = preparation.World.Business.Replace(preparation.World.Time,
            changedCharacters: [original.WithSurvival(exhausted)]);
        CycleWorld boundary = CycleEngine.Rebuild(business, business.Time);
        var forbiddenInput = Commands();
        var kernel = CycleEngine.CreateKernel(Memory(boundary), characterController: forbiddenInput);
        await Advance(kernel);

        Assert.Equal(CycleEventKind.SurvivalFailed, Last(kernel).Kind);
        Assert.Equal(0, forbiddenInput.Calls);
        var state = kernel.World.Business.InteractionStates["a"];
        var terminal = state.CurrentContext!;
        Assert.Equal(CharacterContextStatus.Terminal, terminal.Status);
        Assert.Equal(prior.CurrentContext.ContextSequence + 1, terminal.ContextSequence);
        Assert.Null(state.DraftHandoff);
        Assert.Null(state.PendingAdvance);
        Assert.Null(terminal.Opening.PreviousAdvance);
        Assert.Contains(terminal.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.SurvivalFailed);
        Assert.Contains(terminal.Opening.Experiences, experience => experience.Event.Kind == CycleObservedEventKind.ExpeditionEnded);
        Assert.DoesNotContain(terminal.Opening.Experiences.SelectMany(experience => experience.Event.Effects),
            effect => effect.Key == new CycleStateKey("continuity", "a", "draft"));
        Assert.Empty(terminal.Items);
        Assert.Equal(0, terminal.LatestObservation.Survival!.Food);
        Assert.Equal("尚未放行的计划", prior.DraftHandoff);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(240)));
    }

    private static CycleActivity Waiting(ModelTime at) => new(CycleActivityKind.Wait, 1, at, 2 * CycleTime.Hour, Hour(10));
    private static PendingAdvance CopyAdvance(PendingAdvance source, CycleCommand? command = null, string? description = null) =>
        new(source.ActorId, source.SourceContextSequence, source.OperationSequence, command ?? source.Command, source.RawInput,
            source.ToolCallId, source.OperationRecordId, source.AcceptedAt, description ?? source.Description,
            source.OriginPlaceName, source.ActivityId, source.MovementGeneration, source.Handoff);

    private static CycleFact WithAdvance(CycleFact fact, PendingAdvance advance) {
        var state = fact.InteractionChanges.Single(change => change.ActorId == advance.ActorId);
        var replacement = new CharacterInteractionState(state.ActorId, state.CurrentContext, advance,
            state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence);
        return fact.WithInteractions(fact.InteractionChanges.Select(change => change.ActorId == advance.ActorId ? replacement : change));
    }
    private static CycleFact WithLastObservation(CycleFact fact, string actorId, CycleObservation observation) {
        var state = fact.InteractionChanges.Single(change => change.ActorId == actorId);
        var context = state.CurrentContext!;
        var item = context.Items[^1];
        var replacement = new CharacterContextItem(item.ItemSequence, item.OperationSequence, item.RawInput,
            item.ToolCallId, item.OperationRecordId, item.Result, item.Experiences, observation, item.IsRejected);
        var changedContext = new CharacterContext(context.ContextSequence, context.Status, context.Opening,
            context.Items.Take(context.Items.Count - 1).Append(replacement), context.NextItemSequence, context.LastOperationSequence);
        var changedState = new CharacterInteractionState(actorId, changedContext, state.PendingAdvance,
            state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence);
        return fact.WithInteractions(fact.InteractionChanges.Select(change => change.ActorId == actorId ? changedState : change));
    }

    private static CycleObservation CopyObservation(CycleObservation source, CycleSurvivalObservation? survival = null,
        IReadOnlyList<CycleOtherCharacterObservation>? otherCharacters = null, IReadOnlyList<CycleCommandCheck>? commands = null) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, ItemNames = source.ItemNames,
        OtherCharacters = otherCharacters ?? source.OtherCharacters, Notices = source.Notices, Commands = commands ?? source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Journey = source.Journey,
        Passages = source.Passages, Memories = source.Memories, Outcome = source.Outcome,
        EndingReason = source.EndingReason, Survival = survival ?? source.Survival, Weather = source.Weather
    };

    private static CharacterContext Context(CycleWorld world) => world.Business.InteractionStates["a"].CurrentContext!;
    private static CycleFact Last(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) =>
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(240)));

    internal static void AssertFrozenVisibleGraph(object root) => Visit(root, new(ReferenceEqualityComparer.Instance));
    private static void Visit(object? value, HashSet<object> seen) {
        if (value is null) { return; }
        Assert.False(value is Delegate or CycleWorld or CycleBusinessState or CycleCharacter or CycleDecisionContext or
            CycleExpedition or GraphSpatialState or CycleFact, $"Forbidden context reference: {value.GetType().Name}");
        Assert.NotEqual("secret.gem", value as string);
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or CycleVisibleValue || !seen.Add(value)) { return; }
        if (value is IDictionary dictionary) { Assert.True(dictionary.IsReadOnly, $"Mutable context dictionary: {type.Name}"); }
        else if (value is IList list) { Assert.True(list.IsReadOnly, $"Mutable context list: {type.Name}"); }
        if (value is IEnumerable items) {
            foreach (object? item in items) { Visit(item, seen); }
            return;
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) { Visit(property.GetValue(value), seen); }
        }
    }
}
