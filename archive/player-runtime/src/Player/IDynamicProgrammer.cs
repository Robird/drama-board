using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Proposes one complete program and subjective revision for the exact frozen request.</summary>
public interface IDynamicProgrammer {
    /// <summary>Proposes a program and subjective revision for a frozen programming request.</summary>
    ValueTask<ProgrammingResponse> ProgramAsync(
        ProgrammingRequest request,
        CancellationToken cancellationToken);
}
