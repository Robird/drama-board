using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Server.FreePlay;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>Adapts the inner Graph Spatial rule to the FreePlay world so one traversal arrival ends the running move in the same batch.</summary>
internal sealed class FreePlaySpatialRule : IOccurrenceRule<FreePlayWorld, FreePlayCandidate, FreePlayFact> {
    private readonly SpatialOccurrenceRule _spatial;

    public FreePlaySpatialRule(GraphDefinition definition) {
        _spatial = new(definition);
    }

    public IReadOnlyList<OccurrenceCandidate<FreePlayCandidate>> Forecast(FreePlayWorld world, SimulationRules rules) =>
        _spatial.Forecast(world.Spatial, rules)
            .Select(candidate => new OccurrenceCandidate<FreePlayCandidate>(candidate.Key, candidate.Due, new(candidate)))
            .ToArray();

    public async ValueTask<TransitionDraft<FreePlayFact>> PlanSelectedAsync(FreePlayWorld world,
        OccurrenceCandidate<FreePlayCandidate> winner, CancellationToken cancellationToken) {
        if (winner.Data.Spatial is not { } spatial) {
            throw new InvalidOperationException("The FreePlay Spatial rule only plans Spatial candidates.");
        }

        TransitionDraft<GraphSpatialFact> draft =
            await _spatial.PlanSelectedAsync(world.Spatial, spatial, cancellationToken);
        if (spatial.Data is not TraversalArrivalOccurrenceData) {
            return new TransitionDraft<FreePlayFact>(draft.Facts.Select(fact => new SpatialFact(fact)));
        }

        if (draft.Facts.Count != 1 || draft.Facts[0] is not TraversalArrivedFact arrived) {
            throw new InvalidOperationException(
                "A traversal arrival must plan exactly one TraversalArrivedFact.");
        }

        if (world.Process.Mode is not RunningMoveProcess running ||
            running.MovementGeneration != arrived.ExpectedMovementGeneration) {
            throw new InvalidOperationException(
                "Committing a traversal arrival requires a running move with the expected movement generation.");
        }

        return new TransitionDraft<FreePlayFact>(
            [new SpatialFact(arrived), new ProcessFact(new MoveArrivedFact(arrived.ExpectedMovementGeneration))]);
    }
}
