namespace DramaBoard.Protocol.Tests;

public sealed class ProgrammingResponseTests {
    [Fact]
    public void Constructor_EmptyProgram_IsAllowed() {
        ProgrammingResponse response = CreateResponse();

        Assert.Empty(response.Program);
    }

    [Fact]
    public void Freeze_Program_CopyIsolatedFromSourceList() {
        List<Instruction> source = [new StayInstruction()];
        ProgrammingResponse response = CreateResponse(source);

        source.Add(new ThinkInstruction("later"));

        Assert.Single(response.Program);
        Assert.IsType<StayInstruction>(response.Program[0]);
    }

    [Fact]
    public void Constructor_NullProgramEntry_Throws() {
        List<Instruction> instructions = [null!];

        Assert.Throws<ArgumentException>(() => CreateResponse(instructions));
    }

    [Fact]
    public void Constructor_DefaultActivationId_Throws() {
        Assert.Throws<ArgumentException>(() =>
            new ProgrammingResponse(default, [], new SubjectiveWorkspace("thinking")));
    }

    private static ProgrammingResponse CreateResponse(IReadOnlyList<Instruction>? program = null) =>
        new(new ActivationId("activation-1"), program ?? Array.Empty<Instruction>(), new SubjectiveWorkspace("thinking"));
}
