using System.Globalization;
using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay;

internal sealed record FreePlayWorld(GraphSpatialState Spatial, ModelTime EffectiveTime);
internal sealed record FreePlayCandidate(OccurrenceCandidate<SpatialOccurrenceData>? Spatial);

internal sealed class FreePlayScene : IOccurrenceRule<FreePlayWorld, FreePlayCandidate, GraphSpatialFact> {
    internal static readonly EntityId Actor = new("human");
    internal GraphDefinition Definition { get; } = GraphDefinition.Create(
        [new("A"), new("B"), new("C"), new("D")],
        [Passage("ab", "A", "B", 1000), Passage("ac", "A", "C", 2000),
         Passage("bd", "B", "D", 3000), Passage("cd", "C", "D", 1000)]);
    private readonly SpatialOccurrenceRule _spatial;
    private readonly SpatialPlanner _planner;
    private readonly GraphSpatialReducer _reducer;
    private readonly SpatialQueries _queries;
    private readonly Func<FreePlayWorld, DecisionRequest, CancellationToken, ValueTask<PlayerDecision>> _decide;
    private readonly string _runId;

    internal FreePlayScene(string runId,
        Func<FreePlayWorld, DecisionRequest, CancellationToken, ValueTask<PlayerDecision>> decide) {
        _runId = runId;
        _decide = decide;
        _spatial = new(Definition);
        _planner = new(Definition);
        _reducer = new(Definition);
        _queries = new(Definition);
    }

    internal FreePlayWorld Genesis() => new(
        GraphSpatialState.Create(Definition, [new(Actor, new PlaceId("A"))]), ModelTime.Zero);

    internal FreePlayWorld Fold(FreePlayWorld world, LogicalInstant instant, GraphSpatialFact fact) =>
        new(_reducer.Apply(world.Spatial, instant, fact), instant.ModelTime);

    internal void Validate(FreePlayWorld world) => GraphSpatialStateValidator.ValidateComplete(Definition, world.Spatial);

    public IReadOnlyList<OccurrenceCandidate<FreePlayCandidate>> Forecast(FreePlayWorld world, SimulationRules rules) {
        var candidates = _spatial.Forecast(world.Spatial, rules).Select(candidate =>
            new OccurrenceCandidate<FreePlayCandidate>(candidate.Key, candidate.Due, new(candidate))).ToList();
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == Actor);
        if (actor.Location is AtPlaceLocation) {
            candidates.Add(new(DecisionKey(actor), new CandidateDue(world.EffectiveTime), new(null)));
        }
        return candidates.AsReadOnly();
    }

    public async ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(FreePlayWorld world,
        OccurrenceCandidate<FreePlayCandidate> winner, CancellationToken cancellationToken) {
        if (winner.Data.Spatial is { } spatial) {
            return await _spatial.PlanSelectedAsync(world.Spatial, spatial, cancellationToken);
        }
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == Actor);
        if (actor.Location is not AtPlaceLocation place || winner.Key != DecisionKey(actor) ||
            winner.Due.ModelTime != world.EffectiveTime) {
            throw new InvalidOperationException("Decision candidate does not match committed state.");
        }
        ObservedExit[] exits = _queries.GetExits(world.Spatial, place.PlaceId, 1)
            .Where(exit => exit.EffectiveEntryAllowed)
            .Select(exit => new ObservedExit(exit.PassageId.Value, exit.DestinationPlaceId.Value,
                exit.ExpectedDuration.Ticks, true)).ToArray();
        var request = new DecisionRequest(new DecisionId($"{_runId}:{actor.MovementGeneration}"),
            Actor.Value, world.EffectiveTime.Ticks,
            new Observation(Actor.Value, place.PlaceId.Value, world.EffectiveTime.Ticks, exits, [], [], []),
            [new AvailableAction(ActionKinds.Travel, CandidateExitIds: exits.Select(exit => exit.ExitId).ToArray())]);
        PlayerDecision decision = await _decide(world, request, cancellationToken);
        if (!TryPlan(world, request, decision, out SpatialPlanAccepted? plan)) {
            throw new InvalidOperationException("Player driver returned an invalid travel decision.");
        }
        return new TransitionDraft<GraphSpatialFact>(plan!.Facts);
    }

    internal bool TryPlan(FreePlayWorld world, DecisionRequest request, PlayerDecision decision,
        out SpatialPlanAccepted? plan) {
        plan = null;
        Intent intent = decision.Intent;
        if (!PlayerDecisionValidator.Validate(decision, request).IsValid ||
            intent.ActionKind != ActionKinds.Travel || string.IsNullOrWhiteSpace(intent.ExitId) ||
            intent.TargetActorId is not null || intent.TargetObjectId is not null || intent.DestinationId is not null ||
            intent.FreeText is not null || intent.DurationMs is not null || intent.UntilModelTimeMs is not null) {
            return false;
        }
        plan = _planner.TryStartTraversal(world.Spatial, Actor, new PassageId(intent.ExitId), 1,
            world.EffectiveTime) as SpatialPlanAccepted;
        return plan is not null;
    }

    internal string Location(FreePlayWorld world) => _queries.GetLocation(world.Spatial, Actor, world.EffectiveTime) switch {
        AtPlaceView place => place.PlaceId.Value,
        TraversingView traversal => $"{traversal.PassageId.Value} → {traversal.TargetPlaceId.Value}",
        _ => throw new InvalidOperationException("Unknown location.")
    };

    private static CandidateKey DecisionKey(SpatialEntity actor) => CandidateKey.FromUtf8(
        "free-play/decision/human/" + actor.MovementGeneration.ToString(CultureInfo.InvariantCulture));

    private static PassageDefinition Passage(string id, string from, string to, long length) =>
        new(new(id), new(from), new(to), length, new(true, true));
}
