namespace DramaBoard.Protocol;

/// <summary>Describes one passage in the actor's known-passages graph.</summary>
/// <param name="Handle">The stable handle of the passage.</param>
/// <param name="EndpointA">The identifier of the passage's endpoint A.</param>
/// <param name="EndpointB">The identifier of the passage's endpoint B.</param>
/// <param name="ExpectedDurationMs">The expected travel duration in model-time milliseconds.</param>
/// <param name="EnterableFromA">Whether the passage can be entered from endpoint A.</param>
/// <param name="EnterableFromB">Whether the passage can be entered from endpoint B.</param>
public sealed record KnownPassage(
    PassageHandle Handle,
    string EndpointA,
    string EndpointB,
    long ExpectedDurationMs,
    bool EnterableFromA,
    bool EnterableFromB) {
    /// <summary>Gets the stable handle of the passage.</summary>
    public PassageHandle Handle { get; } = Handle;

    /// <summary>Gets the identifier of the passage's endpoint A.</summary>
    public string EndpointA { get; } = ValidateEndpoint(EndpointA, nameof(EndpointA));

    /// <summary>Gets the identifier of the passage's endpoint B.</summary>
    public string EndpointB { get; } = ValidateEndpoint(EndpointB, nameof(EndpointB));

    /// <summary>Gets the expected travel duration in model-time milliseconds.</summary>
    public long ExpectedDurationMs { get; } = ValidateDuration(ExpectedDurationMs, nameof(ExpectedDurationMs));

    /// <summary>Gets a value indicating whether the passage can be entered from endpoint A.</summary>
    public bool EnterableFromA { get; } = EnterableFromA;

    /// <summary>Gets a value indicating whether the passage can be entered from endpoint B.</summary>
    public bool EnterableFromB { get; } = EnterableFromB;

    private static string ValidateEndpoint(string endpoint, string parameterName) {
        if (string.IsNullOrWhiteSpace(endpoint)) {
            throw new ArgumentException("Passage endpoints cannot be empty.", parameterName);
        }

        return endpoint;
    }

    private static long ValidateDuration(long value, string parameterName) {
        if (value <= 0) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Expected duration must be a positive number of milliseconds.");
        }

        return value;
    }
}
