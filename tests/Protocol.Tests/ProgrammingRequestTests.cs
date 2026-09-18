namespace DramaBoard.Protocol.Tests;

public sealed class ProgrammingRequestTests {
    [Fact]
    public void Freeze_KnownPassages_CopyIsolatedFromSourceList() {
        List<KnownPassage> source = [CreateKnownPassage("passage.north")];
        ProgrammingRequest request = CreateRequest(knownPassages: source);

        source.Add(CreateKnownPassage("passage.south"));

        Assert.Single(request.KnownPassages);
        Assert.Equal("passage.north", request.KnownPassages[0].Handle.Value);
    }

    [Fact]
    public void Constructor_DuplicateKnownPassageHandle_Throws() {
        List<KnownPassage> passages = [CreateKnownPassage("passage.north"), CreateKnownPassage("passage.north")];

        Assert.Throws<ArgumentException>(() => CreateRequest(knownPassages: passages));
    }

    [Fact]
    public void Constructor_NullKnownPassageEntry_Throws() {
        List<KnownPassage> passages = [null!];

        Assert.Throws<ArgumentException>(() => CreateRequest(knownPassages: passages));
    }

    [Fact]
    public void Freeze_RemainingProgram_CopyIsolatedFromSourceList() {
        List<Instruction> source = [new StayInstruction()];
        ProgrammingRequest request = CreateRequest(remainingProgram: source);

        source.Add(new MoveInstruction(new PassageHandle("passage.north")));

        Assert.Single(request.RemainingProgram);
        Assert.IsType<StayInstruction>(request.RemainingProgram[0]);
    }

    [Fact]
    public void Constructor_NullRemainingProgramEntry_Throws() {
        List<Instruction> instructions = [null!];

        Assert.Throws<ArgumentException>(() => CreateRequest(remainingProgram: instructions));
    }

    [Fact]
    public void Constructor_BlankPlaceId_Throws() {
        Assert.Throws<ArgumentException>(() => CreateRequest(placeId: string.Empty));
        Assert.Throws<ArgumentException>(() => CreateRequest(placeId: "   "));
    }

    [Fact]
    public void Constructor_BlankActorId_Throws() {
        Assert.Throws<ArgumentException>(() => CreateRequest(actorId: string.Empty));
        Assert.Throws<ArgumentException>(() => CreateRequest(actorId: "   "));
    }

    [Fact]
    public void Constructor_DefaultActivationId_Throws() {
        Assert.Throws<ArgumentException>(() => new ProgrammingRequest(
            default, "actor.alice", 1_000, ProgrammingReason.MissingInstruction, null,
            "place.square", [], new SubjectiveWorkspace("work in progress"), []));
    }

    [Fact]
    public void Constructor_EmptyLists_AreAllowed() {
        ProgrammingRequest request = CreateRequest();

        Assert.Empty(request.KnownPassages);
        Assert.Empty(request.RemainingProgram);
    }

    private static ProgrammingRequest CreateRequest(
        string? actorId = null,
        string? placeId = null,
        IReadOnlyList<KnownPassage>? knownPassages = null,
        IReadOnlyList<Instruction>? remainingProgram = null) =>
        new(
            new ActivationId("activation-1"),
            actorId ?? "actor.alice",
            1_000,
            ProgrammingReason.MissingInstruction,
            null,
            placeId ?? "place.square",
            knownPassages ?? Array.Empty<KnownPassage>(),
            new SubjectiveWorkspace("work in progress"),
            remainingProgram ?? Array.Empty<Instruction>());

    private static KnownPassage CreateKnownPassage(string handle) =>
        new(new PassageHandle(handle), "place.square", "place.inn", 1_500, EnterableFromA: true, EnterableFromB: false);
}
