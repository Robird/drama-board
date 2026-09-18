namespace DramaBoard.Protocol.Tests;

public sealed class KnownPassageTests {
    [Fact]
    public void Constructor_NonPositiveExpectedDuration_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateKnownPassage(durationMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateKnownPassage(durationMs: -1));
    }

    [Fact]
    public void Constructor_BlankEndpointA_Throws() {
        Assert.Throws<ArgumentException>(() => CreateKnownPassage(endpointA: "   "));
    }

    [Fact]
    public void Constructor_BlankEndpointB_Throws() {
        Assert.Throws<ArgumentException>(() => CreateKnownPassage(endpointB: string.Empty));
    }

    [Fact]
    public void Constructor_ValidPassage_StoresFieldsAsData() {
        KnownPassage passage = CreateKnownPassage();

        Assert.Equal("passage.north", passage.Handle.Value);
        Assert.Equal("place.square", passage.EndpointA);
        Assert.Equal("place.inn", passage.EndpointB);
        Assert.Equal(1_500, passage.ExpectedDurationMs);
        Assert.True(passage.EnterableFromA);
        Assert.False(passage.EnterableFromB);
    }

    private static KnownPassage CreateKnownPassage(
        string handle = "passage.north",
        string endpointA = "place.square",
        string endpointB = "place.inn",
        long durationMs = 1_500) =>
        new(new PassageHandle(handle), endpointA, endpointB, durationMs, EnterableFromA: true, EnterableFromB: false);
}
