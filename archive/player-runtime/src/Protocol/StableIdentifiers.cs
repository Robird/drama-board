namespace DramaBoard.Protocol;

/// <summary>Validates the stable identifier rules shared by the Protocol boundary types.</summary>
internal static class StableIdentifier {
    private const int MaximumLength = 256;

    public static string Validate(string value, string parameterName, string description) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"{description} cannot be empty.", parameterName);
        }

        if (value.Length > MaximumLength) {
            throw new ArgumentException(
                $"{description} cannot exceed {MaximumLength} characters.",
                parameterName);
        }

        if (value.Any(char.IsControl)) {
            throw new ArgumentException($"{description} cannot contain control characters.", parameterName);
        }

        return value;
    }
}
