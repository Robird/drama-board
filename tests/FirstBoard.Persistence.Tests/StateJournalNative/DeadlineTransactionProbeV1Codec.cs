using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using DramaBoard.FirstBoard;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence.Tests.StateJournalNative;

internal sealed record DeadlineTransactionProbeV1(
    WorldVersion ParentVersion,
    JournalBatch<FirstBoardFact> Batch);

internal static class DeadlineTransactionProbeV1Codec
{
    internal const string SchemaId = "firstboard.statejournal-deadline-transaction/1";

    private const string GameCellarSealedTag = "game.cellar-sealed/1";
    private const string SpatialPassageEntryAccessChangedTag =
        "spatial.passage-entry-access-changed/1";

    public static byte[] Encode(DeadlineTransactionProbeV1 transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(transaction.Batch);

        PassageEntryAccessChangedFact changed = RequireDeadlineFacts(transaction.Batch);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", SchemaId);
            writer.WriteStartObject("parent");
            writer.WriteNumber("lineageId", transaction.ParentVersion.LineageId);
            writer.WriteNumber(
                "transitionCount",
                transaction.ParentVersion.TransitionCount);
            writer.WriteEndObject();
            writer.WriteStartObject("instant");
            writer.WriteNumber(
                "modelTimeMs",
                transaction.Batch.Instant.ModelTime.Ticks);
            writer.WriteNumber(
                "causalOrdinal",
                transaction.Batch.Instant.CausalOrdinal);
            writer.WriteEndObject();
            writer.WriteBase64String(
                "candidateKey",
                transaction.Batch.CauseKey.ToByteArray());
            writer.WriteStartArray("facts");
            writer.WriteStartObject();
            writer.WriteString("tag", GameCellarSealedTag);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("tag", SpatialPassageEntryAccessChangedTag);
            writer.WriteString("passageId", changed.PassageId.Value);
            writer.WriteBoolean(
                "enterableFromA",
                changed.ResultAccess.EnterableFromA);
            writer.WriteBoolean(
                "enterableFromB",
                changed.ResultAccess.EnterableFromB);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static DeadlineTransactionProbeV1 Decode(ReadOnlySpan<byte> encoded)
    {
        try
        {
            DeadlineTransactionProbeV1 transaction = DecodeCore(encoded);
            byte[] canonical = Encode(transaction);
            if (!encoded.SequenceEqual(canonical))
            {
                throw new InvalidDataException(
                    "The deadline transaction is valid JSON but is not in canonical wire form.");
            }

            return transaction;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
            InvalidOperationException or
            FormatException or
            ArgumentException or
            OverflowException)
        {
            throw new InvalidDataException(
                "The deadline transaction envelope is malformed.",
                exception);
        }
    }

    public static string ComputeDigest(ReadOnlySpan<byte> encoded) =>
        Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();

    private static DeadlineTransactionProbeV1 DecodeCore(ReadOnlySpan<byte> encoded)
    {
        using JsonDocument document = JsonDocument.Parse(encoded.ToArray());
        JsonElement root = document.RootElement;
        RequireExactProperties(
            root,
            "deadline transaction",
            "schema",
            "parent",
            "instant",
            "candidateKey",
            "facts");
        RequireString(root, "schema", SchemaId);

        JsonElement parent = root.GetProperty("parent");
        RequireExactProperties(
            parent,
            "deadline transaction parent",
            "lineageId",
            "transitionCount");
        var parentVersion = new WorldVersion(
            RequireInt64(parent, "lineageId"),
            RequireInt64(parent, "transitionCount"));

        JsonElement instantElement = root.GetProperty("instant");
        RequireExactProperties(
            instantElement,
            "deadline transaction instant",
            "modelTimeMs",
            "causalOrdinal");
        var instant = new LogicalInstant(
            new ModelTime(RequireInt64(instantElement, "modelTimeMs")),
            RequireInt64(instantElement, "causalOrdinal"));

        JsonElement candidateKeyElement = root.GetProperty("candidateKey");
        RequireValueKind(
            candidateKeyElement,
            JsonValueKind.String,
            "deadline transaction candidateKey");
        CandidateKey candidateKey = CandidateKey.FromBytes(
            candidateKeyElement.GetBytesFromBase64());

        JsonElement facts = root.GetProperty("facts");
        RequireValueKind(facts, JsonValueKind.Array, "deadline transaction facts");
        if (facts.GetArrayLength() != 2)
        {
            throw new InvalidDataException(
                "A deadline transaction must contain exactly two ordered facts.");
        }

        JsonElement gameFact = facts[0];
        RequireExactProperties(gameFact, "deadline Game fact", "tag");
        RequireString(gameFact, "tag", GameCellarSealedTag);

        JsonElement spatialFact = facts[1];
        RequireExactProperties(
            spatialFact,
            "deadline Spatial fact",
            "tag",
            "passageId",
            "enterableFromA",
            "enterableFromB");
        RequireString(
            spatialFact,
            "tag",
            SpatialPassageEntryAccessChangedTag);
        string passageId = RequireString(spatialFact, "passageId");
        bool enterableFromA = RequireBoolean(spatialFact, "enterableFromA");
        bool enterableFromB = RequireBoolean(spatialFact, "enterableFromB");

        FirstBoardFact[] decodedFacts =
        [
            new GameBoardFact(new CellarSealedEvent()),
            new SpatialBoardFact(new PassageEntryAccessChangedFact(
                new PassageId(passageId),
                new PassageEntryAccess(enterableFromA, enterableFromB))),
        ];
        var batch = new JournalBatch<FirstBoardFact>(
            instant,
            candidateKey,
            decodedFacts);
        return new DeadlineTransactionProbeV1(parentVersion, batch);
    }

    private static PassageEntryAccessChangedFact RequireDeadlineFacts(
        JournalBatch<FirstBoardFact> batch)
    {
        if (batch.Facts.Count != 2 ||
            batch.Facts[0] is not GameBoardFact { Value: CellarSealedEvent } ||
            batch.Facts[1] is not SpatialBoardFact
            {
                Value: PassageEntryAccessChangedFact changed,
            })
        {
            throw new ArgumentException(
                "A deadline transaction must contain exactly CellarSealed followed by " +
                "PassageEntryAccessChanged.",
                nameof(batch));
        }

        if (string.IsNullOrWhiteSpace(changed.PassageId.Value))
        {
            throw new ArgumentException(
                "The deadline passage identifier must be initialized.",
                nameof(batch));
        }

        return changed;
    }

    private static void RequireExactProperties(
        JsonElement element,
        string context,
        params string[] expectedNames)
    {
        RequireValueKind(element, JsonValueKind.Object, context);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The {context} contains duplicate property '{property.Name}'.");
            }

            if (!ContainsOrdinal(expectedNames, property.Name))
            {
                throw new InvalidDataException(
                    $"The {context} contains unknown property '{property.Name}'.");
            }
        }

        foreach (string expectedName in expectedNames)
        {
            if (!seen.Contains(expectedName))
            {
                throw new InvalidDataException(
                    $"The {context} is missing property '{expectedName}'.");
            }
        }
    }

    private static bool ContainsOrdinal(IEnumerable<string> values, string candidate)
    {
        foreach (string value in values)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static long RequireInt64(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        RequireValueKind(value, JsonValueKind.Number, propertyName);
        return value.GetInt64();
    }

    private static bool RequireBoolean(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"Property '{propertyName}' must be a JSON boolean.");
        }

        return value.GetBoolean();
    }

    private static string RequireString(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        RequireValueKind(value, JsonValueKind.String, propertyName);
        return value.GetString()
            ?? throw new InvalidDataException(
                $"Property '{propertyName}' cannot be null.");
    }

    private static void RequireString(
        JsonElement parent,
        string propertyName,
        string expectedValue)
    {
        string actual = RequireString(parent, propertyName);
        if (!string.Equals(actual, expectedValue, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Property '{propertyName}' has unsupported value '{actual}'.");
        }
    }

    private static void RequireValueKind(
        JsonElement element,
        JsonValueKind expected,
        string context)
    {
        if (element.ValueKind != expected)
        {
            throw new InvalidDataException(
                $"The {context} must be a JSON {expected} value.");
        }
    }
}
