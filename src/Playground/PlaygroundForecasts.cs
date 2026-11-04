using DramaBoard.Kernel.Time;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

public sealed class PlaygroundBinding(string ownerId, PlaygroundEventKind eventKind, string targetId) {
    public string OwnerId { get; } = ownerId;
    public PlaygroundEventKind EventKind { get; } = eventKind;
    public string TargetId { get; } = targetId;
    public override string ToString() => $"{OwnerId}:{EventKind}:{TargetId}";
}

/// <summary>Runtime prediction; activity sequence is independent of attention revisions.</summary>
public sealed class PlaygroundPrediction {
    public string OwnerId { get; }
    public PlaygroundPredictionKind Kind { get; }
    public string TargetId { get; }
    public ModelTime Due { get; }
    public long Generation { get; }
    public int Sequence { get; }
    internal OccurrenceCandidate<SpatialOccurrenceData>? SpatialCandidate { get; init; }
    public PlaygroundPrediction(string ownerId, PlaygroundPredictionKind kind, string targetId, ModelTime due, long generation, int sequence = 0) {
        OwnerId = ownerId; Kind = kind; TargetId = targetId; Due = due; Generation = generation; Sequence = sequence;
    }
    /// <summary>Encodes the scheduling identity independently of diagnostic formatting.</summary>
    public CandidateKey CreateCandidateKey() => new(FormattableString.Invariant($"{OwnerId}:{Kind}:{TargetId}:{Due.Ticks}:{Generation}:{Sequence}"));

    /// <summary>Compares the six scheduling fields, excluding the composed spatial candidate's planning data.</summary>
    public bool HasSameIdentity(PlaygroundPrediction? other) => other is not null &&
        OwnerId == other.OwnerId && Kind == other.Kind && TargetId == other.TargetId && Due == other.Due &&
        Generation == other.Generation && Sequence == other.Sequence;

    public override string ToString() => $"{Kind} for {TargetId} at {Due} (owner={OwnerId}, generation={Generation}, sequence={Sequence})";
}

/// <summary>Immutable derived predictions and fixed coarse routing. No part of this object is durable.</summary>
public sealed class PlaygroundForecasts {
    private readonly IReadOnlyList<PlaygroundPrediction> _predictions;
    private readonly IReadOnlyList<PlaygroundBinding> _bindings;
    private readonly Dictionary<PlaygroundEventKind, PlaygroundBinding[]> _index;
    public IReadOnlyList<PlaygroundPrediction> Predictions => _predictions;
    public IReadOnlyList<PlaygroundBinding> Bindings => _bindings;
    internal PlaygroundForecasts(IEnumerable<PlaygroundPrediction> predictions, IEnumerable<PlaygroundBinding> bindings) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = Array.AsReadOnly(bindings.ToArray());
        _index = _bindings.GroupBy(b => b.EventKind).ToDictionary(g => g.Key,
            g => g.OrderBy(b => b.OwnerId, StringComparer.Ordinal).ToArray());
    }
    private PlaygroundForecasts(IEnumerable<PlaygroundPrediction> predictions, PlaygroundForecasts previous) {
        _predictions = Array.AsReadOnly(predictions.OrderBy(p => p.OwnerId, StringComparer.Ordinal).ToArray());
        _bindings = previous._bindings; _index = previous._index;
    }
    internal PlaygroundForecasts WithPredictions(IEnumerable<PlaygroundPrediction> predictions) => new(predictions, this);
    internal IEnumerable<PlaygroundBinding> FindBindings(PlaygroundFact fact) => _index.GetValueOrDefault(fact.Kind, [])
        .Where(binding => binding.TargetId == "*" || binding.TargetId == fact.ActorId || binding.TargetId == fact.ResourceId);
    public override string ToString() => $"predictions[{string.Join(';', _predictions)}]bindings[{string.Join(';', _bindings)}]";
}
