using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial;

/// <summary>Projects committed Spatial events against one pinned immutable definition.</summary>
public sealed class SpatialReducer : IEventReducer<SpatialState, SpatialEvent>
{
    private readonly SpatialDefinition _definition;

    /// <summary>Initializes a reducer bound to immutable spatial content.</summary>
    public SpatialReducer(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <inheritdoc />
    public SpatialState Apply(SpatialState state, DomainEvent<SpatialEvent> domainEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(domainEvent);
        SpatialStateValidator.ValidateStamp(_definition, state);
        return SpatialProjector.Apply(
            _definition,
            state,
            domainEvent.Kind,
            domainEvent.Payload,
            domainEvent.Timestamp.ModelTime);
    }
}
