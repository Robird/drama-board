using System.Buffers;
using System.Text.Json;

namespace DramaBoard.Journal.Atelia;

/// <summary>Opaque Atelia frame kinds owned by the DramaBoard journal adapter.</summary>
public static class AteliaJournalFrameKinds
{
    /// <summary>Identifies a complete journal batch frame.</summary>
    public const uint JournalBatch = 0x4442_4231;

    /// <summary>Identifies a lineage creation metadata frame.</summary>
    public const uint LineageCreated = 0x4442_4C31;
}

/// <summary>Describes the persisted identity and parentage of one journal branch.</summary>
public readonly record struct LineageMetadata(
    long LineageId,
    long? ParentLineageId,
    int? ForkPrefixTransitionCount);

/// <summary>Encodes branch lineage creation metadata into its dedicated Atelia frame.</summary>
public static class LineageMetadataCodec
{
    /// <summary>Serializes branch lineage metadata.</summary>
    public static byte[] Serialize(LineageMetadata metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("lineage", metadata.LineageId);
            if (metadata.ParentLineageId is long parentLineageId)
            {
                writer.WriteNumber("parent", parentLineageId);
            }
            else
            {
                writer.WriteNull("parent");
            }

            if (metadata.ForkPrefixTransitionCount is int forkPrefixTransitionCount)
            {
                writer.WriteNumber("prefix", forkPrefixTransitionCount);
            }
            else
            {
                writer.WriteNull("prefix");
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes branch lineage metadata.</summary>
    public static LineageMetadata Deserialize(ReadOnlySpan<byte> framePayload)
    {
        using JsonDocument document = JsonDocument.Parse(framePayload.ToArray());
        JsonElement root = document.RootElement;
        JsonElement parent = root.GetProperty("parent");
        JsonElement prefix = root.GetProperty("prefix");
        return new LineageMetadata(
            root.GetProperty("lineage").GetInt64(),
            parent.ValueKind == JsonValueKind.Null ? null : parent.GetInt64(),
            prefix.ValueKind == JsonValueKind.Null ? null : prefix.GetInt32());
    }
}
