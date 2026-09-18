using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial;

/// <summary>
/// Forecasts and consumes interaction opportunities for exact pairwise intersections of active passage segments.
/// The due time is the start of the model tick containing the intersection, rounded toward negative infinity.
/// </summary>
public sealed class SpatialContactOccurrenceRule :
    IOccurrenceRule<GraphSpatialState, PassageContactOccurrenceData, GraphSpatialFact> {
    private readonly GraphDefinition _definition;

    public SpatialContactOccurrenceRule(GraphDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public IReadOnlyList<OccurrenceCandidate<PassageContactOccurrenceData>> Forecast(
        GraphSpatialState world,
        SimulationRules rules) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        GraphSpatialStateValidator.ValidateComplete(_definition, world);

        var candidates = new List<OccurrenceCandidate<PassageContactOccurrenceData>>();
        IEnumerable<IGrouping<PassageId, SpatialEntity>> passageGroups = world.Entities
            .Where(entity => entity.Location is TraversingLocation)
            .GroupBy(entity => ((TraversingLocation)entity.Location).PassageId)
            .OrderBy(group => group.Key);
        foreach (IGrouping<PassageId, SpatialEntity> passageGroup in passageGroups) {
            SpatialEntity[] traversing = [.. passageGroup];
            for (int leftIndex = 0; leftIndex < traversing.Length; leftIndex++) {
                SpatialEntity left = traversing[leftIndex];
                for (int rightIndex = leftIndex + 1; rightIndex < traversing.Length; rightIndex++) {
                    SpatialEntity right = traversing[rightIndex];
                    var contactKey = new PassageContactKey(
                        passageGroup.Key,
                        left.Id,
                        left.MovementGeneration,
                        right.Id,
                        right.MovementGeneration);
                    if (world.ConsumedContacts.Contains(contactKey) ||
                        !PassageContactCalculator.TryCalculate(
                            _definition,
                            world,
                            contactKey,
                            out PassageContactCalculation calculation)) {
                        continue;
                    }

                    var data = new PassageContactOccurrenceData(contactKey, calculation.Kind);
                    candidates.Add(new OccurrenceCandidate<PassageContactOccurrenceData>(
                        CreateCandidateKey(contactKey),
                        new CandidateDue(calculation.Due),
                        data));
                }
            }
        }

        OccurrenceCandidate<PassageContactOccurrenceData>[] canonical =
        [
            .. candidates.OrderBy(candidate => candidate.Data.ContactKey),
        ];
        return Array.AsReadOnly(canonical);
    }

    public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(
        GraphSpatialState world,
        OccurrenceCandidate<PassageContactOccurrenceData> winner,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        GraphSpatialStateValidator.ValidateComplete(_definition, world);

        PassageContactOccurrenceData data = winner.Data
            ?? throw new InvalidOperationException("Passage contact occurrence data is required.");
        ArgumentNullException.ThrowIfNull(data.ContactKey);
        if (world.ConsumedContacts.Contains(data.ContactKey) ||
            !PassageContactCalculator.TryCalculate(
                _definition,
                world,
                data.ContactKey,
                out PassageContactCalculation calculation) ||
            winner.Key != CreateCandidateKey(data.ContactKey) ||
            winner.Due.ModelTime != calculation.Due ||
            data.Kind != calculation.Kind) {
            throw new InvalidOperationException(
                "The selected passage contact does not match current segment truth.");
        }

        return ValueTask.FromResult(new TransitionDraft<GraphSpatialFact>(
            [new PassageContactOccurredFact(data.ContactKey, data.Kind)]));
    }

    private static CandidateKey CreateCandidateKey(PassageContactKey key) {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue("graph-spatial/contact");
        writer.WriteStringValue(key.PassageId.Value);
        writer.WriteStringValue(key.EntityA.Value);
        writer.WriteNumberValue(key.MovementGenerationA);
        writer.WriteStringValue(key.EntityB.Value);
        writer.WriteNumberValue(key.MovementGenerationB);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }
}
