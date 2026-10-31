namespace DramaBoard.ResourceCycle.Completion;

/// <summary>Process-local operational limits, shared by all characters served by this controller instance.</summary>
/// <remarks>A new host run creates a new budget. These counters are not character memory or an exactly-once ledger.</remarks>
public sealed record LlmControllerOptions {
    public int MaxCalls { get; init; } = 128;
    public int MaxConsecutiveProtocolFailures { get; init; } = 3;
    /// <summary>Maximum rendered context plus local feedback history and correction material in UTF-16 code units.
    /// Native action blocks are counted by serialized size. Excess is rejected, never truncated.</summary>
    public int MaxContextCharacters { get; init; } = 200_000;
    /// <summary>Maximum decoded action string length in UTF-16 code units.</summary>
    public int MaxCommandCharacters { get; init; } = 8192;
    /// <summary>Successful host feedback submissions allowed within one character service.</summary>
    public int MaxFeedbackPerDecision { get; init; } = 3;
    /// <summary>Maximum combined decoded feedback fields, measured in UTF-16 code units.</summary>
    public int MaxFeedbackCharacters { get; init; } = 4000;

    internal void Validate() {
        if (MaxCalls <= 0 || MaxConsecutiveProtocolFailures <= 0 || MaxContextCharacters <= 0 ||
            MaxCommandCharacters <= 0 || MaxCommandCharacters > int.MaxValue / 6 - 1024 ||
            MaxFeedbackPerDecision <= 0 || MaxFeedbackCharacters <= 0 || MaxFeedbackCharacters > int.MaxValue / 6 - 1024) {
            throw new ArgumentOutOfRangeException(nameof(LlmControllerOptions), "All limits must be positive and the command limit must support bounded JSON input.");
        }
    }
}

/// <summary>Explicit host pause; no substitute game operation has been produced.</summary>
public sealed class LlmControllerPausedException(string reasonCode, int callsMade, Exception? innerException = null)
    : Exception($"LLM controller paused: {reasonCode} (calls made: {callsMade}).", innerException) {
    public string ReasonCode { get; } = reasonCode;
    public int CallsMade { get; } = callsMade;
}
