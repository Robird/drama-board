using DramaBoard.Kernel.Simulation;
using DramaBoard.Playground;

/// <summary>A transient developer view of an already committed occurrence, never character material.</summary>
internal sealed record PlaygroundDiagnosticRecord(
    OccurrenceCompletion<PlaygroundFact> Completion,
    IReadOnlyList<PlaygroundExperienceAssociation> Experiences);

/// <summary>Joins the world's fact/event coordinates to the receiver's durable experience identity.</summary>
internal sealed record PlaygroundExperienceAssociation(
    int FactOrdinal, int EventOrdinal, string ActorId, long ExperienceSequence);

internal static class PlaygroundContextDiagnostics {
    /// <summary>
    /// Called only after E/S publication and installation, never from recovery. The completion retains
    /// structured world facts and their interaction replacements; no string rendering or re-perception
    /// is involved. A callback failure escapes the host loop without retrying the committed occurrence.
    /// </summary>
    internal static void Publish(OccurrenceCompletion<PlaygroundFact> completion, Action<PlaygroundDiagnosticRecord>? sink) {
        if (sink is null) { return; }
        var associations = new List<PlaygroundExperienceAssociation>();
        for (int factOrdinal = 0; factOrdinal < completion.Event.Facts.Count; factOrdinal++) {
            PlaygroundFact fact = completion.Event.Facts[factOrdinal];
            foreach (CharacterInteractionState state in fact.InteractionChanges) {
                PlaygroundObservedEvent[] events = fact.ObservedEvents.Where(entry => entry.ObserverId == state.ActorId).ToArray();
                // Planning appends exactly these events, in receiver order, using consecutive identities.
                // The committed replacement carries the first unallocated identity; old retained
                // experiences and context opening transfers must not be reported again as new.
                long sequence = state.NextExperienceSequence - events.Length;
                foreach (PlaygroundObservedEvent entry in events) {
                    associations.Add(new(factOrdinal, entry.EventOrdinal, state.ActorId, sequence++));
                }
            }
        }
        try {
            sink(new(completion, associations.AsReadOnly()));
        } catch (Exception error) {
            // Normalize callback failures so the CLI reports the committed boundary even when the
            // callback happens to throw an exception otherwise used for a graceful Console exit.
            throw new IOException($"开发者诊断输出失败；转换 {completion.Cursor.Version.LineageId}:" +
                $"{completion.Cursor.Version.TransitionCount} 已提交，当前会话已停止。", error);
        }
    }
}
