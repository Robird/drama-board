using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

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
        var world = new CycleWorld(business, CyclePredictionMaintenance.Rebuild(business), business.Expedition is { } expedition
            ? CycleExpeditionMap.Simulation.Rebuild(expedition.Spatial, boundaryTime) : null);
        Validate(world, boundaryTime);
        return world;
    }
    public static CycleWorld Fold(CycleWorld world, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        CycleBusinessState business = CycleFactApplication.Apply(world.Business, instant, fact);
        return new(business, world.Forecasts, business.Expedition is { } expedition
            ? new SpatialSimulationState(expedition.Spatial, world.Spatial!.Contacts) : null);
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
        SpatialSimulationState? spatial = folded.Spatial;
        if (spatial is not null) {
            GraphSpatialFact[] spatialFacts = facts.SelectMany(fact => fact.SpatialFacts).ToArray();
            spatial = CycleExpeditionMap.Simulation.Complete(before.Spatial!, instant, spatialFacts, spatial);
        }
        return ReferenceEquals(forecasts, folded.Forecasts) && ReferenceEquals(spatial, folded.Spatial)
            ? folded : new(folded.Business, forecasts, spatial);
    }
    public static void Validate(CycleWorld world, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(world);
        world.Business.Validate();
        if (world.Time != boundaryTime) { throw new InvalidOperationException("Business time differs from its publication boundary."); }
        CyclePredictionMaintenance.Validate(world.Business, world.Forecasts, boundaryTime);
        if (world.Expedition is null ? world.Spatial is not null : world.Spatial is null || !ReferenceEquals(world.Expedition.Spatial, world.Spatial.Business)) {
            throw new InvalidOperationException("Runtime Spatial must share the expedition's authoritative business graph.");
        }
        if (world.Spatial is not null) { CycleExpeditionMap.Simulation.Validate(world.Spatial, boundaryTime); }
    }

    private sealed class CycleRule(CycleMetrics metrics, ICycleCharacterController? characterController) : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        private readonly SpatialOccurrenceRule _spatialRule = new(CycleExpeditionMap.Graph);
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) {
            if (world.ExpeditionSummary != CycleExpeditionSummary.Exploring) { return []; }
            var predictions = world.Forecasts.Predictions.ToList();
            if (world.Spatial is not null) {
                predictions.AddRange(_spatialRule.Forecast(world.Spatial, rules).Select(candidate =>
                    new CyclePrediction($"spatial:{candidate.Key}", CyclePredictionKind.SpatialOccurrence,
                        candidate.Data is TraversalArrivalOccurrenceData arrival ? arrival.EntityId.Value : "world", candidate.Due.ModelTime, 0) { SpatialCandidate = candidate }));
            }
            if (predictions.Count == 0) { return []; }
            // One cross-module policy: death first; environmental changes, arrival and completion precede warnings/input.
            ModelTime due = predictions.Min(p => p.Due);
            CyclePredictionKind kind = predictions.Where(p => p.Due == due).Min(p => p.Kind);
            CyclePrediction[] eligible = predictions.Where(p => p.Due == due && p.Kind == kind).ToArray();
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
            if (prediction.SpatialCandidate is { } spatialCandidate) {
                if (world.Spatial is null || winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != spatialCandidate.Due.ModelTime) {
                    throw new InvalidOperationException("Invalid composed spatial candidate.");
                }
                TransitionDraft<GraphSpatialFact> spatialDraft = await _spatialRule.PlanSelectedAsync(world.Spatial, spatialCandidate, cancellationToken).ConfigureAwait(false);
                return new([CycleInteractionRules.Enrich(world, CyclePlanning.Observe(world, CycleSurvivalRules.Enrich(world,
                    CyclePlanning.Arrive(world, prediction.Due, spatialDraft.Facts))))]);
            }
            if (winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != prediction.Due || prediction.Due < world.Time ||
                !world.Forecasts.Predictions.Any(p => p.ToString() == prediction.ToString()) || CyclePredictionMaintenance.Predict(world.Business, prediction.OwnerId)?.ToString() != prediction.ToString()) {
                throw new InvalidOperationException("A stale or invalid prediction cannot execute.");
            }
            CycleFact fact;
            CycleControllerOperation? operation = null;
            if (prediction.Kind == CyclePredictionKind.RequestDecision) {
                if (world.Business.InteractionStates[prediction.TargetId].CurrentContext?.Status != CharacterContextStatus.Open) {
                    return new([CycleInteractionRules.OpenContext(world, prediction.TargetId)]);
                }
                if (characterController is null) { throw new InvalidOperationException($"A playable world requires an {nameof(ICycleCharacterController)}."); }
                CycleDecisionContext decision = CycleDecisionContext.Create(world, prediction.TargetId);
                operation = await characterController.ChooseCommandAsync(decision, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (operation is null || string.IsNullOrWhiteSpace(operation.ToolCallId) || string.IsNullOrWhiteSpace(operation.OperationRecordId) || operation.RawInput is null) {
                    throw new InvalidOperationException("A controller operation requires captured tool-call and operation record identities and raw input.");
                }
                CycleCommand? command = CycleCommandParser.Parse(operation.RawInput);
                string? rejection = command is null ? "无法识别行动格式，请使用当前列出的行动命令。" : decision.Check(command);
                if (rejection is not null) {
                    return new([CycleInteractionRules.Reject(world, prediction.TargetId, operation, rejection)]);
                }
                if (command is null) { throw new InvalidOperationException("A successful parse requires an action."); }
                if (command.Kind == CycleCommandKind.Handoff) {
                    return new([CycleInteractionRules.Enrich(world, CycleInteractionRules.EditHandoff(world, prediction.TargetId).WithOperation(operation), operation)]);
                }
                fact = CyclePlanning.Decision(world, prediction.TargetId, command).WithOperation(operation);
            } else { fact = CyclePlanning.Occurrence(world, prediction); }
            return new([CycleInteractionRules.Enrich(world, CyclePlanning.Observe(world, CycleSurvivalRules.Enrich(world, fact)), operation)]);
        }
        private static OccurrenceCandidate<CyclePrediction> Candidate(CyclePrediction prediction) =>
            new(new CandidateKey(prediction.ToString()), new CandidateDue(prediction.Due), prediction);
    }
}
