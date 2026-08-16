using System.Text.Json;
using System.Text.Json.Serialization;

namespace DramaBoard.Protocol;

/// <summary>Identifies one decision exchange across the Player boundary.</summary>
[JsonConverter(typeof(DecisionIdJsonConverter))]
public readonly record struct DecisionId
{
    /// <summary>Initializes a decision identifier from its stable value.</summary>
    public DecisionId(string value)
    {
        Value = StableIdentifier.Validate(value, nameof(value), "Decision identifier");
    }

    /// <summary>Gets the stable decision identifier value.</summary>
    public string Value { get; }

    /// <summary>Returns the stable decision identifier value.</summary>
    public override string ToString() => Value;
}

/// <summary>Identifies an action contract by a stable, hand-authored string.</summary>
[JsonConverter(typeof(ActionKindJsonConverter))]
public readonly record struct ActionKind
{
    /// <summary>Initializes an action kind from its stable identifier.</summary>
    public ActionKind(string id)
    {
        Id = StableIdentifier.Validate(id, nameof(id), "Action kind identifier");
    }

    /// <summary>Gets the stable action identifier.</summary>
    public string Id { get; }

    /// <summary>Returns the stable action identifier.</summary>
    public override string ToString() => Id;
}

/// <summary>Identifies why the Host opened a decision point.</summary>
[JsonConverter(typeof(DecisionReasonJsonConverter))]
public readonly record struct DecisionReason
{
    /// <summary>Initializes a decision reason from its stable identifier.</summary>
    public DecisionReason(string id)
    {
        Id = StableIdentifier.Validate(id, nameof(id), "Decision reason identifier");
    }

    /// <summary>Gets the stable decision reason identifier.</summary>
    public string Id { get; }

    /// <summary>Returns the stable decision reason identifier.</summary>
    public override string ToString() => Id;
}

/// <summary>Identifies a known-fact contract by a stable, hand-authored string.</summary>
[JsonConverter(typeof(FactKindJsonConverter))]
public readonly record struct FactKind
{
    /// <summary>Initializes a fact kind from its stable identifier.</summary>
    public FactKind(string id)
    {
        Id = StableIdentifier.Validate(id, nameof(id), "Fact kind identifier");
    }

    /// <summary>Gets the stable fact identifier.</summary>
    public string Id { get; }

    /// <summary>Returns the stable fact identifier.</summary>
    public override string ToString() => Id;
}

internal static class StableIdentifier
{
    public static string Validate(string value, string parameterName, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} cannot be empty.", parameterName);
        }

        return value;
    }
}

internal sealed class DecisionIdJsonConverter : JsonConverter<DecisionId>
{
    public override DecisionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadRequiredString(ref reader, "decision identifier"));

    public override void Write(Utf8JsonWriter writer, DecisionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    private static string ReadRequiredString(ref Utf8JsonReader reader, string description) =>
        reader.GetString() ?? throw new JsonException($"The {description} must be a string.");
}

internal sealed class ActionKindJsonConverter : JsonConverter<ActionKind>
{
    public override ActionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadRequiredString(ref reader, "action kind"));

    public override void Write(Utf8JsonWriter writer, ActionKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Id);

    private static string ReadRequiredString(ref Utf8JsonReader reader, string description) =>
        reader.GetString() ?? throw new JsonException($"The {description} must be a string.");
}

internal sealed class DecisionReasonJsonConverter : JsonConverter<DecisionReason>
{
    public override DecisionReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadRequiredString(ref reader, "decision reason"));

    public override void Write(Utf8JsonWriter writer, DecisionReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Id);

    private static string ReadRequiredString(ref Utf8JsonReader reader, string description) =>
        reader.GetString() ?? throw new JsonException($"The {description} must be a string.");
}

internal sealed class FactKindJsonConverter : JsonConverter<FactKind>
{
    public override FactKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadRequiredString(ref reader, "fact kind"));

    public override void Write(Utf8JsonWriter writer, FactKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Id);

    private static string ReadRequiredString(ref Utf8JsonReader reader, string description) =>
        reader.GetString() ?? throw new JsonException($"The {description} must be a string.");
}
