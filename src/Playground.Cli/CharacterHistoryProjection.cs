using DramaBoard.Playground;

internal sealed record CharacterAdvanceSnapshot(long SourceContextSequence, long OperationSequence, string OperationRecordId);
internal sealed record CharacterOpeningSnapshot(string Text, IReadOnlyList<long> ExperienceSequences,
    CharacterAdvanceSnapshot? PreviousAdvance);
internal sealed record CharacterItemSnapshot(long ItemSequence, long? OperationSequence, string? OperationRecordId,
    IReadOnlyList<long> ExperienceSequences, string Text);
internal sealed record CharacterMaterialSnapshot(long ContextSequence, string Status, string? StateCommitId,
    string ObservationText, CharacterOpeningSnapshot Opening, IReadOnlyList<CharacterItemSnapshot> Items,
    long? LineageId = null, long? TransitionCount = null, long? TimeTicks = null);

/// <summary>Frozen readable material and semantic identities. Contains no live world or durable object graph.</summary>
internal static class CharacterHistoryProjection {
    public static CharacterMaterialSnapshot Capture(CharacterInteractionState interaction, string? stateCommitId,
        long? lineageId = null, long? transitionCount = null, long? timeTicks = null) {
        ArgumentNullException.ThrowIfNull(interaction);
        CharacterContext context = interaction.CurrentContext
            ?? throw new InvalidOperationException("Character history requires a committed Context.");
        var layout = new DecisionPlayLayout();
        var opening = new CharacterOpeningSnapshot(CharacterContextRenderer.RenderOpening(interaction, layout),
            context.Opening.Experiences.Select(experience => experience.ExperienceSequence).ToArray(),
            context.Opening.PreviousAdvance is { } advance
                ? new(advance.SourceContextSequence, advance.OperationSequence, advance.OperationRecordId) : null);
        var items = new List<CharacterItemSnapshot>();
        long previous = 0;
        foreach (CharacterContextItem item in context.Items) {
            // Render this semantic group using its frozen prefix. No Markdown is parsed to recover identities.
            var prefix = new CharacterContext(context.ContextSequence, context.Status, context.Opening,
                context.Items.TakeWhile(candidate => candidate.ItemSequence <= item.ItemSequence),
                item.ItemSequence + 1, context.LastOperationSequence);
            var prefixState = new CharacterInteractionState(interaction.ActorId, prefix);
            string text = CharacterContextRenderer.RenderItems(prefixState, layout, previous);
            items.Add(new(item.ItemSequence, item.OperationSequence, item.OperationRecordId,
                item.Experiences.Select(experience => experience.ExperienceSequence).ToArray(), text));
            previous = item.ItemSequence;
        }
        return new(context.ContextSequence, context.Status.ToString(), stateCommitId,
            CharacterContextRenderer.RenderLatestState(interaction, layout), opening, items.AsReadOnly(),
            lineageId, transitionCount, timeTicks);
    }
}
