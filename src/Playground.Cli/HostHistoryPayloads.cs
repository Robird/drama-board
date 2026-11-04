using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The payload contract shared by durable admission and the host's reading projections.</summary>
internal static class HostHistoryPayloads {
    private static readonly JsonSerializerOptions Json = new() {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static T Read<T>(PlaygroundHostRecord record) {
        try {
            return record.Payload.Deserialize<T>(Json)
                ?? throw new InvalidDataException($"Host record {record.Sequence} has an empty payload.");
        } catch (JsonException error) {
            throw new InvalidDataException($"Host record {record.Sequence} ({record.Kind}) has an invalid payload.", error);
        }
    }

    public static void Validate(PlaygroundHostRecord record) {
        switch (record.Kind) {
            case "character-material": _ = ReadMaterial(record); break;
            case "model-request": _ = ReadRequest(record); break;
            case "model-response":
            case "model-failure": _ = ReadOutcome(record); break;
            case "feedback-saved":
                RequireActor(record);
                if (!record.Payload.TryGetProperty("FeedbackId", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString())) {
                    throw new InvalidDataException("A saved feedback record requires a FeedbackId.");
                }
                break;
        }
    }

    public static CharacterMaterialSnapshot ReadMaterial(PlaygroundHostRecord record) {
        RequireActor(record);
        var material = Read<CharacterMaterialSnapshot>(record);
        if (material.ContextSequence != record.ContextSequence) {
            throw new InvalidDataException("Character material ContextSequence does not match its host envelope.");
        }
        RequireEntries(material.Items);
        return material;
    }

    public static ModelRequestSnapshot ReadRequest(PlaygroundHostRecord record) {
        RequireActor(record);
        var request = Read<ModelRequestSnapshot>(record);
        RequireModelIdentity(request.RequestId, request.CallNumber, request.ModelId, request.RawRecordSequence);
        if (request.Boundary is { } boundary && (boundary.BranchId != record.BranchId ||
            boundary.ActorId != record.ActorId || boundary.ContextSequence != record.ContextSequence ||
            string.IsNullOrWhiteSpace(boundary.StateCommitId) || boundary.OperationSequence < 0 ||
            boundary.TransitionCount < 0 || boundary.GameRevision <= 0 || boundary.TimeTicks < 0)) {
            throw new InvalidDataException("Model request boundary is invalid or does not match its host envelope.");
        }
        ValidateMessages(request.Shared);
        ValidateMessages(request.Tail);
        return request;
    }

    public static ModelOutcomeSnapshot ReadOutcome(PlaygroundHostRecord record) {
        RequireActor(record);
        var outcome = Read<ModelOutcomeSnapshot>(record);
        RequireModelIdentity(outcome.RequestId, outcome.CallNumber, outcome.ModelId, outcome.RawRecordSequence);
        if (outcome.Result is { } result) {
            RequireEntries(result.Blocks);
            if (result.Errors is { } errors) { RequireEntries(errors); }
        }
        return outcome;
    }

    private static void ValidateMessages(IReadOnlyList<ModelMessageSnapshot> messages) {
        RequireEntries(messages);
        foreach (ModelMessageSnapshot message in messages) {
            RequireEntries(message.Blocks);
            RequireEntries(message.Results);
            foreach (ModelToolResultSnapshot result in message.Results) { RequireEntries(result.Blocks); }
        }
    }

    private static void RequireActor(PlaygroundHostRecord record) {
        if (string.IsNullOrWhiteSpace(record.RunId) || string.IsNullOrWhiteSpace(record.BranchId) ||
            string.IsNullOrWhiteSpace(record.ActorId) || record.ContextSequence is not > 0) {
            throw new InvalidDataException("Character history records require run, branch, actor and Context identities.");
        }
    }

    private static void RequireModelIdentity(string requestId, int callNumber, string modelId, long? rawRecordSequence) {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(modelId) || callNumber <= 0 || rawRecordSequence is <= 0) {
            throw new InvalidDataException("Invalid model history request identity or call number.");
        }
    }

    private static void RequireEntries<T>(IReadOnlyList<T> values) where T : class {
        // Nullable annotations reject null properties, but System.Text.Json does not check collection elements.
        if (values.Any(value => value is null)) { throw new InvalidDataException("Host history collections cannot contain null entries."); }
    }
}
