using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;
using DramaBoard.Server.FreePlay.Runtime;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay;

internal abstract record FreePlayFact;
internal sealed record SpatialFact(GraphSpatialFact Fact) : FreePlayFact;
internal sealed record ProcessFact(FreePlayProcessFact Fact) : FreePlayFact;

internal sealed record FreePlayWorld(GraphSpatialState Spatial, PlayerProcessState Process, ModelTime EffectiveTime);
internal sealed record FreePlayCandidate(OccurrenceCandidate<SpatialOccurrenceData>? Spatial, FreePlayProcessOccurrenceData? Process = null);

/// <summary>Composition root of the FreePlay scene: graph definition, genesis, folding and cross-domain invariants.</summary>
internal sealed class FreePlayScene {
    internal static readonly EntityId Actor = new("human");
    internal GraphDefinition Definition { get; } = GraphDefinition.Create(
        [new("A"), new("B"), new("C"), new("D")],
        [Passage("ab", "A", "B", 1000), Passage("ac", "A", "C", 2000),
         Passage("bd", "B", "D", 3000), Passage("cd", "C", "D", 1000)]);
    internal SpatialPlanner Planner { get; }
    internal SpatialQueries Queries { get; }
    private readonly GraphSpatialReducer _reducer;

    internal FreePlayScene() {
        Planner = new(Definition);
        _reducer = new(Definition);
        Queries = new(Definition);
    }

    internal FreePlayWorld Genesis() => new(
        GraphSpatialState.Create(Definition, [new(Actor, new PlaceId("A"))]),
        PlayerProcessStateGenesis.Create(),
        ModelTime.Zero);

    internal FreePlayWorld Fold(FreePlayWorld world, LogicalInstant instant, FreePlayFact fact) => fact switch {
        SpatialFact spatial => new FreePlayWorld(
            _reducer.Apply(world.Spatial, instant, spatial.Fact), world.Process, instant.ModelTime),
        ProcessFact process => new FreePlayWorld(
            world.Spatial, PlayerProcessReducer.Apply(world.Process, instant, process.Fact), instant.ModelTime),
        _ => throw new InvalidOperationException($"The FreePlay scene does not support fact type '{fact.GetType().Name}'.")
    };

    internal void Validate(FreePlayWorld world) {
        GraphSpatialStateValidator.ValidateComplete(Definition, world.Spatial);
        PlayerProcessValidator.Validate(world.Process);
        // Cross-domain invariant: movement truth lives in the Spatial domain, so a traversing actor
        // and a running move process with the matching generation are two views of the same fact.
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == Actor);
        bool traversing = actor.Location is TraversingLocation;
        if (world.Process.Mode is RunningMoveProcess running) {
            if (!traversing || running.MovementGeneration != actor.MovementGeneration) {
                throw new InvalidOperationException(
                    "A running move process requires the actor to traverse the passage its movement generation names.");
            }
        } else if (traversing) {
            throw new InvalidOperationException(
                "A traversing actor requires a running move process carrying its movement generation.");
        }
    }

    internal string Location(FreePlayWorld world) => Queries.GetLocation(world.Spatial, Actor, world.EffectiveTime) switch {
        AtPlaceView place => place.PlaceId.Value,
        TraversingView traversal => $"{traversal.PassageId.Value} → {traversal.TargetPlaceId.Value}",
        _ => throw new InvalidOperationException("Unknown location.")
    };

    private static PassageDefinition Passage(string id, string from, string to, long length) =>
        new(new(id), new(from), new(to), length, new(true, true));
}
