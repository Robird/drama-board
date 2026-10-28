using DramaBoard.ResourceCycle;

/// <summary>Read-only presentation of a character's observation. Layouts never receive the world.</summary>
internal interface IPlayLayout {
    string Id { get; }
    string Name { get; }
    string Render(CycleObservation observation);
    string RenderFull(CycleViewSnapshot snapshot) => Render(snapshot.Observation);
    string RenderUpdate(CycleViewUpdate update) => update.FullTail
        ? CycleUpdateRenderer.RenderEvents(update) + RenderFull(update.Snapshot)
        : CycleUpdateRenderer.Render(update);
    string RenderHelp(CycleObservation observation);
    string RenderPrompt(CycleObservation observation);
}

internal sealed class ClassicPlayLayout : IPlayLayout {
    public string Id => "classic";
    public string Name => "完整分栏";
    public string Render(CycleObservation observation) => PlayView.Render(observation);
    public string RenderHelp(CycleObservation observation) => PlayView.RenderCommands(observation);
    public string RenderPrompt(CycleObservation observation) => $"{observation.Actor.Id}> ";
}
