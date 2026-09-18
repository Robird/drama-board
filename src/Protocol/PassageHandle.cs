namespace DramaBoard.Protocol;

/// <summary>Identifies one passage in the actor's known-passages graph.</summary>
public readonly record struct PassageHandle {
    /// <summary>Initializes a passage handle from its stable value.</summary>
    public PassageHandle(string value) {
        Value = StableIdentifier.Validate(value, nameof(value), "Passage handle");
    }

    /// <summary>Gets the stable passage handle value.</summary>
    public string Value { get; }

    /// <summary>Returns the stable passage handle value.</summary>
    public override string ToString() => Value;
}
