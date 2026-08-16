using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Kernel.Simulation;

/// <summary>Forecasts candidate futures from a world value and resolves a selected candidate into a replacement world.</summary>
public interface ISimSystem<TWorld, TCandidatePayload, TEventPayload>
{
    /// <summary>Returns the system's current candidate futures without modifying the supplied world.</summary>
    IReadOnlyList<EventCandidate<TCandidatePayload>> ForecastNext(TWorld world, ModelTime now);

    /// <summary>Resolves one selected candidate without modifying the supplied world.</summary>
    ResolveResult<TWorld, TEventPayload> Resolve(
        TWorld world,
        EventCandidate<TCandidatePayload> candidate);
}