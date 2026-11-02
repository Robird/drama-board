using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class CommunicationDeliveryIntegrityTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Delivery_rejects_valid_but_replaced_say_or_give_payload(bool gift, bool closed) {
        PlaygroundWorld before = OpenWorld(2, supplyWater: gift);
        if (closed) {
            before = Apply(before, Plan(before, "b", PlaygroundCommand.Wait(2)));
        }
        PlaygroundFact fact = Plan(before, "a", gift ? PlaygroundCommand.Give("b", "item.water", 1) : PlaygroundCommand.Say("b", "original"));
        Apply(before, fact).Validate();
        PlaygroundFact forged = ReplaceDelivery(fact, "b", entry => entry.Communication is null ? entry : Copy(entry,
            communication: gift ? entry.Communication! with { ItemId = "item.food" }
                : entry.Communication! with { SpeechText = "forged" }));
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Apply(before, forged));
        Assert.Contains("Delivered experience", failure.Message);
    }

    internal static PlaygroundWorld OpenWorld(int count = 3, bool supplyWater = false) {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: false,
            enableWeather: false, characterCount: count).World;
        if (supplyWater) {
            PlaygroundCharacter sender = world.GetCharacter("a");
            world = PlaygroundEngine.Rebuild(world.Business.Replace(world.Time,
                changedCharacters: [sender.Change(sender.Activity, backpack: sender.Backpack.Add("item.water", 1))]), world.Time);
        }
        foreach (string actor in world.Characters.Select(actor => actor.Id).ToArray()) {
            world = Apply(world, PlaygroundInteractionRules.OpenContext(world, actor));
        }
        return world;
    }

    internal static PlaygroundFact Plan(PlaygroundWorld world, string actor, PlaygroundCommand command) {
        Assert.Null(PlaygroundActions.Check(world, actor, command));
        string operationId = $"material/{actor}/{world.Business.InteractionStates[actor].CurrentContext!.LastOperationSequence + 1}";
        var operation = new PlaygroundControllerOperation(operationId, PlaygroundCommandParser.Format(command), operationId);
        PlaygroundFact fact = PlaygroundPlanning.Decision(world, actor, command).WithOperation(operation);
        return PlaygroundInteractionRules.Enrich(world, PlaygroundPlanning.Observe(world, PlaygroundSurvivalRules.Enrich(world, fact)), operation);
    }

    internal static PlaygroundWorld Apply(PlaygroundWorld world, PlaygroundFact fact) {
        var instant = new DramaBoard.Kernel.Time.LogicalInstant(world.Time, 0);
        PlaygroundWorld result = PlaygroundEngine.Complete(world, instant, [fact], PlaygroundEngine.Fold(world, instant, fact));
        result.Validate();
        return result;
    }

    internal static PlaygroundObservedEvent Copy(PlaygroundObservedEvent entry, string? detail = null,
        PlaygroundObservedCommunication? communication = null) => new(entry.EventOrdinal, entry.ObserverId, entry.Kind,
        entry.From, entry.At, entry.SubjectId, entry.SubjectName, detail ?? entry.Detail, entry.Effects,
        communication ?? entry.Communication);

    internal static PlaygroundFact ReplaceDelivery(PlaygroundFact fact, string actor, Func<PlaygroundObservedEvent, PlaygroundObservedEvent> replace) {
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
