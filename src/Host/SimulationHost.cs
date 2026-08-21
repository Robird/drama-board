using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Host;

/// <summary>Drives authoritative one-transition Kernel steps until a normal terminal status.</summary>
public static class SimulationHost
{
    /// <summary>Repeats public Kernel steps without adding another scheduling or commit authority.</summary>
    public static async ValueTask<HostRunResult<TWorld>> RunUntilAsync<TWorld, TCandidateData, TFact>(
        SimulationKernel<TWorld, TCandidateData, TFact> kernel,
        ModelTime notAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        int committedTransitionCount = 0;
        while (true)
        {
            StepStatus status = await kernel.StepAsync(notAfter, cancellationToken);
            if (status == StepStatus.Committed)
            {
                committedTransitionCount = checked(committedTransitionCount + 1);
                continue;
            }

            return new HostRunResult<TWorld>(
                kernel.World,
                kernel.Version,
                kernel.CurrentModelTime,
                status,
                committedTransitionCount);
        }
    }
}

/// <summary>Reports the committed state reached by one Host run.</summary>
public sealed record HostRunResult<TWorld>(
    TWorld World,
    WorldVersion Version,
    ModelTime CurrentModelTime,
    StepStatus Status,
    int CommittedTransitionCount);
