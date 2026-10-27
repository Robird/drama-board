using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

public sealed class CycleBinding(string ownerId, CycleEventKind eventKind, string targetId) {
    public string OwnerId { get; } = ownerId;
    public CycleEventKind EventKind { get; } = eventKind;
    public string TargetId { get; } = targetId;
    public override string ToString() => $"{OwnerId}:{EventKind}:{TargetId}";
}

/// <summary>Runtime candidate identity; activity sequence is independent of attention revisions.</summary>
public sealed class CyclePrediction {
    public string OwnerId { get; }
    public CyclePredictionKind Kind { get; }
    public string TargetId { get; }
    public ModelTime Due { get; }
    public long Generation { get; }
    public int Sequence { get; }
    public CyclePrediction(string ownerId, CyclePredictionKind kind, string targetId, ModelTime due, long generation, int sequence = 0) {
        OwnerId = ownerId; Kind = kind; TargetId = targetId; Due = due; Generation = generation; Sequence = sequence;
    }
    public override string ToString() => $"{OwnerId}:{Kind}:{TargetId}:{Due.Ticks}:{Generation}:{Sequence}";
}

/// <summary>Immutable derived predictions and fixed coarse routing. No part of this object is durable.</summary>
public sealed class CycleForecasts {
    private readonly IReadOnlyList<CyclePrediction> _predictions;
    private readonly IReadOnlyList<CycleBinding> _bindings;
    private readonly Dictionary<CycleEventKind, CycleBinding[]> _index;
    public IReadOnlyList<CyclePrediction> Predictions => _predictions;
    public IReadOnlyList<CycleBinding> Bindings => _bindings;
    internal CycleForecasts(IEnumerable<CyclePrediction> predictions, IEnumerable<CycleBinding> bindings) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = Array.AsReadOnly(bindings.ToArray());
        _index = _bindings.GroupBy(b => b.EventKind).ToDictionary(g => g.Key,
            g => g.OrderBy(b => b.OwnerId, StringComparer.Ordinal).ToArray());
    }
    private CycleForecasts(IEnumerable<CyclePrediction> predictions, CycleForecasts previous) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = previous._bindings; _index = previous._index;
    }
    internal CycleForecasts WithPredictions(IEnumerable<CyclePrediction> predictions) => new(predictions, this);
    internal IEnumerable<CycleBinding> FindBindings(CycleFact fact) => _index.GetValueOrDefault(fact.Kind, [])
        .Where(binding => binding.TargetId == "*" || binding.TargetId == fact.ActorId || binding.TargetId == fact.ResourceId);
    public override string ToString() => $"predictions[{string.Join(';', _predictions)}]bindings[{string.Join(';', _bindings)}]";
}
