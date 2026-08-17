using System.Buffers;
using System.Text.Json;

namespace DramaBoard.Journal.Atelia;

/// <summary>Encodes one complete logical event batch into one atomically visible Atelia frame.</summary>
public static class DomainEventBatchFrameCodec
{
    /// <summary>Gets the current physical batch-frame format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Serializes the event envelopes in one nonempty batch.</summary>
    public static byte[] Serialize(IReadOnlyList<byte[]> eventEnvelopes)
    {
        ArgumentNullException.ThrowIfNull(eventEnvelopes);
        if (eventEnvelopes.Count == 0)
        {
            throw new ArgumentException("A persisted event batch cannot be empty.", nameof(eventEnvelopes));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteStartArray("events");
            foreach (byte[] envelope in eventEnvelopes)
            {
                ArgumentNullException.ThrowIfNull(envelope);
                writer.WriteBase64StringValue(envelope);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes all event envelopes from one physical batch frame.</summary>
    public static IReadOnlyList<byte[]> Deserialize(ReadOnlySpan<byte> framePayload)
    {
        using JsonDocument document = JsonDocument.Parse(framePayload.ToArray());
        JsonElement root = document.RootElement;
        int version = root.GetProperty("v").GetInt32();
        if (version != FormatVersion)
        {
            throw new NotSupportedException($"Domain event batch frame version {version} is not supported.");
        }

        JsonElement events = root.GetProperty("events");
        var envelopes = new List<byte[]>(events.GetArrayLength());
        foreach (JsonElement envelope in events.EnumerateArray())
        {
            envelopes.Add(envelope.GetBytesFromBase64());
        }

        if (envelopes.Count == 0)
        {
            throw new IncompleteEventBatchException("A persisted event batch frame cannot be empty.");
        }

        return envelopes.AsReadOnly();
    }
}
