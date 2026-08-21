using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia;

/// <summary>Encodes one complete committed transition into one Atelia frame payload.</summary>
public static class JournalBatchEnvelopeCodec
{
    /// <summary>Serializes one nonempty committed batch and its ordered fact payloads.</summary>
    public static byte[] Serialize<TPayload>(
        JournalBatch<TPayload> batch,
        string payloadCodec,
        Func<TPayload, byte[]> serializePayload)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadCodec);
        ArgumentNullException.ThrowIfNull(serializePayload);
        if (batch.Facts.Count == 0)
        {
            throw new ArgumentException("A persisted journal batch cannot be empty.", nameof(batch));
        }

        byte[][] payloads = new byte[batch.Facts.Count][];
        for (int index = 0; index < batch.Facts.Count; index++)
        {
            payloads[index] = serializePayload(batch.Facts[index])
                ?? throw new InvalidOperationException("The payload serializer returned null.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("instant");
            writer.WriteNumber("ms", batch.Instant.ModelTime.Ticks);
            writer.WriteNumber("ordinal", batch.Instant.CausalOrdinal);
            writer.WriteEndObject();
            writer.WriteBase64String("cause", batch.CauseKey.ToByteArray());
            writer.WriteString("pc", payloadCodec);
            writer.WriteStartArray("facts");
            foreach (byte[] payload in payloads)
            {
                writer.WriteBase64StringValue(payload);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes one complete committed batch from an Atelia frame payload.</summary>
    public static JournalBatch<TPayload> Deserialize<TPayload>(
        ReadOnlySpan<byte> envelope,
        string expectedPayloadCodec,
        Func<byte[], TPayload> deserializePayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPayloadCodec);
        ArgumentNullException.ThrowIfNull(deserializePayload);

        using JsonDocument document = JsonDocument.Parse(envelope.ToArray());
        JsonElement root = document.RootElement;
        string payloadCodec = root.GetProperty("pc").GetString()
            ?? throw new JsonException("The payload codec identifier cannot be null.");
        if (!string.Equals(payloadCodec, expectedPayloadCodec, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Payload codec mismatch: journal declares '{payloadCodec}', caller declares " +
                $"'{expectedPayloadCodec}'.");
        }

        JsonElement instant = root.GetProperty("instant");
        var logicalInstant = new LogicalInstant(
            new ModelTime(instant.GetProperty("ms").GetInt64()),
            instant.GetProperty("ordinal").GetInt64());
        CandidateKey causeKey = CandidateKey.FromBytes(root.GetProperty("cause").GetBytesFromBase64());
        JsonElement factsElement = root.GetProperty("facts");
        if (factsElement.ValueKind != JsonValueKind.Array || factsElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("A persisted journal batch must contain at least one fact.");
        }

        var facts = new List<TPayload>(factsElement.GetArrayLength());
        foreach (JsonElement payload in factsElement.EnumerateArray())
        {
            facts.Add(deserializePayload(payload.GetBytesFromBase64()));
        }

        return new JournalBatch<TPayload>(logicalInstant, causeKey, facts);
    }
}
