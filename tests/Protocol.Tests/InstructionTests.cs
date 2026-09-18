namespace DramaBoard.Protocol.Tests;

public sealed class InstructionTests {
    [Fact]
    public void Constructor_MoveInstructionWithBlankPassage_Throws() {
        Assert.Throws<ArgumentException>(() => new MoveInstruction(new PassageHandle(" ")));
    }

    [Fact]
    public void Constructor_MoveInstructionWithOverlongPassage_Throws() {
        Assert.Throws<ArgumentException>(() => new MoveInstruction(new PassageHandle(new string('x', 257))));
    }

    [Fact]
    public void Constructor_MoveInstructionWithControlCharacterInPassage_Throws() {
        Assert.Throws<ArgumentException>(() => new MoveInstruction(new PassageHandle("passage.\nnorth")));
    }

    [Fact]
    public void Constructor_ThinkInstructionWithNullNote_Throws() {
        Assert.Throws<ArgumentException>(() => new ThinkInstruction(null!));
    }

    [Fact]
    public void Constructor_ThinkInstructionWithBlankNote_Throws() {
        Assert.Throws<ArgumentException>(() => new ThinkInstruction("   "));
    }

    [Fact]
    public void Constructor_ThinkInstructionWithOverlongNote_Throws() {
        Assert.Throws<ArgumentException>(() => new ThinkInstruction(new string('x', 4_097)));
    }

    [Fact]
    public void Constructor_ThinkInstructionWithNoteAtLimit_IsAllowed() {
        Assert.Equal(4_096, new ThinkInstruction(new string('x', 4_096)).Note.Length);
    }

    [Fact]
    public void Equality_SameMoveInstructions_HasValueSemantics() {
        MoveInstruction first = new(new PassageHandle("passage.north"));
        MoveInstruction second = new(new PassageHandle("passage.north"));

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new MoveInstruction(new PassageHandle("passage.south")));
    }

    [Fact]
    public void Equality_SameThinkInstructions_HasValueSemantics() {
        Assert.Equal(new ThinkInstruction("note"), new ThinkInstruction("note"));
        Assert.NotEqual(new ThinkInstruction("note"), new ThinkInstruction("other note"));
    }

    [Fact]
    public void Equality_SameStayInstructions_HasValueSemantics() {
        Assert.Equal(new StayInstruction(), new StayInstruction());
        Assert.NotEqual<Instruction>(new StayInstruction(), new MoveInstruction(new PassageHandle("passage.north")));
    }
}
