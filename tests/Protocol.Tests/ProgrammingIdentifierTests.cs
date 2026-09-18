namespace DramaBoard.Protocol.Tests;

public sealed class ProgrammingIdentifierTests {
    [Fact]
    public void Equality_SamePassageHandle_HasValueSemantics() {
        PassageHandle first = new("passage.north");
        PassageHandle second = new("passage.north");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new PassageHandle("passage.south"));
        Assert.Equal("passage.north", first.ToString());
    }

    [Fact]
    public void Equality_SameActivationId_HasValueSemantics() {
        ActivationId first = new("activation-42");
        ActivationId second = new("activation-42");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new ActivationId("activation-43"));
        Assert.Equal("activation-42", first.ToString());
    }

    [Fact]
    public void Constructor_BlankIdentifier_Throws() {
        Assert.Throws<ArgumentException>(() => new PassageHandle(string.Empty));
        Assert.Throws<ArgumentException>(() => new PassageHandle("   "));
        Assert.Throws<ArgumentException>(() => new ActivationId(string.Empty));
        Assert.Throws<ArgumentException>(() => new ActivationId("   "));
    }

    [Fact]
    public void Constructor_IdentifierLongerThanProtocolLimit_Throws() {
        Assert.Throws<ArgumentException>(() => new PassageHandle(new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new ActivationId(new string('x', 257)));
    }

    [Fact]
    public void Constructor_IdentifierAtProtocolLimit_IsAllowed() {
        PassageHandle handle = new(new string('x', 256));
        ActivationId activation = new(new string('a', 256));

        Assert.Equal(256, handle.Value.Length);
        Assert.Equal(256, activation.Value.Length);
    }

    [Fact]
    public void Constructor_IdentifierContainingControlCharacter_Throws() {
        Assert.Throws<ArgumentException>(() => new PassageHandle("passage.\nnorth"));
        Assert.Throws<ArgumentException>(() => new ActivationId("activation.\tnorth"));
    }
}
