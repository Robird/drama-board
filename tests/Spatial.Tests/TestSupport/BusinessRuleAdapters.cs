using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial.Tests.TestSupport;

// Legacy business/math tests deliberately exercise one fact at a time. This test-only
// adapter supplies a fresh completed root; integration tests exercise actual maintenance.
internal sealed class BusinessContactRule(GraphDefinition definition) {
    private readonly SpatialSimulation _simulation = new(definition);
    private readonly SpatialContactOccurrenceRule _rule = new(definition);
    private SpatialSimulationState Root(GraphSpatialState world) => _simulation.Rebuild(world, new ModelTime(long.MinValue));
    public IReadOnlyList<OccurrenceCandidate<PassageContactOccurrenceData>> Forecast(GraphSpatialState world, SimulationRules rules) =>
        _rule.Forecast(Root(world), rules).Select(c => new OccurrenceCandidate<PassageContactOccurrenceData>(c.Key, c.Due, (PassageContactOccurrenceData)c.Data)).ToArray();
    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
        OccurrenceCandidate<PassageContactOccurrenceData> candidate, CancellationToken token) =>
        _rule.PlanSelectedAsync(Root(world), new(candidate.Key, candidate.Due, candidate.Data), token);
}

internal sealed class BusinessOccurrenceRule(GraphDefinition definition) {
    private readonly SpatialSimulation _simulation = new(definition);
    private readonly SpatialOccurrenceRule _rule = new(definition);
    private SpatialSimulationState Root(GraphSpatialState world) => _simulation.Rebuild(world, new ModelTime(long.MinValue));
    public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(GraphSpatialState world, SimulationRules rules) => _rule.Forecast(Root(world), rules);
    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(GraphSpatialState world,
        OccurrenceCandidate<SpatialOccurrenceData> candidate, CancellationToken token) => _rule.PlanSelectedAsync(Root(world), candidate, token);
}
