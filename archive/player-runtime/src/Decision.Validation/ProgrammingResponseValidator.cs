using DramaBoard.Protocol;

namespace DramaBoard.Decision.Validation;

/// <summary>Describes why a dynamic-programmer response does not match its programming request.</summary>
public enum ProgrammingResponseValidationError {
    None = 0,
    ActivationIdMismatch,
    UnknownPassageHandle,
    WorkspaceTooLong,
}

/// <summary>Contains the pure validation result for one dynamic-programmer response.</summary>
public readonly record struct ProgrammingResponseValidationResult(
    ProgrammingResponseValidationError Error,
    string? Message) {
    public bool IsValid => Error == ProgrammingResponseValidationError.None;

    public static ProgrammingResponseValidationResult Valid { get; } =
        new(ProgrammingResponseValidationError.None, null);
}

/// <summary>
/// Validates one dynamic-programmer response against its frozen request without reading the world
/// or performing I/O: known-reference qualification only, not current executability.
/// </summary>
public static class ProgrammingResponseValidator {
    private const int MaximumWorkspaceLength = 65_536;

    public static ProgrammingResponseValidationResult Validate(
        ProgrammingResponse response,
        ProgrammingRequest request) {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(request);

        if (response.ActivationId != request.ActivationId) {
            return new(
                ProgrammingResponseValidationError.ActivationIdMismatch,
                "The programming response does not match the requested ActivationId.");
        }

        var knownHandles = new HashSet<string>(
            request.KnownPassages.Select(passage => passage.Handle.Value),
            StringComparer.Ordinal);
        foreach (Instruction instruction in response.Program) {
            if (instruction is MoveInstruction move && !knownHandles.Contains(move.Passage.Value)) {
                return new(
                    ProgrammingResponseValidationError.UnknownPassageHandle,
                    $"The program's move instruction references passage '{move.Passage.Value}', which is not among the request's known passages.");
            }
        }

        int workspaceLimit = Math.Max(MaximumWorkspaceLength, request.Workspace.Text.Length);
        if (response.Workspace.Text.Length > workspaceLimit) {
            return new(
                ProgrammingResponseValidationError.WorkspaceTooLong,
                $"The workspace revision contains {response.Workspace.Text.Length} characters, which exceeds the allowed maximum of {workspaceLimit} characters.");
        }

        return ProgrammingResponseValidationResult.Valid;
    }
}
