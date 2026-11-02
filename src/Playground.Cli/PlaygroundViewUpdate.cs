using System.Collections.ObjectModel;
using DramaBoard.Playground;

internal sealed record PlaygroundObservedEventSource(long LineageId, long TransitionCount, int FactOrdinal, int EventOrdinal,
    string? OperationRecordId, string? ToolCallId);

/// <summary>One committed batch and the latest values selected by effects, semantic changes and necessary context.</summary>
internal sealed class PlaygroundViewUpdate {
    public PlaygroundViewSnapshot Snapshot { get; }
    public IReadOnlyList<PlaygroundObservedEvent> Events { get; }
    public IReadOnlyDictionary<PlaygroundStateKey, PlaygroundVisibleValue> CurrentValues { get; }
    public PlayDeliveryPhase Phase { get; }
    public bool FullTail { get; }
    public bool GoalsChanged { get; }
    public IReadOnlyList<PlaygroundObservedEventSource> Sources { get; }
    private readonly IReadOnlyDictionary<PlaygroundStateKey, string> _labels;
    public string Label(PlaygroundStateKey key) => _labels.GetValueOrDefault(key) ?? Snapshot.Label(key);

    private PlaygroundViewUpdate(PlaygroundViewSnapshot snapshot, IReadOnlyList<PlaygroundObservedEvent> events,
        Dictionary<PlaygroundStateKey, PlaygroundVisibleValue> currentValues, PlayDeliveryPhase phase, bool fullTail,
        IReadOnlyList<PlaygroundObservedEventSource> sources, IReadOnlyDictionary<PlaygroundStateKey, string> labels, bool goalsChanged) {
        Snapshot = snapshot; Events = events; CurrentValues = new ReadOnlyDictionary<PlaygroundStateKey, PlaygroundVisibleValue>(currentValues);
        Phase = phase; FullTail = fullTail;
        Sources = sources;
        _labels = labels;
        GoalsChanged = goalsChanged;
    }

    public static PlaygroundViewUpdate Create(PlaygroundViewSnapshot previous, PlaygroundViewSnapshot current,
        IEnumerable<PlaygroundObservedEvent> events, PlayDeliveryPhase phase, bool fullTail = false,
        IReadOnlyList<PlaygroundObservedEventSource>? sources = null) {
        var batch = events.ToArray();
        if (sources is not null && sources.Count != batch.Length) { throw new ArgumentException("Source identities must match the event batch.", nameof(sources)); }
        var visibleIndices = Enumerable.Range(0, batch.Length).Where(i => batch[i].ObserverId == current.Observation.Actor.Id).ToArray();
        var visibleEvents = visibleIndices.Select(i => batch[i]).ToArray();
        var visibleSources = sources is null ? [] : visibleIndices.Select(i => sources[i]).ToArray();
        var keys = visibleEvents.SelectMany(e => e.Effects).Select(e => e.Key).ToHashSet();
        foreach (var key in previous.Values.Keys.Concat(current.Values.Keys).Distinct()) {
            if (previous.Resolve(key) != current.Resolve(key)) { keys.Add(key); }
        }
        foreach (var rateKey in previous.RateExplanations.Keys.Concat(current.RateExplanations.Keys).Distinct()) {
            var valueKey = new PlaygroundStateKey(rateKey.Category, rateKey.ObjectId, rateKey.Field[..^5]);
            bool changed = !previous.RateExplanations.TryGetValue(rateKey, out var before) ||
                !current.RateExplanations.TryGetValue(rateKey, out var after) || !before.SameAs(after);
            if (changed || keys.Contains(rateKey) || keys.Contains(valueKey)) {
                keys.Add(valueKey);
                keys.Add(rateKey);
            }
        }
        string actor = current.Observation.Actor.Id;
        if (keys.Any(key => key.Category is "body" or "inventory" or "money" or "trade")) {
            var warnings = new PlaygroundStateKey("body", actor, "warnings");
            if (current.Resolve(warnings).Kind != PlaygroundVisibleValueKind.KnownAbsent) { keys.Add(warnings); }
            if (current.Observation.Actor.Activity is not null) { keys.Add(new("activity", actor, "due")); }
            if (current.Observation.ArrivalDue is not null) { keys.Add(new("location", actor, "arrival")); }
        }
        var labels = keys.ToDictionary(key => key, key => current.KnownLabel(key) ?? previous.KnownLabel(key) ??
            (key.Category == "inventory" && visibleEvents.FirstOrDefault(e => e.SubjectId == key.ObjectId)?.SubjectName is { } name
                ? name + "库存" : current.Label(key)));
        return new(current, Array.AsReadOnly(visibleEvents), keys.OrderBy(k => k.Category, StringComparer.Ordinal)
            .ThenBy(k => k.ObjectId, StringComparer.Ordinal).ThenBy(k => k.Field, StringComparer.Ordinal)
            .ToDictionary(key => key, current.Resolve), phase, fullTail, Array.AsReadOnly(visibleSources),
            new ReadOnlyDictionary<PlaygroundStateKey, string>(labels), current.IncludeGoals &&
            !(previous.Observation.Goals is { } beforeGoals ? beforeGoals.SameAs(current.Observation.Goals) : current.Observation.Goals is null));
    }
}
