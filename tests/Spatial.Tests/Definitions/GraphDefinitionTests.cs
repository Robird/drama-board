using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Definitions;

public sealed class GraphDefinitionTests
{
    [Fact]
    public void Create_CanonicalizesPlacesAndPassagesWithoutCollapsingParallelPassages()
    {
        PassageDefinition bridge = GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            enterableFromB: false);
        PassageDefinition ferry = GraphTestWorld.Passage(
            GraphTestWorld.Ferry,
            GraphTestWorld.A,
            GraphTestWorld.B,
            length: 20,
            enterableFromA: false);

        GraphDefinition first = GraphDefinition.Create(
            [GraphTestWorld.B, GraphTestWorld.A],
            [ferry, bridge]);
        GraphDefinition second = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [bridge, ferry]);

        Assert.Equal([GraphTestWorld.A, GraphTestWorld.B], first.Places);
        Assert.Equal([GraphTestWorld.Bridge, GraphTestWorld.Ferry], first.Passages.Select(value => value.Id));
        Assert.Equal(first.Places, second.Places);
        Assert.Equal(first.Passages, second.Passages);
        Assert.Equal(new PassageEntryAccess(true, false), first.GetPassage(GraphTestWorld.Bridge).InitialEntryAccess);
        Assert.Equal(new PassageEntryAccess(false, true), first.GetPassage(GraphTestWorld.Ferry).InitialEntryAccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void Identifier_RejectsBlankOrOuterWhitespace(string value)
    {
        Assert.Throws<ArgumentException>(() => new PlaceId(value));
        Assert.Throws<ArgumentException>(() => new PassageId(value));
        Assert.Throws<ArgumentException>(() => new EntityId(value));
    }

    [Fact]
    public void Create_RejectsDuplicateAndUnknownDefinitionReferences()
    {
        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.A],
            []));

        PassageDefinition duplicate = GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B);
        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [duplicate, duplicate]));

        PassageDefinition unknownEndpoint = GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.C);
        Assert.Throws<ArgumentException>(() => GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [unknownEndpoint]));
    }

    [Fact]
    public void Passage_RejectsEqualEndpointsAndNonPositiveLength()
    {
        Assert.Throws<ArgumentException>(() => GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            length: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphTestWorld.Passage(
            GraphTestWorld.Bridge,
            GraphTestWorld.A,
            GraphTestWorld.B,
            length: -1));
    }

    [Fact]
    public void PassageEntryPatch_RequiresAtLeastOneSpecifiedBit()
    {
        Assert.Throws<ArgumentException>(() => new PassageEntryPatch(null, null));
        Assert.Equal(false, new PassageEntryPatch(false, null).EnterableFromA);
        Assert.Null(new PassageEntryPatch(false, null).EnterableFromB);
    }
}
