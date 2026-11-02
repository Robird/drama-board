using Atelia.DurableGraph;

namespace DramaBoard.Playground;

/// <summary>One evaluated additive contribution in the owning attribute's unit and time basis.</summary>
[DurableType("DramaBoard.Playground.RateContribution", 1)]
public sealed partial record RateContribution(
    [field: DurableField(1)] string SourceKey,
    [field: DurableField(2)] string SourceLabel,
    [field: DurableField(3)] int Amount) : IDurableObject {
    public void Validate() {
        if (string.IsNullOrWhiteSpace(SourceKey) || string.IsNullOrWhiteSpace(SourceLabel)) {
            throw new InvalidOperationException("A rate contribution requires a public source key and label.");
        }
    }
}

/// <summary>Frozen additive causes. Total is derived, never independently writable.</summary>
[DurableType("DramaBoard.Playground.RateBreakdown", 1)]
public sealed partial class RateBreakdown : IDurableObject {
    [DurableField(1)] private List<RateContribution> _contributions;
    public IReadOnlyList<RateContribution> Contributions => _contributions.AsReadOnly();
    public int Total => checked((int)_contributions.Sum(contribution => (long)contribution.Amount));

    public RateBreakdown(IEnumerable<RateContribution> contributions) {
        ArgumentNullException.ThrowIfNull(contributions);
        _contributions = contributions.ToList();
        if (_contributions.Any(contribution => contribution is null)) {
            throw new InvalidOperationException("A rate contribution cannot be null.");
        }
        _contributions.Sort((a, b) => StringComparer.Ordinal.Compare(a.SourceKey, b.SourceKey));
        Validate();
    }

    public bool SameAs(RateBreakdown? other) => ReferenceEquals(this, other) ||
        other is not null && _contributions.SequenceEqual(other._contributions);

    public void Validate() {
        if (_contributions is null) { throw new InvalidOperationException("Rate contributions are required."); }
        string? previous = null;
        foreach (RateContribution contribution in _contributions) {
            if (contribution is null) { throw new InvalidOperationException("A rate contribution cannot be null."); }
            contribution.Validate();
            if (previous is not null && StringComparer.Ordinal.Compare(previous, contribution.SourceKey) >= 0) {
                throw new InvalidOperationException("Rate source keys must be unique and canonically ordered.");
            }
            previous = contribution.SourceKey;
        }
        _ = Total;
    }

    /// <summary>A public cause has the same label across attributes in one character snapshot.</summary>
    internal static void ValidateSources(params RateBreakdown[] rates) {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (RateBreakdown rate in rates) {
            if (rate is null) { throw new InvalidOperationException("Every attribute requires a rate breakdown."); }
            rate.Validate();
            foreach (RateContribution contribution in rate.Contributions) {
                if (labels.TryGetValue(contribution.SourceKey, out string? label) && label != contribution.SourceLabel) {
                    throw new InvalidOperationException("The same public source must have a consistent label across attributes.");
                }
                labels[contribution.SourceKey] = contribution.SourceLabel;
            }
        }
    }
}
