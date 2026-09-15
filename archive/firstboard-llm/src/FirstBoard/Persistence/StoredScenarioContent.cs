using System.Text.Json;

namespace DramaBoard.FirstBoard.Persistence;

/// <summary>Reads the existing ScenarioDefinition canonical content format, not dynamic State or Event bodies.</summary>
internal static class StoredScenarioContent
{
    internal static ScenarioDefinition Read(byte[] utf8)
    {
        using JsonDocument document = JsonDocument.Parse(utf8);
        JsonElement root = document.RootElement;
        if (Text(root, "schema") != "dramaboard.scenario-definition/2")
        {
            throw new InvalidDataException("The saved scenario content format is unsupported.");
        }
        var definition = new ScenarioDefinition(
            Text(root, "id"),
            root.GetProperty("revision").GetInt32(),
            Text(root, "rulesetId"),
            root.GetProperty("cellarDeadlineMs").GetInt64(),
            root.GetProperty("places").EnumerateArray()
                .Select(place => new ScenarioPlaceDefinition(place.GetString()
                    ?? throw new InvalidDataException("A scenario place ID cannot be null."))).ToArray(),
            root.GetProperty("passages").EnumerateArray().Select(passage => new ScenarioPassageDefinition(
                Text(passage, "id"), Text(passage, "endpointAId"), Text(passage, "endpointBId"),
                passage.GetProperty("length").GetInt64(), passage.GetProperty("enterableFromA").GetBoolean(),
                passage.GetProperty("enterableFromB").GetBoolean(), OptionalText(passage, "requiredTicketObjectId"))).ToArray(),
            root.GetProperty("actors").EnumerateArray().Select(actor => new ScenarioActorDefinition(
                Text(actor, "id"), Text(actor, "initialPlaceId"), new ScenarioRoleDefinition(
                    Text(actor, "name"), Text(actor, "traits"), Text(actor, "goal"), Text(actor, "voice"),
                    actor.GetProperty("referenceMaterials").EnumerateArray().Select(material =>
                        new ScenarioReferenceMaterialDefinition(Text(material, "id"),
                            Text(material, "source"), Text(material, "content"))).ToArray(),
                    actor.GetProperty("initialMemoryShards").EnumerateArray().Select(shard =>
                        new ScenarioMemoryShardDefinition(Text(shard, "key"), Text(shard, "title"),
                            Text(shard, "maintenanceInstructions"), Text(shard, "initialContent"))).ToArray()))).ToArray(),
            root.GetProperty("objects").EnumerateArray().Select(item => new ScenarioObjectDefinition(
                Text(item, "id"), OptionalText(item, "initialPlaceId"), OptionalText(item, "initialOwnerActorId"))).ToArray());
        definition.Validate();
        return definition.Freeze();
    }

    private static string Text(JsonElement parent, string name) =>
        parent.GetProperty(name).GetString() ?? throw new InvalidDataException($"Scenario field '{name}' cannot be null.");

    private static string? OptionalText(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }
}
