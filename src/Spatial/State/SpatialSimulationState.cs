using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>One derived future contact between current movement segments; never persisted.</summary>
public sealed class ContactPrediction : IEquatable<ContactPrediction> {
    private readonly PassageContactKey _contactKey;
    private readonly ModelTime _due;
    private readonly PassageContactKind _kind;

    public ContactPrediction(PassageContactKey contactKey, ModelTime due, PassageContactKind kind) {
        ArgumentNullException.ThrowIfNull(contactKey);
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        _contactKey = contactKey;
        _due = due;
        _kind = kind;
    }

    public PassageContactKey ContactKey => _contactKey;
    public ModelTime Due => _due;
    public PassageContactKind Kind => _kind;
    public bool Equals(ContactPrediction? other) => other is not null &&
        ContactKey == other.ContactKey && Due == other.Due && Kind == other.Kind;
    public override bool Equals(object? obj) => Equals(obj as ContactPrediction);
    public override int GetHashCode() => HashCode.Combine(ContactKey, Due, Kind);
}

/// <summary>Immutable runtime contact cache. Empty passages still exist in GraphDefinition.</summary>
public sealed class SpatialContactPredictions : IEquatable<SpatialContactPredictions> {
    private readonly List<ContactPrediction> _entries = [];

    public SpatialContactPredictions(IEnumerable<ContactPrediction> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        ContactPrediction[] values = [.. entries];
        if (values.Any(value => value is null)) {
            throw new ArgumentException("Contact predictions cannot contain null.", nameof(entries));
        }
        _entries = [.. values.OrderBy(value => value.ContactKey)];
        for (int i = 1; i < _entries.Count; i++) {
            if (_entries[i - 1].ContactKey == _entries[i].ContactKey) {
                throw new ArgumentException("Contact prediction keys must be unique.", nameof(entries));
            }
        }
    }

    public IReadOnlyList<ContactPrediction> Entries => _entries.AsReadOnly();
    public bool Equals(SpatialContactPredictions? other) => other is not null && Entries.SequenceEqual(other.Entries);
    public override bool Equals(object? obj) => Equals(obj as SpatialContactPredictions);
    public override int GetHashCode() {
        var hash = new HashCode();
        foreach (ContactPrediction entry in Entries) { hash.Add(entry); }
        return hash.ToHashCode();
    }
}

/// <summary>Immutable runtime wrapper joining durable business state and its derived contact cache.</summary>
public sealed class SpatialSimulationState : IEquatable<SpatialSimulationState> {
    private readonly GraphSpatialState _business;
    private readonly SpatialContactPredictions _contacts;

    /// <summary>Assembles a runtime snapshot without predicting. Genesis and cold-open use SpatialSimulation.Rebuild.</summary>
    public SpatialSimulationState(GraphSpatialState business, SpatialContactPredictions contacts) {
        ArgumentNullException.ThrowIfNull(business);
        ArgumentNullException.ThrowIfNull(contacts);
        _business = business;
        _contacts = contacts;
    }

    public GraphSpatialState Business => _business;
    public SpatialContactPredictions Contacts => _contacts;
    public bool Equals(SpatialSimulationState? other) => other is not null &&
        Business.Equals(other.Business) && Contacts.Equals(other.Contacts);
    public override bool Equals(object? obj) => Equals(obj as SpatialSimulationState);
    public override int GetHashCode() => HashCode.Combine(Business, Contacts);
}
