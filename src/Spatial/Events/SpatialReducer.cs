using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Folds one committed Spatial fact against immutable state.</summary>
public sealed class SpatialReducer
{
    private readonly SpatialDefinition _definition;

    /// <summary>Initializes a reducer bound to immutable spatial content.</summary>
    public SpatialReducer(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <summary>Applies one raw fact at its batch-shared logical instant.</summary>
    public SpatialState Apply(SpatialState state, LogicalInstant instant, SpatialEvent fact)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fact);
        SpatialStateValidator.ValidateStamp(_definition, state);
        return SpatialProjector.Apply(_definition, state, fact, instant.ModelTime);
    }
}
