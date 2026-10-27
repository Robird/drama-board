namespace DramaBoard.Protocol;

/// <summary>Represents one step of a program produced by the dynamic programmer.</summary>
public abstract record Instruction;

/// <summary>Travels through the given known passage.</summary>
/// <param name="Passage">The passage to travel through.</param>
public sealed record MoveInstruction(PassageHandle Passage) : Instruction;

/// <summary>Appends one think note to the actor's subjective workspace.</summary>
/// <param name="Note">The natural-language note produced by thinking.</param>
public sealed record ThinkInstruction(string Note) : Instruction {
    private const int MaximumNoteLength = 4_096;

    /// <summary>Gets the natural-language note after validating its protocol limit.</summary>
    public string Note { get; } = ValidateNote(Note, nameof(Note));

    private static string ValidateNote(string note, string parameterName) {
        if (string.IsNullOrWhiteSpace(note)) {
            throw new ArgumentException("Think note cannot be empty.", parameterName);
        }

        if (note.Length > MaximumNoteLength) {
            throw new ArgumentException(
                $"Think note cannot exceed {MaximumNoteLength} characters.",
                parameterName);
        }

        return note;
    }
}

/// <summary>Keeps the actor in place without acting.</summary>
public sealed record StayInstruction : Instruction;
