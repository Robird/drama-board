namespace DramaBoard.Protocol;

/// <summary>Carries the actor's subjective workspace text across the programmer boundary.</summary>
/// <param name="Text">The workspace text, which may be empty but not null.</param>
public sealed record SubjectiveWorkspace(string Text) {
    /// <summary>Gets the workspace text, which may be empty but not null.</summary>
    public string Text { get; } = ValidateNotNull(Text);

    private static string ValidateNotNull(string text) {
        ArgumentNullException.ThrowIfNull(text);
        return text;
    }
}
