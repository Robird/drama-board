using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Playground;

/// <summary>One attribute's independent threshold estimates, not notification delivery times.
/// Null means the threshold is not reached within representable time under the stated assumptions.</summary>
[DurableType("DramaBoard.Playground.AttributeForecast", 1)]
public sealed partial record PlaygroundAttributeForecast(
    [field: DurableField(1)] ModelTime? WarningAt,
    [field: DurableField(2)] ModelTime? DepletionAt) : IDurableObject {
    public void Validate(ModelTime? observedAt = null) {
        long earliest = observedAt?.Ticks ?? 0;
        if (WarningAt is { } warning && warning.Ticks < earliest ||
            DepletionAt is { } depletion && (depletion.Ticks < earliest || WarningAt is null || WarningAt > depletion)) {
            throw new InvalidOperationException("Invalid attribute threshold forecast.");
        }
    }
}
