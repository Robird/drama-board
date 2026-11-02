using DramaBoard.Playground;

/// <summary>Names the newly raised warnings recorded in an event, never today's body values.</summary>
internal static class SurvivalWarningView {
    public static string Attributes(IEnumerable<PlaygroundObservedEvent> events) {
        var fields = events.Where(e => e.Kind == PlaygroundObservedEventKind.SurvivalWarning)
            .SelectMany(e => e.Effects)
            .Where(effect => effect.Key.Category == "body" && effect.Before == PlaygroundVisibleValue.Flag(false) &&
                effect.After == PlaygroundVisibleValue.Flag(true))
            .Select(effect => effect.Key.Field).ToHashSet(StringComparer.Ordinal);
        return string.Join("、", new[] { ("warning-food", "食物"), ("warning-water", "饮水"), ("warning-warmth", "温暖") }
            .Where(pair => fields.Contains(pair.Item1)).Select(pair => pair.Item2));
    }
}
