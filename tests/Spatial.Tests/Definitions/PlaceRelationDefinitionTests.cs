using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Definitions;

public sealed class PlaceRelationDefinitionTests {
    [Fact]
    public void Relation_CanonicalizesEndpointsAndPreservesAllCapabilityBits() {
        ulong flags = PlaceRelationCapabilities.Sound | (1UL << 63) | (1UL << 37);
        var relation = new PlaceRelationDefinition(GraphTestWorld.B, GraphTestWorld.A, flags);

        Assert.Equal(GraphTestWorld.A, relation.EndpointA);
        Assert.Equal(GraphTestWorld.B, relation.EndpointB);
        Assert.Equal(flags, relation.Flags);
        Assert.Equal(new PlaceRelationDefinition(GraphTestWorld.A, GraphTestWorld.B, flags), relation);
    }

    [Fact]
    public void Relation_RejectsSelfConnectionAndUninitializedEndpoints() {
        Assert.Throws<ArgumentException>(() => new PlaceRelationDefinition(GraphTestWorld.A, GraphTestWorld.A, 1));
        Assert.Throws<ArgumentException>(() => new PlaceRelationDefinition(default, GraphTestWorld.A, 1));
        Assert.Throws<ArgumentException>(() => new PlaceRelationDefinition(GraphTestWorld.A, default, 1));
    }

    [Fact]
    public void Create_RejectsUnknownEndpointsAndNullRelations() {
        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A], [], [new PlaceRelationDefinition(GraphTestWorld.A, GraphTestWorld.B, 1)]));
        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B], [], [null!]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_RejectsRepeatedEndpointPairRegardlessOfDirectionOrFlags(bool reverse) {
        var first = new PlaceRelationDefinition(GraphTestWorld.A, GraphTestWorld.B, 1);
        var duplicate = reverse
            ? new PlaceRelationDefinition(GraphTestWorld.B, GraphTestWorld.A, 2)
            : new PlaceRelationDefinition(GraphTestWorld.A, GraphTestWorld.B, 2);

        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B], [], [first, duplicate]));
    }

    [Fact]
    public void Create_CanonicalizesRelationsAndOwnsAnImmutableCopy() {
        var ab = new PlaceRelationDefinition(GraphTestWorld.B, GraphTestWorld.A, 0);
        var bc = new PlaceRelationDefinition(GraphTestWorld.C, GraphTestWorld.B, ulong.MaxValue);
        PlaceRelationDefinition[] input = [bc, ab];
        GraphDefinition first = GraphDefinition.Create([GraphTestWorld.C, GraphTestWorld.B, GraphTestWorld.A], [], input);
        GraphDefinition second = GraphDefinition.Create([GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C], [], [ab, bc]);
        input[0] = ab;

        Assert.Equal([ab, bc], first.Relations);
        Assert.Equal(first.Relations, second.Relations);
        Assert.Equal(0UL, first.Relations[0].Flags);
        Assert.Equal(ulong.MaxValue, first.Relations[1].Flags);
        Assert.Throws<NotSupportedException>(() => ((IList<PlaceRelationDefinition>)first.Relations)[0] = bc);
        Assert.Empty(GraphDefinition.Create([GraphTestWorld.A], []).Relations);
    }
}
