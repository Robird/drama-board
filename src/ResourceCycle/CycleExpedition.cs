using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

[DurableType("DramaBoard.ResourceCycle.ExpeditionOutcome", 1)]
public enum CycleExpeditionOutcome { Exploring, Escaped, Dead }

/// <summary>A character's last direct observation of one visited place; resources are historical snapshots.</summary>
[DurableType("DramaBoard.ResourceCycle.Visit", 1)]
public sealed partial class CycleVisit : IDurableObject {
    [DurableField(1)] private string _actorId;
    [DurableField(2)] private string _placeId;
    [DurableField(3)] private ModelTime _at;
    [DurableField(4)] private List<CycleResource> _resources;
    public string ActorId => _actorId;
    public string PlaceId => _placeId;
    public ModelTime At => _at;
    public IReadOnlyList<CycleResource> Resources => _resources.AsReadOnly();
    public CycleVisit(string actorId, string placeId, ModelTime at, IEnumerable<CycleResource> resources) {
        _actorId = actorId; _placeId = placeId; _at = at; _resources = [.. resources];
    }
    public override string ToString() => $"{ActorId}:{PlaceId}@{At.Ticks}[{string.Join(';', Resources)}]";
}

/// <summary>Persistent scenario identity, active spatial subgraph, observations and shared environment.</summary>
[DurableType("DramaBoard.ResourceCycle.Expedition", 3)]
public sealed partial class CycleExpedition : IDurableObject {
    [DurableField(1)] private string _scenarioId;
    [DurableField(2)] private GraphSpatialState _spatial;
    [DurableField(3)] private List<CycleVisit> _visits;
    [DurableField(6)] private CycleEnvironment? _environment;
    public string ScenarioId => _scenarioId;
    public GraphSpatialState Spatial => _spatial;
    public IReadOnlyList<CycleVisit> Visits => _visits.AsReadOnly();
    public CycleEnvironment? Environment => _environment;
    public CycleExpedition(string scenarioId, GraphSpatialState spatial, IEnumerable<CycleVisit> visits,
        CycleEnvironment? environment = null) {
        _scenarioId = scenarioId; _spatial = spatial; _visits = [.. visits];
        _environment = environment;
    }
    internal CycleExpedition Change(GraphSpatialState spatial, IEnumerable<CycleVisit> visits,
        CycleEnvironment? environment = null) {
        var replacements = visits.ToDictionary(v => (v.ActorId, v.PlaceId));
        return new(ScenarioId, spatial, Visits.Where(v => !replacements.ContainsKey((v.ActorId, v.PlaceId)))
            .Concat(replacements.Values).OrderBy(v => v.ActorId, StringComparer.Ordinal).ThenBy(v => v.PlaceId, StringComparer.Ordinal),
            environment ?? Environment);
    }
    internal bool SameState(CycleExpedition other) => ScenarioId == other.ScenarioId && Spatial.Equals(other.Spatial) &&
        Visits.SequenceEqual(other.Visits) && ReferenceEquals(Environment, other.Environment);
    internal void Validate(CycleBusinessState business) {
        if (ScenarioId != business.Definition.ContentId || business.Characters.Count < 1 ||
            Spatial.Entities.Count != business.Characters.Count(c => c.IsActive) || Spatial.ScheduledPassageEntryChanges.Count != 0 ||
            Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != Visits.Count) {
            throw new InvalidOperationException("Invalid expedition scenario, participants, observations or ending.");
        }
        GraphSpatialStateValidator.ValidateComplete(business.Definition.Runtime.Graph, Spatial);
        Environment?.Validate(business.Time, business.Characters, business.Definition);
        if (Environment is not null && business.Characters.Any(c => c.Survival is null)) {
            throw new InvalidOperationException("Weather requires survival needs for each participant.");
        }
        foreach (CycleCharacter actor in business.Characters) {
            if (actor.Ending is { } ending) {
                if (Spatial.TryGetEntity(new(actor.Id), out _) || ending.Outcome != CycleExpeditionOutcome.Dead) {
                    throw new InvalidOperationException("Only deceased residents may be retired from the living world.");
                }
                if (ending.Location is AtPlaceLocation endedPlace) { _ = business.Definition.GetPlace(endedPlace.PlaceId.Value); }
                else if (ending.Location is TraversingLocation movement) {
                    _ = business.Definition.Runtime.Graph.GetPassage(movement.PassageId);
                    if (movement.AnchorTime > ending.At || movement.ArrivalDue < ending.At) {
                        throw new InvalidOperationException("An ended traversal is a historical snapshot at the ending instant.");
                    }
                } else { throw new InvalidOperationException("Invalid ending location."); }
                continue;
            }
            if (!Spatial.TryGetEntity(new(actor.Id), out SpatialEntity? entity) ||
                entity!.Location is TraversingLocation && actor.Activity is not null) {
                throw new InvalidOperationException("A character must have one spatial location and at most one timed activity.");
            }
            if (actor.Activity is { Kind: CycleActivityKind.Gather } gathering &&
                (entity.Location is not AtPlaceLocation gatheringPlace ||
                    business.Definition.GetResourcePlace(gathering.ResourceId!) != gatheringPlace.PlaceId.Value)) {
                throw new InvalidOperationException("Gathering requires the resource's actual place.");
            }

        }
        foreach (CycleVisit visit in Visits) {
            if (!business.Characters.Any(c => c.Id == visit.ActorId) || !business.Definition.Runtime.Graph.Contains(new PlaceId(visit.PlaceId)) ||
                visit.At < new ModelTime(0) || visit.At > business.Time || visit.Resources.Any(r =>
                    !business.Resources.Any(current => current.Id == r.Id) || business.Definition.GetResourcePlace(r.Id) != visit.PlaceId)) {
                throw new InvalidOperationException("Invalid remembered observation.");
            }
        }
        if (business.Resources.Any(r => business.Definition.GetResourcePlace(r.Id) is null)) {
            throw new InvalidOperationException("Every expedition resource must belong to a defined place.");
        }
    }
    public override string ToString() => $"{ScenarioId};positions=[{string.Join(';', Spatial.Entities.Select(e => $"{e.Id}:{e.MovementGeneration}:{DescribeLocation(e.Location)}"))}];visits=[{string.Join(';', Visits)}]" +
        (Environment is null ? "" : $";environment={Environment}");
    private static string DescribeLocation(SpatialLocation location) => location switch {
        AtPlaceLocation at => at.PlaceId.Value,
        TraversingLocation moving => $"{moving.PassageId}:{moving.AnchorTime.Ticks}:{moving.ArrivalDue.Ticks}:{moving.TargetPlaceId}",
        _ => throw new InvalidOperationException("Unknown spatial location.")
    };
}
