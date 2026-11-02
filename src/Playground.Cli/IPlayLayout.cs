using DramaBoard.Playground;

/// <summary>Read-only presentation of a character's observation. Layouts never receive the world.</summary>
internal interface IPlayLayout {
    string Id { get; }
    string Name { get; }
    string Render(PlaygroundObservation observation, CharacterInteractionState? interactionState = null);
    string RenderFull(PlaygroundViewSnapshot snapshot);
    string RenderUpdate(PlaygroundViewUpdate update) => update.FullTail
        ? PlaygroundUpdateRenderer.RenderEvents(update) + RenderFull(update.Snapshot)
        : PlaygroundUpdateRenderer.Render(update);
    string RenderActionHelp(PlaygroundObservation observation);
}

internal sealed class ClassicPlayLayout : IPlayLayout {
    public string Id => "classic";
    public string Name => "完整分栏";
    public string Render(PlaygroundObservation observation, CharacterInteractionState? interactionState = null) => PlayView.Render(observation, interactionState);
    public string RenderFull(PlaygroundViewSnapshot snapshot) => PlayView.Render(snapshot);
    public string RenderActionHelp(PlaygroundObservation observation) => PlayView.RenderCommands(observation);
}
