using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

internal static class ViewContextFixture {
    public static CharacterInteractionState State(CycleObservation observation, long sequence = 1, string? draftHandoff = null) =>
        new(observation.Actor.Id, new CharacterContext(sequence, CharacterContextStatus.Open,
            new CharacterContextOpening(CycleBackground.For(observation), observation, null, observation.Time,
                CycleOutcomeActivityState.Completed)), draftHandoff: draftHandoff);

    // Presentation-only fixtures explicitly install their opening. Kernel integration tests must commit opening normally.
    public static CycleDecisionContext Create(CycleWorld world, string actorId, long sequence = 1) {
        if (world.Business.InteractionStates[actorId].CurrentContext is not null) {
            return CycleDecisionContext.Create(world, actorId);
        }
        var observation = CycleDecisionContext.Observe(world, actorId);
        world = CycleEngine.Rebuild(world.Business.Replace(world.Time,
            interactionChanges: [State(observation, sequence)]), world.Time);
        return CycleDecisionContext.Create(world, actorId);
    }
}
