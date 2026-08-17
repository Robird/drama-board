using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Journal.Atelia;

/// <summary>Encodes the complete simulation cursor snapshot into a versioned JSON envelope.</summary>
public static class CursorSnapshotEnvelopeCodec
{
    /// <summary>Gets the current cursor snapshot envelope version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Serializes a cursor snapshot.</summary>
    public static byte[] Serialize(CursorSnapshot snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteNumber("lineage", snapshot.LineageId);
            writer.WriteNumber("now", snapshot.NowTicks);
            writer.WriteNumber("resolveCount", snapshot.ResolveCountAtCurrentTime);
            writer.WriteNumber("nextBatch", snapshot.NextBatchOrdinal);
            if (snapshot.LastResolvedSourceId is long sourceId)
            {
                writer.WriteStartObject("last");
                writer.WriteNumber("s", sourceId);
                writer.WriteNumber("cid", snapshot.LastResolvedCandidateId!.Value);
                writer.WriteNumber("due", snapshot.LastResolvedDueTicks!.Value);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("last");
            }

            writer.WriteBoolean("noop", snapshot.LastResolveProducedNoEvents);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes a cursor snapshot envelope.</summary>
    public static CursorSnapshot Deserialize(ReadOnlySpan<byte> envelope)
    {
        using JsonDocument document = JsonDocument.Parse(envelope.ToArray());
        JsonElement root = document.RootElement;
        int version = root.GetProperty("v").GetInt32();
        if (version != FormatVersion)
        {
            throw new NotSupportedException($"Cursor snapshot envelope version {version} is not supported.");
        }

        JsonElement last = root.GetProperty("last");
        bool hasLastResolve = last.ValueKind != JsonValueKind.Null;
        return new CursorSnapshot(
            root.GetProperty("lineage").GetInt64(),
            root.GetProperty("now").GetInt64(),
            root.GetProperty("resolveCount").GetInt32(),
            root.GetProperty("nextBatch").GetInt64(),
            hasLastResolve ? last.GetProperty("s").GetInt64() : null,
            hasLastResolve ? last.GetProperty("cid").GetInt64() : null,
            hasLastResolve ? last.GetProperty("due").GetInt64() : null,
            root.GetProperty("noop").GetBoolean());
    }
}