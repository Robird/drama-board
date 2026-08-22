using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Spatial;

namespace DramaBoard.Player.Agency.Tests.Spatial;

public sealed class SpatialKnowledgeTests
{
    private static readonly PlaceId A = new("a");
    private static readonly PlaceId B = new("b");
    private static readonly PlaceId C = new("c");
    private static readonly PlaceId Unknown = new("unknown");
    private static readonly PassageId Road = new("road");
    private static readonly PassageId Ferry = new("ferry");
    private static readonly PassageId UnknownPassage = new("unknown-passage");

    [Fact]
    public void FullMap_ReturnsOriginalImmutableDefinitionForEveryWorldAndSubject()
    {
        GraphDefinition objective = ObjectiveGraph();
        FullMapPlayerSpatialKnowledgeGetter<TestWorld> getter =
            FullMapPlayerSpatialKnowledgeGetter<TestWorld>.Instance;

        PlayerSpatialKnowledgeSnapshot first = getter.GetKnownGraph(
            new TestWorld("first"),
            "alice",
            objective);
        PlayerSpatialKnowledgeSnapshot second = getter.GetKnownGraph(
            new TestWorld("different-runtime-state"),
            "bob",
            objective);

        Assert.Same(objective, first.KnownGraph);
        Assert.Same(objective, second.KnownGraph);
        Assert.NotSame(first, second);
        Assert.Same(getter, FullMapPlayerSpatialKnowledgeGetter<TestWorld>.Instance);
    }

    [Fact]
    public void FullMapAndGetter_RejectNullOrInvalidArguments()
    {
        GraphDefinition objective = ObjectiveGraph();
        FullMapPlayerSpatialKnowledgeGetter<TestWorld> getter =
            FullMapPlayerSpatialKnowledgeGetter<TestWorld>.Instance;

        Assert.Throws<ArgumentNullException>(() =>
            PlayerSpatialKnowledgeSnapshot.FullMap(null!));
        Assert.Throws<ArgumentNullException>(() =>
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(null!, objective));
        Assert.Throws<ArgumentNullException>(() =>
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, null!));
        Assert.Throws<ArgumentNullException>(() =>
            getter.GetKnownGraph(null!, "alice", objective));
        Assert.Throws<ArgumentNullException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), "alice", null!));
        Assert.Throws<ArgumentNullException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), null!, objective));
        Assert.Throws<ArgumentException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), "", objective));
        Assert.Throws<ArgumentException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), " ", objective));
        Assert.Throws<ArgumentException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), " alice", objective));
        Assert.Throws<ArgumentException>(() =>
            getter.GetKnownGraph(new TestWorld("world"), "alice ", objective));
    }

    [Fact]
    public void CreateExactSubgraph_PreservesCanonicalParallelPassagesAndDirections()
    {
        GraphDefinition objective = ObjectiveGraph();
        GraphDefinition exactSubgraph = GraphDefinition.Create(
            [B, A],
            [objective.GetPassage(Ferry), objective.GetPassage(Road)]);

        PlayerSpatialKnowledgeSnapshot snapshot =
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, exactSubgraph);

        Assert.Same(exactSubgraph, snapshot.KnownGraph);
        Assert.Equal([A, B], snapshot.KnownGraph.Places);
        Assert.Equal(
            [Ferry, Road],
            snapshot.KnownGraph.Passages.Select(passage => passage.Id));
        Assert.Equal(
            new PassageEntryAccess(true, false),
            snapshot.KnownGraph.GetPassage(Road).InitialEntryAccess);
        Assert.Equal(
            new PassageEntryAccess(false, true),
            snapshot.KnownGraph.GetPassage(Ferry).InitialEntryAccess);
    }

    [Fact]
    public void CreateExactSubgraph_AllowsContentRemovalAndIsolatedKnownPlaces()
    {
        GraphDefinition objective = ObjectiveGraph();
        GraphDefinition exactSubgraph = GraphDefinition.Create([C], []);

        PlayerSpatialKnowledgeSnapshot snapshot =
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, exactSubgraph);

        Assert.Equal([C], snapshot.KnownGraph.Places);
        Assert.Empty(snapshot.KnownGraph.Passages);
    }

    [Fact]
    public void CreateExactSubgraph_RejectsUnknownPlaceOrPassage()
    {
        GraphDefinition objective = ObjectiveGraph();
        GraphDefinition unknownPlace = GraphDefinition.Create([Unknown], []);
        GraphDefinition unknownPassage = GraphDefinition.Create(
            [A, B],
            [Passage(UnknownPassage, A, B, 10, true, true)]);

        Assert.Throws<ArgumentException>(() =>
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, unknownPlace));
        Assert.Throws<ArgumentException>(() =>
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, unknownPassage));
    }

    [Fact]
    public void CreateExactSubgraph_RejectsEveryModifiedPassageField()
    {
        GraphDefinition objective = ObjectiveGraph();

        AssertModifiedPassageRejected(objective, Passage(Road, B, A, 10, true, false));
        AssertModifiedPassageRejected(objective, Passage(Road, A, C, 10, true, false));
        AssertModifiedPassageRejected(objective, Passage(Road, A, B, 11, true, false));
        AssertModifiedPassageRejected(objective, Passage(Road, A, B, 10, false, false));
        AssertModifiedPassageRejected(objective, Passage(Road, A, B, 10, true, true));
    }

    [Fact]
    public void GraphDefinition_RejectsPassageWhoseEndpointIsMissingFromSubgraph()
    {
        PassageDefinition road = Passage(Road, A, B, 10, true, false);

        Assert.Throws<ArgumentException>(() => GraphDefinition.Create([A], [road]));
    }

    private static void AssertModifiedPassageRejected(
        GraphDefinition objective,
        PassageDefinition modified)
    {
        GraphDefinition exactSubgraph = GraphDefinition.Create(
            [A, B, C],
            [modified]);

        Assert.Throws<ArgumentException>(() =>
            PlayerSpatialKnowledgeSnapshot.CreateExactSubgraph(objective, exactSubgraph));
    }

    private static GraphDefinition ObjectiveGraph() => GraphDefinition.Create(
        [C, B, A],
        [
            Passage(Road, A, B, 10, true, false),
            Passage(Ferry, A, B, 20, false, true),
            Passage(new PassageId("trail"), B, C, 30, true, true),
        ]);

    private static PassageDefinition Passage(
        PassageId id,
        PlaceId endpointA,
        PlaceId endpointB,
        long length,
        bool enterableFromA,
        bool enterableFromB) =>
        new(
            id,
            endpointA,
            endpointB,
            length,
            new PassageEntryAccess(enterableFromA, enterableFromB));

    private sealed record TestWorld(string State);
}
