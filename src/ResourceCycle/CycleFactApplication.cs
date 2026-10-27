using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Applies recorded business replacements without planning effects or maintaining runtime forecasts.</summary>
internal static class CycleFactApplication {
    internal static CycleBusinessState Apply(CycleBusinessState business, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(business);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.At != instant.ModelTime || fact.At < business.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        ValidateFact(business, fact);
        CycleExpedition? expedition = business.Expedition;
        if (expedition is not null) {
            GraphSpatialState spatial = expedition.Spatial;
            foreach (GraphSpatialFact spatialFact in fact.SpatialFacts) {
                spatial = CycleExpeditionMap.Reducer.Apply(spatial, instant, spatialFact);
            }
            expedition = expedition.Change(spatial, fact.Visits, fact.Outcome, fact.EndingReason);
        }
        return business.Replace(fact.At, fact.Light, fact.ChangedResources, fact.Script,
            fact.ChangedCharacters, fact.LastServedActorId, expedition);
    }
    internal static bool SameFoldedBusiness(CycleBusinessState expected, CycleBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) && ReferenceEquals(expected.Script, actual.Script) &&
        expected.LastServedActorId == actual.LastServedActorId && expected.Resources.Count == actual.Resources.Count &&
        expected.Resources.Zip(actual.Resources).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.Characters.Count == actual.Characters.Count && expected.Characters.Zip(actual.Characters).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        (expected.Expedition is null ? actual.Expedition is null : actual.Expedition is not null && expected.Expedition.SameState(actual.Expedition));

    private static void ValidateFact(CycleBusinessState business, CycleFact fact) {
        if (!Enum.IsDefined(fact.Kind) ||
            fact.ChangedResources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedResources.Count ||
            fact.ChangedCharacters.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedCharacters.Count ||
            fact.ChangedResources.Any(r => !business.Resources.Any(old => old.Id == r.Id)) ||
            fact.ChangedCharacters.Any(c => !business.Characters.Any(old => old.Id == c.Id)) ||
            (fact.ResourceId is not null && !business.Resources.Any(r => r.Id == fact.ResourceId)) ||
            (fact.ActorId is not null && !business.Characters.Any(c => c.Id == fact.ActorId))) {
            throw new InvalidOperationException("Fact contains unknown or duplicate identities.");
        }
        bool actorEffect = fact.ActorId is not null && fact.ChangedCharacters.Any(c => c.Id == fact.ActorId);
        bool oneResource = fact.ChangedResources.Count == 1 && fact.ChangedResources[0].Id == fact.ResourceId;
        bool decision = fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged or CycleEventKind.MoveStarted;
        if ((decision ? fact.LastServedActorId != fact.ActorId : fact.LastServedActorId is not null) ||
            (fact.LastServedActorId is not null && !actorEffect)) {
            throw new InvalidOperationException("Service cursor does not match a recorded decision.");
        }
        bool valid = fact.Kind switch {
            CycleEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            CycleEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            CycleEventKind.GatherStarted or CycleEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            CycleEventKind.MoveStarted or CycleEventKind.MoveArrived =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
        bool movement = fact.Kind is CycleEventKind.MoveStarted or CycleEventKind.MoveArrived;
        if (movement) {
            if (business.Expedition is null || fact.SpatialFacts.Count != 1 ||
                (fact.Kind == CycleEventKind.MoveStarted
                    ? fact.SpatialFacts[0] is not TraversalStartedFact started || started.EntityId.Value != fact.ActorId
                    : fact.SpatialFacts[0] is not TraversalArrivedFact arrived || arrived.EntityId.Value != fact.ActorId)) {
                throw new InvalidOperationException("Movement requires a matching recorded spatial fact.");
            }
        } else if (fact.SpatialFacts.Count != 0) { throw new InvalidOperationException("Unexpected spatial facts."); }
        if (fact.Kind != CycleEventKind.MoveArrived && (fact.Outcome is not null || fact.EndingReason is not null) ||
            business.Expedition is null && fact.Visits.Count != 0 ||
            fact.Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != fact.Visits.Count) {
            throw new InvalidOperationException("Unexpected expedition observation or ending.");
        }
    }
}
