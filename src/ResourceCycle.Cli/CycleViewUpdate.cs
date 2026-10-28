using System.Collections.ObjectModel;
using DramaBoard.ResourceCycle;

internal sealed record CycleObservedEventSource(long LineageId, long TransitionCount, int FactOrdinal, int EventOrdinal,
    string? OperationRecordId, string? ToolCallId);

/// <summary>One committed batch and the latest values selected by effects, semantic changes and necessary context.</summary>
internal sealed class CycleViewUpdate {
    public CycleViewSnapshot Snapshot { get; }
    public IReadOnlyList<CycleObservedEvent> Events { get; }
    public IReadOnlyDictionary<CycleStateKey, CycleVisibleValue> CurrentValues { get; }
    public PlayDeliveryPhase Phase { get; }
    public bool FullTail { get; }
    public IReadOnlyList<CycleObservedEventSource> Sources { get; }
    private readonly IReadOnlyDictionary<CycleStateKey, string> _labels;
    public string Label(CycleStateKey key) => _labels.GetValueOrDefault(key) ?? Snapshot.Label(key);

    private CycleViewUpdate(CycleViewSnapshot snapshot, IReadOnlyList<CycleObservedEvent> events,
        Dictionary<CycleStateKey, CycleVisibleValue> currentValues, PlayDeliveryPhase phase, bool fullTail,
        IReadOnlyList<CycleObservedEventSource> sources, IReadOnlyDictionary<CycleStateKey, string> labels) {
        Snapshot = snapshot; Events = events; CurrentValues = new ReadOnlyDictionary<CycleStateKey, CycleVisibleValue>(currentValues);
        Phase = phase; FullTail = fullTail;
        Sources = sources;
        _labels = labels;
    }

    public static CycleViewUpdate Create(CycleViewSnapshot previous, CycleViewSnapshot current,
        IEnumerable<CycleObservedEvent> events, PlayDeliveryPhase phase, bool fullTail = false,
        IReadOnlyList<CycleObservedEventSource>? sources = null) {
        var batch = events.ToArray();
        if (sources is not null && sources.Count != batch.Length) { throw new ArgumentException("Source identities must match the event batch.", nameof(sources)); }
        var visibleIndices = Enumerable.Range(0, batch.Length).Where(i => batch[i].ObserverId == current.Observation.Actor.Id).ToArray();
        var visibleEvents = visibleIndices.Select(i => batch[i]).ToArray();
        var visibleSources = sources is null ? [] : visibleIndices.Select(i => sources[i]).ToArray();
        var keys = visibleEvents.SelectMany(e => e.Effects).Select(e => e.Key).ToHashSet();
        foreach (var key in previous.Values.Keys.Concat(current.Values.Keys).Distinct()) {
            if (previous.Resolve(key) != current.Resolve(key)) { keys.Add(key); }
        }
        string actor = current.Observation.Actor.Id;
        if (keys.Any(key => key.Category is "body" or "inventory")) {
            var warnings = new CycleStateKey("body", actor, "warnings");
            if (current.Resolve(warnings).Kind != CycleVisibleValueKind.KnownAbsent) { keys.Add(warnings); }
            if (current.Observation.Actor.Activity is not null) { keys.Add(new("activity", actor, "due")); }
            if (current.Observation.ArrivalDue is not null) { keys.Add(new("location", actor, "arrival")); }
        }
        var labels = keys.ToDictionary(key => key, key => current.KnownLabel(key) ?? previous.KnownLabel(key) ??
            (key.Category == "inventory" && visibleEvents.FirstOrDefault(e => e.SubjectId == key.ObjectId)?.SubjectName is { } name
                ? name + "库存" : current.Label(key)));
        return new(current, Array.AsReadOnly(visibleEvents), keys.OrderBy(k => k.Category, StringComparer.Ordinal)
            .ThenBy(k => k.ObjectId, StringComparer.Ordinal).ThenBy(k => k.Field, StringComparer.Ordinal)
            .ToDictionary(key => key, current.Resolve), phase, fullTail, Array.AsReadOnly(visibleSources),
            new ReadOnlyDictionary<CycleStateKey, string>(labels));
    }
}
