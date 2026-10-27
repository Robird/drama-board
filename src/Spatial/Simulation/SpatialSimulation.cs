using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Composes spatial business planning, local prediction maintenance and publication boundaries.</summary>
public sealed class SpatialSimulation {
    private readonly GraphDefinition _definition;
    private readonly GraphSpatialReducer _reducer;
    private readonly Action<PassageId>? _onRecompute;

    public SpatialSimulation(GraphDefinition definition) : this(definition, null) { }

    internal SpatialSimulation(GraphDefinition definition, Action<PassageId>? onRecompute) {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _reducer = new GraphSpatialReducer(definition);
        _onRecompute = onRecompute;
    }

    /// <summary>Rebuilds derived contacts for genesis or a completed durable business snapshot.</summary>
    public SpatialSimulationState Rebuild(GraphSpatialState business, ModelTime boundaryTime) {
        GraphSpatialStateValidator.ValidateComplete(_definition, business);
        var contacts = new SpatialContactPredictions(
            _definition.Passages.SelectMany(passage => PredictPassage(business, passage.Id)));
        var world = new SpatialSimulationState(business, contacts);
        Validate(world, boundaryTime);
        return world;
    }

    /// <summary>All occurrence sources share the same completion seam, including additional business rules.</summary>
    public SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact> CreateKernel(
        SimulationRules simulationRules,
        IOccurrenceHistory<SpatialSimulationState, GraphSpatialFact> history,
        IEnumerable<IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>>? additionalRules = null) {
        IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>[] rules = [
            new SpatialOccurrenceRule(_definition),
            new SpatialContactOccurrenceRule(_definition),
            .. additionalRules ?? [],
        ];
        return new(history, simulationRules, rules, Fold, Complete, Validate);
    }

    /// <summary>Folds recorded results only. Intermediate business states may temporarily invalidate old predictions.</summary>
    public SpatialSimulationState Fold(SpatialSimulationState world, LogicalInstant instant, GraphSpatialFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);
        return new SpatialSimulationState(_reducer.Apply(world.Business, instant, fact), world.Contacts);
    }

    /// <summary>Purely derives each affected passage once after business Fold, preserving the supplied final Business.</summary>
    public SpatialSimulationState Complete(
        SpatialSimulationState before,
        LogicalInstant instant,
        IReadOnlyList<GraphSpatialFact> facts,
        SpatialSimulationState folded) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(folded);
        GraphSpatialState business = before.Business;
        var dirty = new HashSet<PassageId>();
        foreach (GraphSpatialFact fact in facts) {
            GraphSpatialState after = _reducer.Apply(business, instant, fact);
            CollectDirty(business, after, fact, dirty);
            business = after;
        }
        if (!business.Equals(folded.Business)) {
            throw new InvalidOperationException("The folded spatial business does not match the recorded fact sequence.");
        }

        SpatialContactPredictions contacts = before.Contacts;
        if (dirty.Count > 0) {
            ContactPrediction[] outputs = [.. dirty.Order().SelectMany(passage => PredictPassage(folded.Business, passage))];
            ContactPrediction[] previous = [.. contacts.Entries.Where(entry => dirty.Contains(entry.ContactKey.PassageId))];
            if (!previous.SequenceEqual(outputs)) {
                contacts = new SpatialContactPredictions(
                    contacts.Entries.Where(entry => !dirty.Contains(entry.ContactKey.PassageId)).Concat(outputs));
            }
        }
        return ReferenceEquals(contacts, folded.Contacts)
            ? folded
            : new SpatialSimulationState(folded.Business, contacts);
    }

    /// <summary>Checks existing outputs structurally, not completeness. Independent full-discovery tests prove coverage.</summary>
    public void Validate(SpatialSimulationState world, ModelTime at) {
        ArgumentNullException.ThrowIfNull(world);
        GraphSpatialStateValidator.ValidateComplete(_definition, world.Business);
        PassageContactKey? previous = null;
        foreach (ContactPrediction prediction in world.Contacts.Entries) {
            PassageContactKey key = prediction.ContactKey;
            if (previous is not null && previous.CompareTo(key) >= 0) {
                throw new InvalidOperationException("Contact predictions must be unique and canonically ordered.");
            }
            previous = key;
            GraphSpatialStateValidator.RequirePassage(_definition, key.PassageId);
            if (key.EntityA.CompareTo(key.EntityB) >= 0 || !Enum.IsDefined(prediction.Kind) ||
                world.Business.ConsumedContacts.Contains(key)) {
                throw new InvalidOperationException("Contact prediction has an invalid or consumed identity.");
            }
            RequireSegment(world.Business, key.PassageId, key.EntityA, key.MovementGenerationA);
            RequireSegment(world.Business, key.PassageId, key.EntityB, key.MovementGenerationB);
            RequireNotPast(prediction.Due, at);
        }
        foreach (SpatialEntity entity in world.Business.Entities) {
            if (entity.Location is TraversingLocation traversal) { RequireNotPast(traversal.ArrivalDue, at); }
        }
        foreach (ScheduledPassageEntryChange schedule in world.Business.ScheduledPassageEntryChanges) {
            RequireNotPast(schedule.Due, at);
        }
    }

    private IReadOnlyList<ContactPrediction> PredictPassage(GraphSpatialState business, PassageId passage) {
        _onRecompute?.Invoke(passage);
        SpatialEntity[] entities = [.. business.Entities.Where(entity =>
            entity.Location is TraversingLocation traversal && traversal.PassageId == passage)];
        var outputs = new List<ContactPrediction>();
        for (int left = 0; left < entities.Length; left++) {
            for (int right = left + 1; right < entities.Length; right++) {
                SpatialEntity a = entities[left];
                SpatialEntity b = entities[right];
                var key = new PassageContactKey(passage, a.Id, a.MovementGeneration, b.Id, b.MovementGeneration);
                if (!business.ConsumedContacts.Contains(key) &&
                    PassageContactCalculator.TryCalculate(_definition, business, key, out PassageContactCalculation calculation)) {
                    outputs.Add(new ContactPrediction(key, calculation.Due, calculation.Kind));
                }
            }
        }
        return outputs.OrderBy(output => output.ContactKey).ToArray();
    }

    private static void CollectDirty(GraphSpatialState before, GraphSpatialState after,
        GraphSpatialFact fact, HashSet<PassageId> dirty) {
        switch (fact) {
            case TraversalStartedFact started:
                AddSegment(after, started.EntityId, dirty);
                break;
            case TraversalReversedFact reversed:
                AddSegment(before, reversed.EntityId, dirty);
                AddSegment(after, reversed.EntityId, dirty);
                break;
            case TraversalArrivedFact arrived:
                AddSegment(before, arrived.EntityId, dirty);
                break;
            case EntityRemovedFact removed:
                AddSegment(before, removed.EntityId, dirty);
                break;
            case PassageContactOccurredFact contact:
                dirty.Add(contact.ContactKey.PassageId);
                break;
            case EntityPlacedFact:
            case PassageEntryAccessChangedFact:
            case PassageEntryChangeScheduledFact:
            case ScheduledPassageEntryChangeAppliedFact:
                break;
            default:
                throw new InvalidOperationException($"Spatial fact '{fact.GetType().Name}' has no contact-impact classification.");
        }
    }

    private static void AddSegment(GraphSpatialState business, EntityId id, HashSet<PassageId> dirty) {
        if (business.TryGetEntity(id, out SpatialEntity? entity) && entity!.Location is TraversingLocation traversal) {
            dirty.Add(traversal.PassageId);
        }
    }

    private static void RequireSegment(GraphSpatialState business, PassageId passage, EntityId id, long generation) {
        SpatialEntity entity = GraphSpatialStateValidator.RequireEntity(business, id);
        if (entity.MovementGeneration != generation || entity.Location is not TraversingLocation traversal ||
            traversal.PassageId != passage) {
            throw new InvalidOperationException("Contact prediction references a non-current movement segment.");
        }
    }

    private static void RequireNotPast(ModelTime due, ModelTime at) {
        if (due < at) { throw new InvalidOperationException("A spatial prediction cannot precede the completed boundary."); }
    }
}
