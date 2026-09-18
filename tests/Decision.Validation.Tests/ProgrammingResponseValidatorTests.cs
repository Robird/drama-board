using DramaBoard.Protocol;

namespace DramaBoard.Decision.Validation.Tests;

public sealed class ProgrammingResponseValidatorTests {
    [Fact]
    public void Validate_MismatchedActivationId_IsRejected() {
        ProgrammingRequest request = Request();

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(new ActivationId("activation-2")),
            request);

        Assert.False(result.IsValid);
        Assert.Equal(ProgrammingResponseValidationError.ActivationIdMismatch, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Validate_MoveToUnknownPassageHandle_IsRejected() {
        ProgrammingRequest request = Request(
            knownPassages:
            [
                KnownPassageA(enterableFromA: true, enterableFromB: true),
            ]);

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [new MoveInstruction(new PassageHandle("passage.old-harbor"))]),
            request);

        Assert.False(result.IsValid);
        Assert.Equal(ProgrammingResponseValidationError.UnknownPassageHandle, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Validate_MoveToKnownPassageHandle_IsValid() {
        ProgrammingRequest request = Request(
            knownPassages:
            [
                KnownPassageA(enterableFromA: true, enterableFromB: true),
            ]);

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [new MoveInstruction(new PassageHandle("passage.a"))]),
            request);

        Assert.True(result.IsValid, result.Message);
        Assert.Equal(ProgrammingResponseValidationError.None, result.Error);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Validate_MoveToKnownButUnenterablePassage_IsValid() {
        // Known references are legal at this layer; whether the move can execute now is a
        // formal in-world result checked later, not part of response validation.
        ProgrammingRequest request = Request(
            knownPassages:
            [
                KnownPassageA(enterableFromA: false, enterableFromB: false),
            ]);

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [new MoveInstruction(new PassageHandle("passage.a"))]),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_EmptyProgramWithValidWorkspace_IsValid() {
        ProgrammingRequest request = Request(workspaceText: "workspace");

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [], workspaceText: "workspace"),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_EmptyProgramWithTooLongWorkspace_IsRejected() {
        // An illegal subjective revision makes the whole response invalid even with an empty
        // program: it is not normalized into a legal [Stay] response (dynamic-programmer.md 5.3).
        ProgrammingRequest request = Request(workspaceText: "0123456789");

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [], workspaceText: new string('x', 65_537)),
            request);

        Assert.False(result.IsValid);
        Assert.Equal(ProgrammingResponseValidationError.WorkspaceTooLong, result.Error);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Validate_WorkspaceGrowthBeyondFixedLimit_IsRejected() {
        ProgrammingRequest request = Request(workspaceText: "0123456789");

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(program: [new StayInstruction()], workspaceText: new string('x', 65_537)),
            request);

        Assert.False(result.IsValid);
        Assert.Equal(ProgrammingResponseValidationError.WorkspaceTooLong, result.Error);
    }

    [Fact]
    public void Validate_WorkspaceAtFixedLimit_IsValid() {
        ProgrammingRequest request = Request(workspaceText: "0123456789");

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(workspaceText: new string('x', 65_536)),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_WorkspaceUnchangedAboveFixedLimit_IsValid() {
        // The limit only constrains programmer-initiated growth: resubmitting an already
        // oversized workspace unchanged (or shortened) remains legal.
        ProgrammingRequest request = Request(workspaceText: new string('x', 70_000));

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(workspaceText: new string('x', 70_000)),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_WorkspaceShortenedAboveFixedLimit_IsValid() {
        ProgrammingRequest request = Request(workspaceText: new string('x', 70_000));

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(workspaceText: new string('x', 68_000)),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void Validate_WorkspaceGrowthAboveCurrentLength_IsRejected() {
        ProgrammingRequest request = Request(workspaceText: new string('x', 70_000));

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(workspaceText: new string('x', 70_001)),
            request);

        Assert.False(result.IsValid);
        Assert.Equal(ProgrammingResponseValidationError.WorkspaceTooLong, result.Error);
    }

    [Fact]
    public void Validate_ThinkAndStayProgram_IsValid() {
        ProgrammingRequest request = Request();

        ProgrammingResponseValidationResult result = ProgrammingResponseValidator.Validate(
            Response(
                program:
                [
                    new ThinkInstruction("The market bridge is usually safe before dusk."),
                    new StayInstruction(),
                ]),
            request);

        Assert.True(result.IsValid, result.Message);
    }

    private const string DefaultActivationId = "activation-1";

    private static ProgrammingRequest Request(
        IReadOnlyList<KnownPassage>? knownPassages = null,
        string? workspaceText = null) =>
        new(
            new ActivationId(DefaultActivationId),
            "actor.alice",
            10,
            ProgrammingReason.MissingInstruction,
            null,
            "place.square",
            knownPassages ?? [],
            new SubjectiveWorkspace(workspaceText ?? "workspace"),
            []);

    private static ProgrammingResponse Response(
        ActivationId? activationId = null,
        IReadOnlyList<Instruction>? program = null,
        string? workspaceText = null) =>
        new(
            activationId ?? new ActivationId(DefaultActivationId),
            program ?? [],
            new SubjectiveWorkspace(workspaceText ?? "workspace"));

    private static KnownPassage KnownPassageA(bool enterableFromA, bool enterableFromB) =>
        new(
            new PassageHandle("passage.a"),
            "place.square",
            "place.market",
            60_000,
            enterableFromA,
            enterableFromB);
}
