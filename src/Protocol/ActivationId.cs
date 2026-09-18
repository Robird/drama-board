namespace DramaBoard.Protocol;

/// <summary>Identifies one dynamic-programmer activation across the protocol boundary.</summary>
public readonly record struct ActivationId {
    /// <summary>Initializes an activation identifier from its stable value.</summary>
    public ActivationId(string value) {
        Value = StableIdentifier.Validate(value, nameof(value), "Activation identifier");
    }

    /// <summary>Gets the stable activation identifier value.</summary>
    public string Value { get; }

    /// <summary>Returns the stable activation identifier value.</summary>
    public override string ToString() => Value;
}
