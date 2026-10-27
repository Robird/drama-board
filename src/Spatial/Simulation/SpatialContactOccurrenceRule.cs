using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Spatial;

/// <summary>
/// Projects maintained contact outputs and consumes selected interaction opportunities.
/// The due time is the start of the model tick containing the intersection, rounded toward negative infinity.
/// </summary>
public sealed class SpatialContactOccurrenceRule :
    IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> {
    private readonly GraphDefinition _definition;

    public SpatialContactOccurrenceRule(GraphDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
    }

    public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(
        SpatialSimulationState world,
        SimulationRules rules) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        return Array.AsReadOnly(world.Contacts.Entries.Select(prediction =>
            new OccurrenceCandidate<SpatialOccurrenceData>(
                CreateCandidateKey(prediction.ContactKey),
                new CandidateDue(prediction.Due),
                new PassageContactOccurrenceData(prediction.ContactKey, prediction.Kind))).ToArray());
    }

    public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(
        SpatialSimulationState snapshot,
        OccurrenceCandidate<SpatialOccurrenceData> winner,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(snapshot);
        GraphSpatialState world = snapshot.Business;
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        GraphSpatialStateValidator.ValidateComplete(_definition, world);

        PassageContactOccurrenceData data = winner.Data as PassageContactOccurrenceData
            ?? throw new InvalidOperationException("Passage contact occurrence data is required.");
        ArgumentNullException.ThrowIfNull(data.ContactKey);
        if (!snapshot.Contacts.Entries.Any(prediction => prediction.ContactKey == data.ContactKey &&
                prediction.Due == winner.Due.ModelTime && prediction.Kind == data.Kind) ||
            world.ConsumedContacts.Contains(data.ContactKey) ||
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

        return ValueTask.FromResult(new TransitionDraft<SpatialSimulationFact>(
            [new SpatialBusinessFact(new PassageContactOccurredFact(data.ContactKey, data.Kind))]));
    }

    internal static CandidateKey CreateCandidateKey(PassageContactKey key) {
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
