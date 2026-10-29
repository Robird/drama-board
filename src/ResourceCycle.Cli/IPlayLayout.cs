using DramaBoard.ResourceCycle;

/// <summary>Read-only presentation of a character's observation. Layouts never receive the world.</summary>
internal interface IPlayLayout {
    string Id { get; }
    string Name { get; }
    string Render(CycleObservation observation, CycleDecisionContinuity? continuity = null);
    string RenderFull(CycleViewSnapshot snapshot) => Render(snapshot.Observation, snapshot.Continuity);
    string RenderUpdate(CycleViewUpdate update) => update.FullTail
        ? CycleUpdateRenderer.RenderEvents(update) + RenderFull(update.Snapshot)
        : CycleUpdateRenderer.Render(update);
    string RenderActionHelp(CycleObservation observation);
}

internal sealed class ClassicPlayLayout : IPlayLayout {
    public string Id => "classic";
    public string Name => "完整分栏";
    public string Render(CycleObservation observation, CycleDecisionContinuity? continuity = null) => PlayView.Render(observation, continuity);
    public string RenderActionHelp(CycleObservation observation) => PlayView.RenderCommands(observation);
}
