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

    /// <summary>Predicts every passage exactly once for a new world; never use this to reopen an existing snapshot.</summary>
    public SpatialSimulationState CreateGenesis(GraphSpatialState business, ModelTime genesisTime) {
        GraphSpatialStateValidator.ValidateComplete(_definition, business);
        var contacts = new SpatialContactPredictions(
            _definition.Passages.SelectMany(passage => PredictPassage(business, passage.Id)));
        var world = new SpatialSimulationState(business, contacts);
        Validate(world, genesisTime);
        return world;
    }

    /// <summary>All occurrence sources share the same completion seam, including additional business rules.</summary>
    public SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> CreateKernel(
        SimulationRules simulationRules,
        IOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact> history,
        IEnumerable<IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact>>? additionalRules = null) {
        IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact>[] rules = [
            new SpatialOccurrenceRule(_definition),
            new SpatialContactOccurrenceRule(_definition),
            .. additionalRules ?? [],
        ];
        return new(history, simulationRules, rules, Fold, Complete, Validate);
    }

    /// <summary>Folds recorded results only. Intermediate business states may temporarily invalidate old predictions.</summary>
    public SpatialSimulationState Fold(SpatialSimulationState world, LogicalInstant instant, SpatialSimulationFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);
        return fact switch {
            SpatialBusinessFact business => new(_reducer.Apply(world.Business, instant, business.Fact), world.Contacts),
            ContactPredictionsReplaced replaced => ApplyReplacement(world, replaced),
            _ => throw new InvalidOperationException($"Unsupported spatial simulation fact '{fact.GetType().Name}'."),
        };
    }

    /// <summary>Maintains each affected passage once, after all business decisions have been folded in order.</summary>
    public TransitionDraft<SpatialSimulationFact> Complete(
        SpatialSimulationState before,
        LogicalInstant instant,
        TransitionDraft<SpatialSimulationFact> businessDraft) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(businessDraft);
        GraphSpatialState business = before.Business;
        var dirty = new HashSet<PassageId>();
        foreach (SpatialSimulationFact fact in businessDraft.Facts) {
            if (fact is not SpatialBusinessFact wrapper) {
                throw new InvalidOperationException("Business planners must not supply prediction maintenance facts.");
            }
            GraphSpatialState after = _reducer.Apply(business, instant, wrapper.Fact);
            CollectDirty(business, after, wrapper.Fact, dirty);
            business = after;
        }

        if (dirty.Count == 0) { return businessDraft; }
        PassageId[] passages = [.. dirty.Order()];
        ContactPrediction[] outputs = [.. passages.SelectMany(passage => PredictPassage(business, passage))];
        ContactPrediction[] previous = [.. before.Contacts.Entries.Where(entry => dirty.Contains(entry.ContactKey.PassageId))];
        if (previous.SequenceEqual(outputs)) { return businessDraft; }
        return new TransitionDraft<SpatialSimulationFact>([
            .. businessDraft.Facts,
            new ContactPredictionsReplaced(passages, outputs),
        ]);
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

    private SpatialSimulationState ApplyReplacement(SpatialSimulationState world, ContactPredictionsReplaced replaced) {
        var passages = new HashSet<PassageId>();
        PassageId? previous = null;
        foreach (PassageId passage in replaced.Passages) {
            GraphSpatialStateValidator.RequirePassage(_definition, passage);
            if (previous is PassageId prior && prior.CompareTo(passage) >= 0) {
                throw new InvalidOperationException("Replacement passages must be unique and canonically ordered.");
            }
            previous = passage;
            passages.Add(passage);
        }
        if (passages.Count == 0 || replaced.Predictions.Any(entry => !passages.Contains(entry.ContactKey.PassageId))) {
            throw new InvalidOperationException("Replacement outputs must belong to nonempty declared passage scope.");
        }
        return new SpatialSimulationState(world.Business, new SpatialContactPredictions(
            world.Contacts.Entries.Where(entry => !passages.Contains(entry.ContactKey.PassageId))
                .Concat(replaced.Predictions)));
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
