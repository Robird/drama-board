using DramaBoard.Goals;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

public static class PlaygroundEngine {
    internal const int MaximumTransitionsPerModelTime = 1024;

    /// <summary>Finds the actor the existing arbitration policy would serve next, without planning or invoking a controller.</summary>
    internal static string? NextDecisionActor(PlaygroundWorld world, KernelCursor cursor) {
        IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> candidates = new PlaygroundRule(new PlaygroundMetrics(), null)
            .Forecast(world, new SimulationRules(0, MaximumTransitionsPerModelTime));
        if (candidates.Count != 1 || candidates[0].Data.Kind != PlaygroundPredictionKind.RequestDecision ||
            candidates[0].Due.ModelTime != world.Time || cursor.LastCauseKey == candidates[0].Key ||
            cursor.Version.TransitionCount == long.MaxValue || cursor.LastInstant is { } last &&
            last.ModelTime == world.Time && last.CausalOrdinal >= MaximumTransitionsPerModelTime - 1) { return null; }
        return candidates[0].Data.TargetId;
    }

    public static SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> CreateKernel(
        IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> history, PredictionMode mode = PredictionMode.Incremental,
        PlaygroundMetrics? metrics = null, IPlaygroundCharacterController? characterController = null) {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        PlaygroundMetrics measurements = metrics ?? new();
        return new(history, new SimulationRules(0, MaximumTransitionsPerModelTime), [new PlaygroundRule(measurements, characterController)],
            Fold, (before, instant, facts, folded) => Complete(before, instant, facts, folded, mode, measurements), Validate);
    }
    public static PlaygroundWorld Rebuild(PlaygroundBusinessState business, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(business);
        business.Validate();
        var world = new PlaygroundWorld(business, PlaygroundPredictionMaintenance.Rebuild(business), business.Expedition is { } expedition
            ? business.Definition.Runtime.Simulation.Rebuild(expedition.Spatial, boundaryTime) : null);
        Validate(world, boundaryTime);
        return world;
    }
    public static PlaygroundWorld Fold(PlaygroundWorld world, LogicalInstant instant, PlaygroundFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        PlaygroundBusinessState business = PlaygroundFactApplication.Apply(world.Business, instant, fact);
        return new(business, world.Forecasts, business.Expedition is { } expedition
            ? new SpatialSimulationState(expedition.Spatial, world.Spatial!.Contacts) : null);
    }
    public static PlaygroundWorld Complete(PlaygroundWorld before, LogicalInstant instant, IReadOnlyList<PlaygroundFact> facts, PlaygroundWorld folded) =>
        Complete(before, instant, facts, folded, PredictionMode.Incremental);

    /// <summary>Pure preparation: recorded business remains untouched; only affected runtime owners are recomputed.</summary>
    public static PlaygroundWorld Complete(PlaygroundWorld before, LogicalInstant instant, IReadOnlyList<PlaygroundFact> facts,
        PlaygroundWorld folded, PredictionMode mode, PlaygroundMetrics? metrics = null) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(folded);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        PlaygroundBusinessState intermediate = before.Business;
        foreach (PlaygroundFact fact in facts) {
            PlaygroundBusinessState next = PlaygroundFactApplication.Apply(intermediate, instant, fact);
            if (mode == PredictionMode.Incremental) {
                PlaygroundPredictionMaintenance.CollectDirtyOwners(intermediate, next, before.Forecasts, fact, dirty, metrics);
            }
            intermediate = next;
        }
        if (!PlaygroundFactApplication.SameFoldedBusiness(intermediate, folded.Business)) {
            throw new InvalidOperationException("Folded business does not match the recorded fact sequence.");
        }
        if (mode == PredictionMode.Full) { dirty.UnionWith(PlaygroundPredictionMaintenance.Owners(folded.Business)); }
        PlaygroundForecasts forecasts = PlaygroundPredictionMaintenance.Update(folded.Business, before.Forecasts, dirty, instant.ModelTime, metrics);
        SpatialSimulationState? spatial = folded.Spatial;
        if (spatial is not null) {
            GraphSpatialFact[] spatialFacts = facts.SelectMany(fact => fact.SpatialFacts).ToArray();
            spatial = before.Definition.Runtime.Simulation.Complete(before.Spatial!, instant, spatialFacts, spatial);
        }
        return ReferenceEquals(forecasts, folded.Forecasts) && ReferenceEquals(spatial, folded.Spatial)
            ? folded : new(folded.Business, forecasts, spatial);
    }
    public static void Validate(PlaygroundWorld world, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(world);
        world.Business.Validate();
        if (world.Time != boundaryTime) { throw new InvalidOperationException("Business time differs from its publication boundary."); }
        PlaygroundPredictionMaintenance.Validate(world.Business, world.Forecasts, boundaryTime);
        if (world.Expedition is null ? world.Spatial is not null : world.Spatial is null || !ReferenceEquals(world.Expedition.Spatial, world.Spatial.Business)) {
            throw new InvalidOperationException("Runtime Spatial must share the expedition's authoritative business graph.");
        }
        if (world.Spatial is not null) { world.Definition.Runtime.Simulation.Validate(world.Spatial, boundaryTime); }
    }

    private sealed class PlaygroundRule(PlaygroundMetrics metrics, IPlaygroundCharacterController? characterController) : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) {
            if (world.ExpeditionSummary != PlaygroundExpeditionSummary.Exploring) { return []; }
            var predictions = world.Forecasts.Predictions.ToList();
            if (world.Spatial is not null) {
                predictions.AddRange(world.Definition.Runtime.OccurrenceRule.Forecast(world.Spatial, rules).Select(candidate =>
                    new PlaygroundPrediction($"spatial:{candidate.Key}", PlaygroundPredictionKind.SpatialOccurrence,
                        candidate.Data is TraversalArrivalOccurrenceData arrival ? arrival.EntityId.Value : "world", candidate.Due.ModelTime, 0) { SpatialCandidate = candidate }));
            }
            if (predictions.Count == 0) { return []; }
            // One cross-module policy: death first; environmental changes, arrival and completion precede warnings/input.
            ModelTime due = predictions.Min(p => p.Due);
            PlaygroundPredictionKind kind = predictions.Where(p => p.Due == due).Min(p => p.Kind);
            PlaygroundPrediction[] eligible = predictions.Where(p => p.Due == due && p.Kind == kind).ToArray();
            if (kind == PlaygroundPredictionKind.RequestDecision) {
                int last = world.Characters.ToList().FindIndex(c => c.Id == world.Business.LastServedActorId);
                for (int offset = 1; offset <= world.Characters.Count; offset++) {
                    string id = world.Characters[(last + offset) % world.Characters.Count].Id;
                    PlaygroundPrediction? next = eligible.SingleOrDefault(p => p.TargetId == id);
                    if (next is not null) { return [Candidate(next)]; }
                }
                throw new InvalidOperationException("No character owns an eligible decision request.");
            }
            return eligible.Select(Candidate).ToArray();
        }
        public async ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world,
            OccurrenceCandidate<PlaygroundPrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            PlaygroundPrediction prediction = winner.Data;
            metrics.PredictionChecks++;
            if (prediction.SpatialCandidate is { } spatialCandidate) {
                if (world.Spatial is null || winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != spatialCandidate.Due.ModelTime) {
                    throw new InvalidOperationException("Invalid composed spatial candidate.");
                }
                TransitionDraft<GraphSpatialFact> spatialDraft = await world.Definition.Runtime.OccurrenceRule.PlanSelectedAsync(world.Spatial, spatialCandidate, cancellationToken).ConfigureAwait(false);
                return new([PlaygroundInteractionRules.Enrich(world, PlaygroundPlanning.Observe(world, PlaygroundSurvivalRules.Enrich(world,
                    PlaygroundPlanning.Arrive(world, prediction.Due, spatialDraft.Facts))))]);
            }
            if (winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != prediction.Due || prediction.Due < world.Time ||
                !world.Forecasts.Predictions.Any(p => p.ToString() == prediction.ToString()) || PlaygroundPredictionMaintenance.Predict(world.Business, prediction.OwnerId)?.ToString() != prediction.ToString()) {
                throw new InvalidOperationException("A stale or invalid prediction cannot execute.");
            }
            PlaygroundFact fact;
            PlaygroundControllerOperation? operation = null;
            if (prediction.Kind == PlaygroundPredictionKind.RequestDecision) {
                if (world.Business.InteractionStates[prediction.TargetId].CurrentContext?.Status != CharacterContextStatus.Open) {
                    return new([PlaygroundInteractionRules.OpenContext(world, prediction.TargetId)]);
                }
                if (characterController is null) { throw new InvalidOperationException($"A playable world requires an {nameof(IPlaygroundCharacterController)}."); }
                PlaygroundDecisionContext decision = PlaygroundDecisionContext.Create(world, prediction.TargetId);
                operation = await characterController.ChooseCommandAsync(decision, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (operation is null || string.IsNullOrWhiteSpace(operation.ToolCallId) || string.IsNullOrWhiteSpace(operation.OperationRecordId) || operation.RawInput is null) {
                    throw new InvalidOperationException("A controller operation requires captured tool-call and operation record identities and raw input.");
                }
                PlaygroundCommand? command = PlaygroundCommandParser.Parse(operation.RawInput);
                string? rejection = command is null ? "无法识别行动格式，请使用当前列出的行动命令。" : decision.Check(command);
                if (rejection is null && command is not null && PlaygroundTradeRules.IsTradeCommand(command)) {
                    rejection = PlaygroundActions.Check(world, prediction.TargetId, command);
                }
                if (command is null && GoalCommandParser.IsGoalCommand(operation.RawInput)) {
                    GoalCommandParser.TryParse(operation.RawInput, out _, out string? goalError);
                    rejection = goalError ?? rejection;
                }
                if (rejection is not null) {
                    return new([PlaygroundInteractionRules.Reject(world, prediction.TargetId, operation, rejection)]);
                }
                if (command is null) { throw new InvalidOperationException("A successful parse requires an action."); }
                if (command.Kind == PlaygroundCommandKind.Cancel && world.Expedition?.Navigation(prediction.TargetId) is not null ||
                    command.Kind == PlaygroundCommandKind.Navigate && world.Expedition?.CandidatePath(prediction.TargetId)?.Steps.Count == 0) {
                    return new([PlaygroundNavigationRules.Instant(world, prediction.TargetId, command, operation)]);
                }
                if (command.Kind is PlaygroundCommandKind.Map or PlaygroundCommandKind.Path) {
                    return new([PlaygroundPathRules.Plan(world, prediction.TargetId, command, operation)]);
                }
                if (command.Kind == PlaygroundCommandKind.Goal) {
                    return new([PlaygroundGoalInteractionRules.Plan(world, prediction.TargetId, command.Goal!, operation)]);
                }
                if (command.Kind == PlaygroundCommandKind.Handoff) {
                    return new([PlaygroundInteractionRules.Enrich(world, PlaygroundInteractionRules.EditHandoff(world, prediction.TargetId).WithOperation(operation), operation)]);
                }
                fact = PlaygroundPlanning.Decision(world, prediction.TargetId, command).WithOperation(operation);
            } else { fact = PlaygroundPlanning.Occurrence(world, prediction); }
            return new([PlaygroundInteractionRules.Enrich(world, PlaygroundPlanning.Observe(world, PlaygroundSurvivalRules.Enrich(world, PlaygroundTradeRules.Enrich(world, PlaygroundEconomyRules.Enrich(world, fact)))), operation)]);
        }
        private static OccurrenceCandidate<PlaygroundPrediction> Candidate(PlaygroundPrediction prediction) =>
            new(new CandidateKey(prediction.ToString()), new CandidateDue(prediction.Due), prediction);
    }
}
