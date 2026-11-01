using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CommunicationDeliveryIntegrityTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Delivery_rejects_valid_but_replaced_say_or_give_payload(bool gift, bool closed) {
        CycleWorld before = OpenWorld(2, supplyWater: gift);
        if (closed) {
            before = Apply(before, Plan(before, "b", CycleCommand.Wait(2)));
        }
        CycleFact fact = Plan(before, "a", gift ? CycleCommand.Give("b", "item.water", 1) : CycleCommand.Say("b", "original"));
        Apply(before, fact).Validate();
        CycleFact forged = ReplaceDelivery(fact, "b", entry => entry.Communication is null ? entry : Copy(entry,
            communication: gift ? entry.Communication! with { ItemId = "item.food" }
                : entry.Communication! with { SpeechText = "forged" }));
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Apply(before, forged));
        Assert.Contains("Delivered experience", failure.Message);
    }

    internal static CycleWorld OpenWorld(int count = 3, bool supplyWater = false) {
        CycleWorld world = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: count).World;
        if (supplyWater) {
            CycleCharacter sender = world.GetCharacter("a");
            world = CycleEngine.Rebuild(world.Business.Replace(world.Time,
                changedCharacters: [sender.Change(sender.Activity, backpack: sender.Backpack.Add("item.water", 1))]), world.Time);
        }
        foreach (string actor in world.Characters.Select(actor => actor.Id).ToArray()) {
            world = Apply(world, CycleInteractionRules.OpenContext(world, actor));
        }
        return world;
    }

    internal static CycleFact Plan(CycleWorld world, string actor, CycleCommand command) {
        Assert.Null(CycleActions.Check(world, actor, command));
        string operationId = $"material/{actor}/{world.Business.InteractionStates[actor].CurrentContext!.LastOperationSequence + 1}";
        var operation = new CycleControllerOperation(operationId, CycleCommandParser.Format(command), operationId);
        CycleFact fact = CyclePlanning.Decision(world, actor, command).WithOperation(operation);
        return CycleInteractionRules.Enrich(world, CyclePlanning.Observe(world, CycleSurvivalRules.Enrich(world, fact)), operation);
    }

    internal static CycleWorld Apply(CycleWorld world, CycleFact fact) {
        var instant = new DramaBoard.Kernel.Time.LogicalInstant(world.Time, 0);
        CycleWorld result = CycleEngine.Complete(world, instant, [fact], CycleEngine.Fold(world, instant, fact));
        result.Validate();
        return result;
    }

    internal static CycleObservedEvent Copy(CycleObservedEvent entry, string? detail = null,
        CycleObservedCommunication? communication = null) => new(entry.EventOrdinal, entry.ObserverId, entry.Kind,
        entry.From, entry.At, entry.SubjectId, entry.SubjectName, detail ?? entry.Detail, entry.Effects,
        communication ?? entry.Communication);

    internal static CycleFact ReplaceDelivery(CycleFact fact, string actor, Func<CycleObservedEvent, CycleObservedEvent> replace) {
        CharacterInteractionState state = fact.InteractionChanges.Single(state => state.ActorId == actor);
        CharacterExperience Replace(CharacterExperience experience) => new(experience.ExperienceSequence, replace(experience.Event));
        CharacterContext? context = state.CurrentContext;
        IEnumerable<CharacterExperience> pending = state.UndeliveredExperiences;
        if (context?.Status == CharacterContextStatus.Open) {
            CharacterContextItem item = context.Items[^1];
            var replacement = new CharacterContextItem(item.ItemSequence, item.OperationSequence, item.RawInput, item.ToolCallId,
                item.OperationRecordId, item.Result, item.Experiences.Select(Replace), item.Observation, item.IsRejected, item.GoalOperation);
            context = new(context.ContextSequence, context.Status, context.Opening,
                context.Items.Take(context.Items.Count - 1).Append(replacement), context.NextItemSequence, context.LastOperationSequence);
        } else {
            pending = pending.Select(Replace);
        }
        var changed = new CharacterInteractionState(actor, context, state.PendingAdvance, pending,
            state.DraftHandoff, state.NextExperienceSequence, state.Goals);
        return fact.WithInteractions(fact.InteractionChanges.Select(current => current.ActorId == actor ? changed : current));
    }
}
