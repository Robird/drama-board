using Atelia.DurableGraph;

namespace DramaBoard.Spatial;

/// <summary>Recorded business changes and completed prediction replacements, folded without discovery.</summary>
[DurableType("DramaBoard.Spatial.SpatialSimulationFact", 1)]
public abstract partial class SpatialSimulationFact : IDurableObject, IEquatable<SpatialSimulationFact> {
    public bool Equals(SpatialSimulationFact? other) => other is not null && Equals((object)other);
    public abstract override bool Equals(object? obj);
    public abstract override int GetHashCode();
}

[DurableType("DramaBoard.Spatial.SpatialBusinessFact", 1)]
public sealed partial class SpatialBusinessFact : SpatialSimulationFact {
    [DurableField(1)] private readonly GraphSpatialFact _fact;

    public SpatialBusinessFact(GraphSpatialFact fact) {
        ArgumentNullException.ThrowIfNull(fact);
        _fact = fact;
    }

    public GraphSpatialFact Fact => _fact;
    public override bool Equals(object? obj) => obj is SpatialBusinessFact other && Fact.Equals(other.Fact);
    public override int GetHashCode() => HashCode.Combine(typeof(SpatialBusinessFact), Fact);
}

/// <summary>Replaces complete outputs of the listed passages; an empty output clears those passages.</summary>
[DurableType("DramaBoard.Spatial.ContactPredictionsReplaced", 1)]
public sealed partial class ContactPredictionsReplaced : SpatialSimulationFact {
    [DurableField(1)] private readonly List<PassageId> _passages = [];
    [DurableField(2)] private readonly SpatialContactPredictions _predictions;

    public ContactPredictionsReplaced(IEnumerable<PassageId> passages, IEnumerable<ContactPrediction> predictions) {
        ArgumentNullException.ThrowIfNull(passages);
        _passages = [.. passages.Order()];
        foreach (PassageId passage in _passages) { SpatialIdentifier.Require(passage, nameof(passages)); }
        if (_passages.Count == 0 || _passages.Distinct().Count() != _passages.Count) {
            throw new ArgumentException("Replacement passages must be nonempty and unique.", nameof(passages));
        }
        _predictions = new SpatialContactPredictions(predictions);
        if (_predictions.Entries.Any(entry => !_passages.Contains(entry.ContactKey.PassageId))) {
            throw new ArgumentException("Every prediction must belong to a replaced passage.", nameof(predictions));
        }
    }

    public IReadOnlyList<PassageId> Passages => _passages.AsReadOnly();
    public IReadOnlyList<ContactPrediction> Predictions => _predictions.Entries;
    public override bool Equals(object? obj) => obj is ContactPredictionsReplaced other &&
        Passages.SequenceEqual(other.Passages) && Predictions.SequenceEqual(other.Predictions);
    public override int GetHashCode() {
        var hash = new HashCode();
        foreach (PassageId passage in Passages) { hash.Add(passage); }
        hash.Add(_predictions);
        return hash.ToHashCode();
    }
}
