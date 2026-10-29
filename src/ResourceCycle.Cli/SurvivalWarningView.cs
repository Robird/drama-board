using DramaBoard.ResourceCycle;

/// <summary>Names the newly raised warnings recorded in an event, never today's body values.</summary>
internal static class SurvivalWarningView {
    public static string Attributes(IEnumerable<CycleObservedEvent> events) {
        var fields = events.Where(e => e.Kind == CycleObservedEventKind.SurvivalWarning)
            .SelectMany(e => e.Effects)
            .Where(effect => effect.Key.Category == "body" && effect.Before == CycleVisibleValue.Flag(false) &&
                effect.After == CycleVisibleValue.Flag(true))
            .Select(effect => effect.Key.Field).ToHashSet(StringComparer.Ordinal);
        return string.Join("、", new[] { ("warning-food", "食物"), ("warning-water", "饮水"), ("warning-warmth", "温暖") }
            .Where(pair => fields.Contains(pair.Item1)).Select(pair => pair.Item2));
    }
}
