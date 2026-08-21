namespace DramaBoard.Protocol;

/// <summary>Describes what a Player wants an actor to attempt.</summary>
/// <param name="ActionKind">The stable action contract identifier.</param>
/// <param name="TargetActorId">The optional actor involved in the action.</param>
/// <param name="TargetObjectId">The optional object involved in the action.</param>
/// <param name="DestinationId">The optional destination for a travel action.</param>
/// <param name="FreeText">The optional natural-language content of the action.</param>
/// <param name="DurationMs">The optional duration in model-time milliseconds.</param>
/// <param name="UntilModelTimeMs">The optional absolute model time in milliseconds at which waiting ends.</param>
public sealed record Intent(
    ActionKind ActionKind,
    string? TargetActorId = null,
    string? TargetObjectId = null,
    string? DestinationId = null,
    string? FreeText = null,
    long? DurationMs = null,
    long? UntilModelTimeMs = null)
{
    /// <summary>Gets the optional natural-language content after validating its protocol limit.</summary>
    public string? FreeText { get; init; } = ProtocolValue.ValidateOptionalFreeText(FreeText, nameof(FreeText));

    /// <summary>Gets the optional bounded positive duration in model-time milliseconds.</summary>
    public long? DurationMs { get; init; } = ProtocolValue.ValidateDuration(DurationMs, nameof(DurationMs));

    /// <summary>Gets the optional non-negative absolute model time at which waiting ends.</summary>
    public long? UntilModelTimeMs { get; init; } =
        ProtocolValue.ValidateModelTime(UntilModelTimeMs, nameof(UntilModelTimeMs));
}

internal static class ProtocolValue
{
    private const int MaximumFreeTextLength = 4_096;
    private const long MaximumDurationMs = 315_360_000_000;

    public static string? ValidateOptionalFreeText(string? value, string parameterName)
    {
        if (value is { Length: > MaximumFreeTextLength })
        {
            throw new ArgumentException(
                $"Free text cannot exceed {MaximumFreeTextLength} characters.",
                parameterName);
        }

        return value;
    }

    public static long? ValidateDuration(long? value, string parameterName)
    {
        if (value is < 1 or > MaximumDurationMs)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Duration must be between 1 and {MaximumDurationMs} milliseconds.");
        }

        return value;
    }

    public static long? ValidateModelTime(long? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Model time cannot be negative.");
        }

        return value;
    }
}
