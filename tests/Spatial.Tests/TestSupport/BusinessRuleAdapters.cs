using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests.TestSupport;

// Business/math tests keep facts without a cursor. Choose a compatible snapshot boundary;
// integration tests exercise actual publication time and prediction maintenance.
internal static class BusinessRuleWorld {
    internal static SpatialSimulationState Rebuild(SpatialSimulation simulation, GraphSpatialState world) {
        // The latest anchor is the earliest possible boundary shared by all current segments.
        // Rebuild still rejects expired arrivals, contacts or schedules at that boundary.
        ModelTime[] anchors = world.Entities.Select(entity => entity.Location).OfType<TraversingLocation>()
            .Select(traversal => traversal.AnchorTime).ToArray();
        ModelTime boundary = anchors.Length > 0 ? anchors.Max()
            : world.ScheduledPassageEntryChanges.Select(schedule => schedule.Due).DefaultIfEmpty(ModelTime.Zero).Min();
        return simulation.Rebuild(world, boundary);
    }
}

internal sealed class BusinessContactRule(GraphDefinition definition) {
    private readonly SpatialSimulation _simulation = new(definition);
    private readonly SpatialContactOccurrenceRule _rule = new(definition);
    private SpatialSimulationState Root(GraphSpatialState world) => BusinessRuleWorld.Rebuild(_simulation, world);
    public IReadOnlyList<OccurrenceCandidate<PassageContactOccurrenceData>> Forecast(GraphSpatialState world, SimulationRules rules) =>
        _rule.Forecast(Root(world), rules).Select(c => new OccurrenceCandidate<PassageContactOccurrenceData>(c.Key, c.Due, (PassageContactOccurrenceData)c.Data)).ToArray();
    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
        OccurrenceCandidate<PassageContactOccurrenceData> candidate, CancellationToken token) =>
        _rule.PlanSelectedAsync(Root(world), new(candidate.Key, candidate.Due, candidate.Data), token);
}

internal sealed class BusinessOccurrenceRule(GraphDefinition definition) {
    private readonly SpatialSimulation _simulation = new(definition);
    private readonly SpatialOccurrenceRule _rule = new(definition);
    private SpatialSimulationState Root(GraphSpatialState world) => BusinessRuleWorld.Rebuild(_simulation, world);
    public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(GraphSpatialState world, SimulationRules rules) => _rule.Forecast(Root(world), rules);
    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> candidate, CancellationToken token) => _rule.PlanSelectedAsync(Root(world), candidate, token);
}
