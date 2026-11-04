using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;

public sealed class HostHistoryPayloadContractTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "drama-history-contract-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => PlaygroundHostJournal.JournalPath(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); } }

    public static TheoryData<string> InvalidCases => new() {
        "material-missing-items", "material-null-opening", "material-null-text", "material-null-item",
        "material-null-experiences", "material-context", "material-run", "material-actor",
        "request-missing-model", "request-null-shared", "request-null-message", "request-null-blocks",
        "request-null-block", "request-null-result", "request-null-result-blocks", "request-null-tool-input",
        "request-boundary-actor", "request-boundary-branch", "request-boundary-context", "request-boundary-missing-counter",
        "request-branch", "request-context", "response-missing-stage", "response-null-blocks",
        "response-null-block", "response-null-error", "response-null-candidate-input", "failure-missing-call", "feedback-missing-id",
        "receipt-missing-context", "completion-missing-world", "completion-missing-event", "completion-missing-exact"
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void UnreadableRecordIsRejectedBeforeWritingAndDoesNotRetireWriter(string example) {
        using var journal = PlaygroundHostJournal.Open(JournalPath);
        PlaygroundHostRecord invalid = InvalidRecord(example);
        PrepareReceipt(journal, invalid);
        byte[] before = ReadBytes();
        int count = journal.Records.Count;

        Assert.Throws<InvalidDataException>(() => journal.Append(invalid.Kind, invalid.RunId, invalid.BranchId,
            invalid.ActorId, invalid.ContextSequence, invalid.Payload));

        Assert.Equal(before, ReadBytes());
        Assert.Equal(count, journal.Records.Count);
        Assert.False(journal.IsFaulted);
        Assert.Equal(count + 1, journal.Append("model-request", "run", "branch", "a", 1, Request()).Sequence);
    }

    private byte[] ReadBytes() {
        using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void ChecksummedUnreadableCompletedLineIsRejectedWithoutRepair(string example) {
        PlaygroundHostRecord invalid = InvalidRecord(example);
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) { PrepareReceipt(journal, invalid); }
        string[] lines = File.ReadAllLines(JournalPath);
        long sequence = lines.Length + 1;
        string previous = JsonNode.Parse(lines[^1])!["Hash"]!.GetValue<string>();
        string payload = JsonSerializer.Serialize(invalid with { Sequence = sequence });
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previous + "\n" + payload)));
        File.AppendAllText(JournalPath, JsonSerializer.Serialize(new {
            Sequence = sequence, PreviousHash = previous, Payload = payload, Hash = hash
        }) + "\n", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(JournalPath);

        Assert.Throws<InvalidDataException>(() => PlaygroundHostJournal.Read(JournalPath));
        Assert.Throws<InvalidDataException>(() => { using var unexpected = PlaygroundHostJournal.Open(JournalPath); });
        Assert.Equal(before, File.ReadAllBytes(JournalPath));
    }

    [Fact]
    public void CompleteProjectionsRemainExportableAfterReopenIncludingNullableAndUnknownEvidence() {
        using (var journal = PlaygroundHostJournal.Open(JournalPath)) {
            journal.Append("character-material", "run", "branch", "a", 1, Material());
            journal.Append("model-request", "run", "branch", "a", 1, Request());
            journal.Append("model-request", "run", "branch", "a", 1, Request() with { Boundary = null });
            journal.Append("model-response", "run", "branch", "a", 1, Outcome(LlmControllerTraceKind.Response));
            journal.Append("model-response", "run", "branch", "a", 1, Outcome(LlmControllerTraceKind.Response) with {
                Termination = new("Cancelled", null, null), ProtocolError = "incomplete response", ProposedOperation = null
            });
            journal.Append("model-failure", "run", "branch", "a", 1, Outcome(LlmControllerTraceKind.Failure));
            journal.Append("feedback-saved", "run", "branch", "a", 1, new { FeedbackId = "F1" });
        }
        byte[] before = File.ReadAllBytes(JournalPath);
        var snapshot = PlaygroundHostJournal.Read(JournalPath);
        string text = CharacterHistoryExporter.Render(snapshot, "branch", "a");

        Assert.Contains("frozen observation", text);
        Assert.Contains("readable reasoning", text);
        Assert.Contains("carried receipt", text);
        Assert.Contains("future block gap", text);
        Assert.Contains("完整模型结果未捕获", text);
        Assert.Contains("没有可读 PlainText", text);
        Assert.Contains("incomplete response", text);
        Assert.Contains("lineage -7", text);
        Assert.Contains("wait 1", text);
        Assert.Equal(before, File.ReadAllBytes(JournalPath));
        using var reopened = PlaygroundHostJournal.Open(JournalPath);
        Assert.Equal(snapshot.Records.Count, reopened.Records.Count);
    }

    private static PlaygroundHostRecord InvalidRecord(string example) {
        string kind = example.Split('-')[0] switch {
            "material" => "character-material", "request" => "model-request", "response" => "model-response",
            "failure" => "model-failure", "receipt" => "operation-received", "completion" => "operation-completed",
            _ => "feedback-saved"
        };
        object value = kind switch {
            "character-material" => Material(), "model-request" => Request(),
            "model-response" => Outcome(LlmControllerTraceKind.Response),
            "model-failure" => Outcome(LlmControllerTraceKind.Failure),
            "operation-received" => new PlaygroundOperationReceipt("operation", "batch", "call", "a", null, "console", ":look"),
            "operation-completed" => new PlaygroundOperationCompletion("operation", new("call", PlaygroundOperationStatus.Succeeded, "done", null, null, true)),
            _ => new { FeedbackId = "F1" }
        };
        var payload = JsonSerializer.SerializeToNode(value, value.GetType())!.AsObject();
        var record = new PlaygroundHostRecord(1, 2, kind, "run", "branch", "a", 1, default);
        switch (example) {
            case "material-missing-items": payload.Remove("Items"); break;
            case "material-null-opening": payload["Opening"] = null; break;
            case "material-null-text": payload["ObservationText"] = null; break;
            case "material-null-item": payload["Items"]![0] = null; break;
            case "material-null-experiences": payload["Items"]![0]!["ExperienceSequences"] = null; break;
            case "material-context": payload["ContextSequence"] = 2; break;
            case "material-run": record = record with { RunId = null }; break;
            case "material-actor": record = record with { ActorId = null }; break;
            case "request-missing-model": payload.Remove("ModelId"); break;
            case "request-null-shared": payload["Shared"] = null; break;
            case "request-null-message": payload["Shared"]![0] = null; break;
            case "request-null-blocks": payload["Shared"]![0]!["Blocks"] = null; break;
            case "request-null-block": payload["Shared"]![0]!["Blocks"]![0] = null; break;
            case "request-null-result": payload["Shared"]![0]!["Results"]![0] = null; break;
            case "request-null-result-blocks": payload["Shared"]![0]!["Results"]![0]!["Blocks"] = null; break;
            case "request-null-tool-input": payload["Shared"]![0]!["Blocks"]![0]!["ToolCall"]!["RawInput"] = null; break;
            case "request-boundary-actor": payload["Boundary"]!["ActorId"] = "other"; break;
            case "request-boundary-branch": payload["Boundary"]!["BranchId"] = "other"; break;
            case "request-boundary-context": payload["Boundary"]!["ContextSequence"] = 2; break;
            case "request-boundary-missing-counter": payload["Boundary"]!.AsObject().Remove("OperationSequence"); break;
            case "request-branch": record = record with { BranchId = null }; break;
            case "request-context": record = record with { ContextSequence = null }; break;
            case "response-missing-stage": payload.Remove("Stage"); break;
            case "response-null-blocks": payload["Result"]!["Blocks"] = null; break;
            case "response-null-block": payload["Result"]!["Blocks"]![0] = null; break;
            case "response-null-error": payload["Result"]!["Errors"] = new JsonArray((JsonNode?)null); break;
            case "response-null-candidate-input": payload["ProposedOperation"]!["RawInput"] = null; break;
            case "failure-missing-call": payload.Remove("CallNumber"); break;
            case "feedback-missing-id": payload.Remove("FeedbackId"); break;
            case "receipt-missing-context": payload.Remove("ContextSequence"); record = record with { ContextSequence = null }; break;
            case "completion-missing-world": payload["Completed"]!.AsObject().Remove("WorldVersion"); break;
            case "completion-missing-event": payload["Completed"]!.AsObject().Remove("DomainEvent"); break;
            case "completion-missing-exact": payload["Completed"]!.AsObject().Remove("ExactResult"); break;
            default: throw new ArgumentException(example);
        }
        return record with { Payload = JsonSerializer.SerializeToElement(payload) };
    }

    private static void PrepareReceipt(PlaygroundHostJournal journal, PlaygroundHostRecord record) {
        if (record.Kind == "operation-completed") {
            new PlaygroundOperationAudit(journal, "branch", "run").Receive("batch", "call", "a", 1, "console", ":look", "operation");
        }
    }

    private static CharacterMaterialSnapshot Material() => new(1, "Open", null, "frozen observation",
        new("opening", [1], new(1, 1, "advance")), [new(1, 1, "operation", [2], "item text")],
        LineageId: -7, TimeTicks: 5);

    private static ModelRequestSnapshot Request() => new("request", 1, null, "model", null, null,
        new("branch", "S1", "a", 1, 0, -7, 0, 0, PlaygroundGameRevision.Current), [
            new("Shared", 0, "Action", null, [new(0, "ToolCall", ToolCall: new("act", "call", "JsonObject", "wait 1", null))],
                [new(0, "feedback", "feedback-call", "Success", [new(0, "Text", Text: "carried receipt")])]),
            new("Shared", 1, "Unsupported", null, [], [], "future message gap")
        ], []);

    private static ModelOutcomeSnapshot Outcome(LlmControllerTraceKind kind) {
        var result = kind == LlmControllerTraceKind.Response
            ? new CompletionResult(new([new ActionBlock.TextReasoningBlock("readable reasoning", new("p", "api", "model"))]),
                new("p", "api", "model")) : null;
        var outcome = ModelHistoryProjection.Outcome(new(kind, 1, "a", 1,
            new("model", new("system", CompletionOutputContract.ProviderDefault([]), []), []), result,
            Operation: new("call", "wait 1", "candidate"), RequestId: "request"), null);
        return outcome.Result is null ? outcome : outcome with {
            Result = outcome.Result with { Blocks = [.. outcome.Result.Blocks,
                new(1, "FutureKind", ProjectionGap: "future block gap"), new(2, "Reasoning")] }
        };
    }
}
