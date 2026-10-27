using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

public static class CycleEngine {
    public static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> CreateKernel(
        IOccurrenceHistory<CycleWorld, CycleFact> history, PredictionMode mode = PredictionMode.Incremental,
        CycleMetrics? metrics = null, ICycleCharacterController? characterController = null) {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        CycleMetrics measurements = metrics ?? new();
        return new(history, new SimulationRules(0, 1024), [new CycleRule(measurements, characterController)],
            Fold, (before, instant, facts, folded) => Complete(before, instant, facts, folded, mode, measurements), Validate);
    }
    public static CycleWorld Rebuild(CycleBusinessState business, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(business);
        business.Validate();
        var world = new CycleWorld(business, CyclePredictionMaintenance.Rebuild(business));
        Validate(world, boundaryTime);
        return world;
    }
    public static CycleWorld Fold(CycleWorld world, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        return new(CycleFactApplication.Apply(world.Business, instant, fact), world.Forecasts);
    }
    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts, CycleWorld folded) =>
        Complete(before, instant, facts, folded, PredictionMode.Incremental);

    /// <summary>Pure preparation: recorded business remains untouched; only affected runtime owners are recomputed.</summary>
    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts,
        CycleWorld folded, PredictionMode mode, CycleMetrics? metrics = null) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(folded);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        CycleBusinessState intermediate = before.Business;
        foreach (CycleFact fact in facts) {
            CycleBusinessState next = CycleFactApplication.Apply(intermediate, instant, fact);
            if (mode == PredictionMode.Incremental) {
                CyclePredictionMaintenance.CollectDirtyOwners(intermediate, next, before.Forecasts, fact, dirty, metrics);
            }
            intermediate = next;
        }
        if (!CycleFactApplication.SameFoldedBusiness(intermediate, folded.Business)) {
            throw new InvalidOperationException("Folded business does not match the recorded fact sequence.");
        }
        if (mode == PredictionMode.Full) { dirty.UnionWith(CyclePredictionMaintenance.Owners(folded.Business)); }
        CycleForecasts forecasts = CyclePredictionMaintenance.Update(folded.Business, before.Forecasts, dirty, instant.ModelTime, metrics);
        return ReferenceEquals(forecasts, folded.Forecasts) ? folded : new(folded.Business, forecasts);
    }
    public static void Validate(CycleWorld world, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(world);
        world.Business.Validate();
        if (world.Time != boundaryTime) { throw new InvalidOperationException("Business time differs from its publication boundary."); }
        CyclePredictionMaintenance.Validate(world.Business, world.Forecasts, boundaryTime);
    }

    private sealed class CycleRule(CycleMetrics metrics, ICycleCharacterController? characterController) : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) {
            if (world.Forecasts.Predictions.Count == 0) { return []; }
            ModelTime due = world.Forecasts.Predictions.Min(p => p.Due);
            CyclePredictionKind kind = world.Forecasts.Predictions.Where(p => p.Due == due).Min(p => p.Kind);
            CyclePrediction[] eligible = world.Forecasts.Predictions.Where(p => p.Due == due && p.Kind == kind).ToArray();
            if (kind == CyclePredictionKind.RequestDecision) {
                int last = world.Characters.ToList().FindIndex(c => c.Id == world.Business.LastServedActorId);
                for (int offset = 1; offset <= world.Characters.Count; offset++) {
                    string id = world.Characters[(last + offset) % world.Characters.Count].Id;
                    CyclePrediction? next = eligible.SingleOrDefault(p => p.TargetId == id);
                    if (next is not null) { return [Candidate(next)]; }
                }
                throw new InvalidOperationException("No character owns an eligible decision request.");
            }
            return eligible.Select(Candidate).ToArray();
        }
        public async ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            CyclePrediction prediction = winner.Data;
            metrics.PredictionChecks++;
            if (winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != prediction.Due || prediction.Due < world.Time ||
                !world.Forecasts.Predictions.Any(p => p.ToString() == prediction.ToString()) || CyclePredictionMaintenance.Predict(world.Business, prediction.OwnerId)?.ToString() != prediction.ToString()) {
                throw new InvalidOperationException("A stale or invalid prediction cannot execute.");
            }
            CycleFact fact;
            if (prediction.Kind == CyclePredictionKind.RequestDecision) {
                if (characterController is null) { throw new InvalidOperationException($"A playable world requires an {nameof(ICycleCharacterController)}."); }
                CycleCommand command = await characterController.ChooseCommandAsync(world, prediction.TargetId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                string? rejection = CycleActions.Check(world, prediction.TargetId, command);
                if (rejection is not null) { throw new InvalidOperationException(rejection); }
                fact = CyclePlanning.Decision(world, prediction.TargetId, command);
            } else { fact = CyclePlanning.Occurrence(world, prediction); }
            return new([fact]);
        }
        private static OccurrenceCandidate<CyclePrediction> Candidate(CyclePrediction prediction) =>
            new(new CandidateKey(prediction.ToString()), new CandidateDue(prediction.Due), prediction);
    }
}
