using DramaBoard.Kernel.Journal;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Projects one committed domain event into a replacement world value.</summary>
public interface IEventReducer<TWorld, TEventPayload>
{
    /// <summary>Applies one timestamped event after it has been committed to the journal.</summary>
    TWorld Apply(TWorld world, DomainEvent<TEventPayload> domainEvent);
}