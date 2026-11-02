using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

internal static class ViewContextFixture {
    public static CharacterInteractionState State(PlaygroundObservation observation, long sequence = 1, string? draftHandoff = null) =>
        new(observation.Actor.Id, new CharacterContext(sequence, CharacterContextStatus.Open,
            new CharacterContextOpening(PlaygroundBackground.For(observation), observation, null, observation.Time,
                PlaygroundOutcomeActivityState.Completed)), draftHandoff: draftHandoff);

    // Presentation-only fixtures explicitly install their opening. Kernel integration tests must commit opening normally.
    public static PlaygroundDecisionContext Create(PlaygroundWorld world, string actorId, long sequence = 1) {
        if (world.Business.InteractionStates[actorId].CurrentContext is not null) {
            return PlaygroundDecisionContext.Create(world, actorId);
        }
        var observation = PlaygroundDecisionContext.Observe(world, actorId);
        world = PlaygroundEngine.Rebuild(world.Business.Replace(world.Time,
            interactionChanges: [State(observation, sequence)]), world.Time);
        return PlaygroundDecisionContext.Create(world, actorId);
    }
}
