using System.Buffers;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Journal.Atelia;

/// <summary>Encodes committed domain events into the versioned payload stored by EventJournal.</summary>
public static class DomainEventEnvelopeCodec
{
    /// <summary>Gets the current envelope format version.</summary>
    public const int FormatVersion = 2;

    /// <summary>Serializes a committed event and its opaque payload bytes.</summary>
    public static byte[] Serialize<TPayload>(
        DomainEvent<TPayload> domainEvent,
        string payloadCodec,
        int batchIndex,
        int batchCount,
        Func<TPayload, byte[]> serializePayload)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadCodec);
        ArgumentNullException.ThrowIfNull(serializePayload);
        if (batchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchCount));
        }

        if (batchIndex < 0 || batchIndex >= batchCount)
        {
            throw new ArgumentOutOfRangeException(nameof(batchIndex));
        }

        byte[] payload = serializePayload(domainEvent.Payload)
            ?? throw new InvalidOperationException("The payload serializer returned null.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteStartObject("t");
            writer.WriteNumber("ms", domainEvent.Timestamp.ModelTime.Ticks);
            writer.WriteNumber("us", domainEvent.Timestamp.Microstep.Value);
            writer.WriteEndObject();
            writer.WriteStartObject("c");
            writer.WriteNumber("k", (int)domainEvent.Cause.Kind);
            writer.WriteNumber("s", domainEvent.Cause.SourceId);
            writer.WriteNumber("cid", domainEvent.Cause.CandidateId.Value);
            writer.WriteNumber("due", domainEvent.Cause.Due.Ticks);
            writer.WriteNumber("b", domainEvent.Cause.BatchOrdinal);
            writer.WriteEndObject();
            writer.WriteStartObject("kind");
            writer.WriteString("id", domainEvent.Kind.Id);
            writer.WriteNumber("ver", domainEvent.Kind.Version);
            writer.WriteEndObject();
            writer.WriteString("pc", payloadCodec);
            writer.WriteNumber("bi", batchIndex);
            writer.WriteNumber("bc", batchCount);
            writer.WriteBase64String("p", payload);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Deserializes one versioned envelope into a committed domain event.</summary>
    public static DomainEvent<TPayload> Deserialize<TPayload>(
        ReadOnlySpan<byte> envelope,
        string expectedPayloadCodec,
        Func<EventKind, byte[], TPayload> deserializePayload,
        out int batchIndex,
        out int batchCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPayloadCodec);
        ArgumentNullException.ThrowIfNull(deserializePayload);

        using JsonDocument document = JsonDocument.Parse(envelope.ToArray());
        JsonElement root = document.RootElement;
        int version = root.GetProperty("v").GetInt32();
        if (version != FormatVersion)
        {
            throw new NotSupportedException($"Domain event envelope version {version} is not supported.");
        }

        JsonElement timestamp = root.GetProperty("t");
        var logicalTimestamp = new LogicalTimestamp(
            new ModelTime(timestamp.GetProperty("ms").GetInt64()),
            new Microstep(timestamp.GetProperty("us").GetInt32()));

        JsonElement cause = root.GetProperty("c");
        var eventCause = new EventCause(
            (CauseKind)cause.GetProperty("k").GetInt32(),
            cause.GetProperty("s").GetInt64(),
            new EventCandidateId(cause.GetProperty("cid").GetInt64()),
            new ModelTime(cause.GetProperty("due").GetInt64()),
            cause.GetProperty("b").GetInt64());

        JsonElement kind = root.GetProperty("kind");
        var eventKind = new EventKind(
            kind.GetProperty("id").GetString()
                ?? throw new JsonException("The event kind id cannot be null."),
            checked((ushort)kind.GetProperty("ver").GetInt32()));
        string payloadCodec = root.GetProperty("pc").GetString()
            ?? throw new JsonException("The payload codec identifier cannot be null.");
        if (!string.Equals(payloadCodec, expectedPayloadCodec, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Payload codec mismatch: journal declares '{payloadCodec}', caller declares " +
                $"'{expectedPayloadCodec}'.");
        }

        batchIndex = root.GetProperty("bi").GetInt32();
        batchCount = root.GetProperty("bc").GetInt32();
        if (batchCount <= 0 || batchIndex < 0 || batchIndex >= batchCount)
        {
            throw new IncompleteEventBatchException(
                $"Invalid event batch position bi={batchIndex}, bc={batchCount}.");
        }

        byte[] payloadBytes = root.GetProperty("p").GetBytesFromBase64();

        return new DomainEvent<TPayload>(
            logicalTimestamp,
            eventCause,
            eventKind,
            deserializePayload(eventKind, payloadBytes));
    }
}

/// <summary>Identifies a visible physical frame that does not contain one complete logical batch.</summary>
public sealed class IncompleteEventBatchException : IOException
{
    /// <summary>Initializes an incomplete-batch diagnostic.</summary>
    public IncompleteEventBatchException(string message)
        : base(message)
    {
    }
}
