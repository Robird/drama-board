using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.ExpeditionOutcome", 1)]
public enum PlaygroundExpeditionOutcome { Exploring, Escaped, Dead }

/// <summary>A character's last direct observation of one visited place; resources are historical snapshots.</summary>
[DurableType("DramaBoard.Playground.Visit", 1)]
public sealed partial class PlaygroundVisit : IDurableObject {
    [DurableField(1)] private string _actorId;
    [DurableField(2)] private string _placeId;
    [DurableField(3)] private ModelTime _at;
    [DurableField(4)] private List<PlaygroundResource> _resources;
    public string ActorId => _actorId;
    public string PlaceId => _placeId;
    public ModelTime At => _at;
    public IReadOnlyList<PlaygroundResource> Resources => _resources.AsReadOnly();
    public PlaygroundVisit(string actorId, string placeId, ModelTime at, IEnumerable<PlaygroundResource> resources) {
        _actorId = actorId; _placeId = placeId; _at = at; _resources = [.. resources];
    }
    public override string ToString() => $"{ActorId}:{PlaceId}@{At.Ticks}[{string.Join(';', Resources)}]";
}

[DurableType("DramaBoard.Playground.ConfirmedPassage", 1)]
public sealed partial record PlaygroundConfirmedPassage(
    [field: DurableField(1)] string ActorId,
    [field: DurableField(2)] string PassageId) : IDurableObject;

/// <summary>Persistent scenario identity, active spatial subgraph, observations and shared environment.</summary>
[DurableType("DramaBoard.Playground.Expedition", 3)]
public sealed partial class PlaygroundExpedition : IDurableObject {
    [DurableField(1)] private string _scenarioId;
    [DurableField(2)] private GraphSpatialState _spatial;
    [DurableField(3)] private List<PlaygroundVisit> _visits;
    [DurableField(6)] private PlaygroundEnvironment? _environment;
    [DurableField(8)] private List<PlaygroundPath> _candidatePaths;
    public IReadOnlyList<PlaygroundPath> CandidatePaths => _candidatePaths.AsReadOnly();
    public PlaygroundPath? CandidatePath(string actorId) => _candidatePaths.SingleOrDefault(p => p.ActorId == actorId);
    [DurableField(7)] private List<PlaygroundConfirmedPassage> _confirmedPassages;
    public IReadOnlyList<PlaygroundConfirmedPassage> ConfirmedPassages => _confirmedPassages.AsReadOnly();
    public bool KnowsPassage(string actorId, string passageId) => _confirmedPassages.Any(k => k.ActorId == actorId && k.PassageId == passageId);
    public string ScenarioId => _scenarioId;
    public GraphSpatialState Spatial => _spatial;
    public IReadOnlyList<PlaygroundVisit> Visits => _visits.AsReadOnly();
    public PlaygroundEnvironment? Environment => _environment;
    public PlaygroundExpedition(string scenarioId, GraphSpatialState spatial, IEnumerable<PlaygroundVisit> visits,
        PlaygroundEnvironment? environment = null, IEnumerable<PlaygroundConfirmedPassage>? confirmedPassages = null, IEnumerable<PlaygroundPath>? candidatePaths = null) {
        _candidatePaths = candidatePaths?.ToList() ?? [];
        _scenarioId = scenarioId; _spatial = spatial; _visits = [.. visits];
        _environment = environment; _confirmedPassages = confirmedPassages?.ToList() ?? [];
    }
    internal PlaygroundExpedition Change(GraphSpatialState spatial, IEnumerable<PlaygroundVisit> visits,
        PlaygroundEnvironment? environment = null, IEnumerable<PlaygroundConfirmedPassage>? confirmedPassages = null, string? candidateActorId = null, PlaygroundPath? candidatePath = null, IEnumerable<string>? retiredActors = null) {
        var replacements = visits.ToDictionary(v => (v.ActorId, v.PlaceId));
        return new(ScenarioId, spatial, Visits.Where(v => !replacements.ContainsKey((v.ActorId, v.PlaceId)))
            .Concat(replacements.Values).OrderBy(v => v.ActorId, StringComparer.Ordinal).ThenBy(v => v.PlaceId, StringComparer.Ordinal),
            environment ?? Environment, ConfirmedPassages.Concat(confirmedPassages ?? []).Distinct().OrderBy(k => k.ActorId, StringComparer.Ordinal).ThenBy(k => k.PassageId, StringComparer.Ordinal),
            CandidatePaths.Where(p => p.ActorId != candidateActorId).Concat(candidatePath is null ? [] : new[] { candidatePath })
                .Where(p => !(retiredActors ?? []).Contains(p.ActorId)).OrderBy(p => p.ActorId, StringComparer.Ordinal));
    }
    internal bool SameState(PlaygroundExpedition other) => ScenarioId == other.ScenarioId && Spatial.Equals(other.Spatial) &&
        CandidatePaths.Count == other.CandidatePaths.Count && CandidatePaths.Zip(other.CandidatePaths).All(p => PlaygroundPath.Same(p.First, p.Second)) &&
        Visits.SequenceEqual(other.Visits) && ConfirmedPassages.SequenceEqual(other.ConfirmedPassages) && ReferenceEquals(Environment, other.Environment);
    internal void Validate(PlaygroundBusinessState business) {
        if (ScenarioId != business.Definition.ContentId || business.Characters.Count < 1 ||
            Spatial.Entities.Count != business.Characters.Count(c => c.IsActive) || Spatial.ScheduledPassageEntryChanges.Count != 0 ||
            Visits.Select(v => (v.ActorId, v.PlaceId)).Distinct().Count() != Visits.Count) {
            throw new InvalidOperationException("Invalid expedition scenario, participants, observations or ending.");
        }
        if (ConfirmedPassages.Distinct().Count() != ConfirmedPassages.Count || ConfirmedPassages.Any(k => !business.Characters.Any(c => c.Id == k.ActorId) || !business.Definition.Passages.Any(p => p.Id == k.PassageId))) {
            throw new InvalidOperationException("Invalid confirmed passage knowledge.");
        }
        foreach (PlaygroundConfirmedPassage known in ConfirmedPassages) {
            PlaygroundPassageDefinition passage = business.Definition.Passages.Single(p => p.Id == known.PassageId);
            if (!Visits.Any(v => v.ActorId == known.ActorId && v.PlaceId == passage.From) ||
                !Visits.Any(v => v.ActorId == known.ActorId && v.PlaceId == passage.To)) {
                throw new InvalidOperationException("Confirmed passage endpoints require the traveller's own visits.");
            }
        }
        PlaygroundPathRules.ValidateCandidates(business);
        GraphSpatialStateValidator.ValidateComplete(business.Definition.Runtime.Graph, Spatial);
        Environment?.Validate(business.Time, business.Characters, business.Definition);
        if (Environment is not null && business.Characters.Any(c => c.Survival is null)) {
            throw new InvalidOperationException("Weather requires survival needs for each participant.");
        }
        foreach (PlaygroundCharacter actor in business.Characters) {
            if (actor.Ending is { } ending) {
                if (Spatial.TryGetEntity(new(actor.Id), out _) || ending.Outcome != PlaygroundExpeditionOutcome.Dead) {
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
            if (actor.Activity is { Kind: PlaygroundActivityKind.Gather } gathering &&
                (entity.Location is not AtPlaceLocation gatheringPlace ||
                    business.Definition.GetResourcePlace(gathering.ResourceId!) != gatheringPlace.PlaceId.Value)) {
                throw new InvalidOperationException("Gathering requires the resource's actual place.");
            }

        }
        foreach (PlaygroundVisit visit in Visits) {
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
