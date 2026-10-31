using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

/// <summary>Composes private goal edits with the existing recorded interaction boundary.</summary>
internal static class CycleGoalInteractionRules {
    internal static CycleFact Plan(CycleWorld world, string actorId, CycleGoalCommand command,
        CycleControllerOperation operation) {
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        CharacterContext current = state.CurrentContext is { Status: CharacterContextStatus.Open } open ? open
            : throw new InvalidOperationException("A goal operation requires an open decision context.");
        long sequence = checked(current.LastOperationSequence + 1);
        CycleGoalOperationResult result = CycleGoalRules.Apply(state.Goals, command, current.ContextSequence, sequence);
        if (!result.Success) { return CycleInteractionRules.Reject(world, actorId, operation, result.Error!); }

        var fact = new CycleFact(CycleEventKind.GoalOperation, world.Time, actorId,
            changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()],
            lastServedActorId: actorId, result: result.Receipt.Message, goalOperation: result.Receipt)
            .WithOperation(operation);
        // Only planning sees this intermediate wrapper. The published replacement below contains
        // the new goals and their matching frozen observation together.
        var provisional = new CharacterInteractionState(actorId, current, state.PendingAdvance,
            state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence, result.After);
        CycleBusinessState business = CycleFactApplication.ApplyReplacements(world.Business, new LogicalInstant(world.Time, 0),
            fact.WithInteractions([provisional]));
        var candidate = new CycleWorld(business, world.Forecasts, world.Spatial);
        CycleObservation observation = CycleDecisionContext.Observe(candidate, actorId);
        var item = new CharacterContextItem(current.NextItemSequence, sequence, operation.RawInput,
            operation.ToolCallId, operation.OperationRecordId, result.Receipt.Message,
            observation: observation, goalOperation: result.Receipt);
        var context = new CharacterContext(current.ContextSequence, CharacterContextStatus.Open, current.Opening,
            CycleInteractionRules.RetainItems(current.Items, sequence).Append(item), checked(current.NextItemSequence + 1), sequence);
        return fact.WithInteractions([new(actorId, context, state.PendingAdvance, state.UndeliveredExperiences,
            state.DraftHandoff, state.NextExperienceSequence, result.After)]);
    }

    /// <summary>Checks recorded effects against pure rules; the comparison result is never installed.</summary>
    internal static void Validate(CycleBusinessState before, CycleBusinessState after, CycleFact fact) {
        foreach ((string actorId, CharacterInteractionState state) in before.InteractionStates) {
            if (fact.Kind != CycleEventKind.GoalOperation || fact.ActorId != actorId) {
                Require(state.Goals.SameAs(after.InteractionStates[actorId].Goals),
                    "Only an accepted goal operation can replace its actor's goals.");
            }
        }
        bool knownGoal = fact.RawInput is not null &&
            CycleGoalCommandParser.TryGetKind(fact.RawInput, out _);
        if (fact.Kind == CycleEventKind.GoalOperation) {
            Require(before.Expedition is not null && fact.ActorId is not null && fact.GoalOperation is { Success: true } &&
                fact.ObservedEvents.Count == 0 && fact.InteractionChanges.Count == 1 && fact.InteractionChanges[0].ActorId == fact.ActorId,
                "A goal operation changes only its own private interaction material.");
            CharacterInteractionState state = before.InteractionStates[fact.ActorId!];
            CharacterContext current = state.CurrentContext is { Status: CharacterContextStatus.Open } open ? open
                : throw new InvalidOperationException("A goal operation requires an open context.");
            Require(CycleGoalCommandParser.TryParse(fact.RawInput!, out CycleGoalCommand? command, out _) && command is not null,
                "A goal fact must retain a valid original goal command.");
            CycleGoalOperationResult expected = CycleGoalRules.Apply(state.Goals, command!, current.ContextSequence,
                checked(current.LastOperationSequence + 1));
            Require(expected.Success && expected.After.SameAs(after.InteractionStates[fact.ActorId!].Goals) &&
                expected.Receipt.SameAs(fact.GoalOperation) && fact.Result == expected.Receipt.Message,
                "Recorded goal effects, identities and receipt differ from the accepted operation.");
        } else if (fact.Kind == CycleEventKind.OperationRejected && knownGoal) {
            CycleGoalCommandParser.TryGetKind(fact.RawInput!, out CycleGoalOperationKind kind);
            Require(CycleGoalRules.Reject(kind, fact.Result!).SameAs(fact.GoalOperation),
                "A rejected goal operation must retain its bounded typed refusal.");
        } else {
            Require(fact.GoalOperation is null, "Only goal submissions carry goal receipts.");
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
