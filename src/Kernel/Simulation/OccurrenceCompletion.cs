using DramaBoard.Kernel.Journal;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Materials for observing one successfully published and installed State.</summary>
public sealed class OccurrenceCompletion<TFact>(KernelCursor cursor, OccurrenceEvent<TFact> occurrence)
{
    public KernelCursor Cursor { get; } = cursor;
    public OccurrenceEvent<TFact> Event { get; } = occurrence;
}
