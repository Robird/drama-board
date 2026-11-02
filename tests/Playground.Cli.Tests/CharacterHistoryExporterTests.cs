using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class CharacterHistoryExporterTests {
    [Fact]
    public void Human_history_contains_observation_operation_and_world_response_without_invented_model_sections() {
        var receipt = new PlaygroundOperationReceipt("O1", "batch", "call", "a", 1, "console", "look");
        var result = new PlaygroundOperationResult("call", PlaygroundOperationStatus.Succeeded, "world says hello", null, null, true);
        var snapshot = Snapshot(
            Start(1),
            Record(2, "character-material", Material("frozen human observation")),
            Record(3, "operation-received", receipt),
            Record(4, "operation-completed", new { OperationRecordId = "O1", Completed = result, Recovered = false }),
            Record(5, "run-end", new { Reason = "input-ended", StateCommitId = "S1" }, actor: null, context: null));

        string text = CharacterHistoryExporter.Render(snapshot, "parent", "a");
        Assert.Contains("frozen human observation", text);
        Assert.Contains("look", text);
        Assert.Contains("world says hello", text);
        Assert.Contains("这是宿主操作回应", text);
        Assert.DoesNotContain("Reasoning (PlainText)", text);
        Assert.DoesNotContain("块 0 · Response", text);
        Assert.DoesNotContain("完整原始 Payload", text);
        Assert.Contains("<!-- host-record {\"Sequence\":3", text);
        Assert.Throws<ArgumentException>(() => CharacterHistoryExporter.Render(snapshot, "parent", "nonexistent"));
    }

    [Fact]
    public void All_readable_model_material_survives_missing_raw_and_corrupt_optional_feedback() {
        string directory = Path.Combine(Path.GetTempPath(), "history-self-contained-" + Guid.NewGuid().ToString("N"));
        string path = PlaygroundHostJournal.JournalPath(directory);
        string raw = Path.Combine(directory, "missing-raw.jsonl");
        string feedback = Path.Combine(directory, "feedback.jsonl");
        try {
            var trace = Trace(LlmControllerTraceKind.Response, [
                new ActionBlock.TextReasoningBlock("readable reasoning", Origin),
                new ActionBlock.Text("visible response"),
                new ActionBlock.ToolCall(RawToolCall.FromText("console", "call", "look"))
            ]);
            using (var journal = PlaygroundHostJournal.Open(path)) {
                journal.Append("run-start", "run", "parent", null, null,
                    new { Actors = new[] { new { ActorId = "a", Controller = "LLM" } }, DetailedRecording = true, AuditPath = raw });
                journal.Append("character-material", "run", "parent", "a", 1, Material("frozen observation"));
                journal.Append("model-request", "run", "parent", "a", 1, ModelHistoryProjection.Request(trace, null, 1));
                journal.Append("model-response", "run", "parent", "a", 1, ModelHistoryProjection.Outcome(trace, 2));
                journal.Append("feedback-saved", "run", "parent", "a", 1, new { FeedbackId = "saved-feedback" });
                var operations = new PlaygroundOperationAudit(journal, "parent", "run");
                var receipt = operations.Receive("batch", "call", "a", 1, "console", "look", "O1");
                operations.Complete(receipt.OperationRecordId, PlaygroundOperationStatus.Succeeded, "frozen world response");
            }
            File.WriteAllText(feedback, "not-json\n");
            Assert.False(File.Exists(raw));
            byte[] before = File.ReadAllBytes(path);
            using var output = new StringWriter();

            CharacterHistoryExporter.Export(path, "parent", "a", output, feedback);
            string text = output.ToString();
            foreach (string evidence in new[] { "frozen observation", "readable reasoning", "visible response", "look", "frozen world response", "工具收据", "saved: F1" }) {
                Assert.Contains(evidence, text);
            }
            Assert.Contains("附属反馈文件不可读", text);
            Assert.Contains("原始模型审计未验证", text);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(raw));
        } finally {
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }

    [Fact]
    public void Child_prefix_excludes_source_request_parent_future_and_later_completion() {
        var receipt = new PlaygroundOperationReceipt("O1", "batch", "call", "a", 1, "console", "pending input");
        var requestTrace = Trace(LlmControllerTraceKind.Request);
        var boundary = new PlaygroundDecisionBoundary("parent", "source-S", "a", 1, 1, 1, 1, 0, 22);
        var snapshot = Snapshot(
            Start(1),
            Record(2, "operation-received", receipt),
            Record(3, "model-request", ModelHistoryProjection.Request(requestTrace, boundary, 1)),
            Record(4, "operation-completed", new { OperationRecordId = "O1", Completed = new PlaygroundOperationResult("call",
                PlaygroundOperationStatus.Succeeded, "parent-future-completion", "version", "event", false), Recovered = true }),
            Record(5, "character-material", Material("parent-future-material")),
            Record(6, "branch-fork", new { SourceBranchId = "parent", SourceStateCommitId = "source-S", SourceThroughSequence = 2,
                SourceRequestId = "request-1", SourceJournalId = "journal" }, branch: "child", actor: null, context: null),
            Start(7, "child", forkedFrom: "source-S"),
            Record(8, "character-material", Material("child material"), branch: "child"));

        string text = CharacterHistoryExporter.Render(snapshot, "child", "a");
        Assert.Contains("pending input", text);
        Assert.Contains("未见 Completed", text);
        Assert.Contains("child material", text);
        Assert.DoesNotContain("parent-future-completion", text);
        Assert.DoesNotContain("parent-future-material", text);
        Assert.DoesNotContain("### Sequence 3", text);
        Assert.DoesNotContain("### Sequence 4", text);
        Assert.True(text.IndexOf("### Sequence 2", StringComparison.Ordinal) < text.IndexOf("### Sequence 8", StringComparison.Ordinal));
    }

    [Fact]
    public void Fork_without_matching_journal_or_feedback_fork_never_guesses_an_ancestor_prefix() {
        var source = Start(1);
        var parent = Record(2, "character-material", Material("secret parent material"));
        var child = Start(4, "child", forkedFrom: "same-S");
        string noProof = CharacterHistoryExporter.Render(Snapshot(source, parent, child), "child", "a");
        Assert.DoesNotContain("secret parent material", noProof);
        Assert.Contains("无可证明的宿主日志切点", noProof);
        var invalid = Record(3, "branch-fork", new { SourceBranchId = "parent", SourceThroughSequence = 2,
            SourceJournalId = "different-journal", SourceRequestId = "missing" }, branch: "child", actor: null, context: null);
        string wrongJournal = CharacterHistoryExporter.Render(Snapshot(source, parent, invalid, child), "child", "a");
        Assert.DoesNotContain("secret parent material", wrongJournal);
        Assert.Contains("不能共同验证", wrongJournal);
    }

    [Fact]
    public void Multiple_outcomes_and_repeated_contexts_stay_in_sequence_with_safe_fences_and_carried_tail_labels() {
        var response = Trace(LlmControllerTraceKind.Response, [new ActionBlock.Text("text with ``` and ```` fences")]);
        var failure = response with { Kind = LlmControllerTraceKind.Failure, ProtocolError = "act-protocol-invalid" };
        var snapshot = Snapshot(Start(1),
            Record(2, "model-request", ModelHistoryProjection.Request(response, null, 1)),
            Record(3, "model-response", ModelHistoryProjection.Outcome(response, 2)),
            Record(4, "model-failure", ModelHistoryProjection.Outcome(failure, 3)),
            Record(5, "character-material", Material("later context") with { ContextSequence = 2 }, context: 2),
            Record(6, "character-material", Material("returned context")));

        string text = CharacterHistoryExporter.Render(snapshot, "parent", "a");
        Assert.Contains("`````text", text);
        Assert.Contains("Tail[0]", text);
        Assert.Contains("携带的既有内容 · Text", text);
        Assert.Contains("saved: F1", text);
        Assert.Contains("act-protocol-invalid", text);
        Assert.True(text.IndexOf("### Sequence 3", StringComparison.Ordinal) < text.IndexOf("### Sequence 4", StringComparison.Ordinal));
        Assert.True(text.IndexOf("later context", StringComparison.Ordinal) < text.IndexOf("returned context", StringComparison.Ordinal));
        Assert.DoesNotContain("完整原始 Payload", text);
    }

    [Fact]
    public void Off_coverage_and_incomplete_tail_are_explicit_and_same_identity_text_changes_are_preserved() {
        var snapshot = Snapshot(Start(1, detailed: false),
            Record(2, "character-material", Material("observation", "first rendered item")),
            Record(3, "character-material", Material("observation", "first rendered item")),
            Record(4, "character-material", Material("observation", "changed rendered item"))) with { DiscardedTailBytes = 17 };

        string text = CharacterHistoryExporter.Render(snapshot, "parent", "a");
        Assert.Contains("详细人物记录 off", text);
        Assert.Contains("观察及模型阅读可能缺失", text);
        Assert.Contains("尾部 17 字节未完成", text);
        Assert.Contains("同一身份再次捕获，呈现变化", text);
        Assert.Equal(1, text.Split("first rendered item").Length - 1);
        Assert.Equal(1, text.Split("changed rendered item").Length - 1);
    }

    [Fact]
    public void Feedback_is_joined_only_through_visible_saved_ids_not_equal_world_or_context_boundaries() {
        var boundary = new PlaygroundDecisionBoundary("parent", "same-S", "a", 1, 1, 1, 1, 0, 22);
        var snapshot = Snapshot(Start(1),
            Record(2, "feedback-saved", new { FeedbackId = "visible-feedback" }),
            Record(3, "model-request", ModelHistoryProjection.Request(Trace(LlmControllerTraceKind.Request), boundary, 1)),
            Record(4, "feedback-saved", new { FeedbackId = "future-feedback" }),
            Record(5, "branch-fork", new { SourceBranchId = "parent", SourceStateCommitId = "same-S", SourceThroughSequence = 2,
                SourceRequestId = "request-1", SourceJournalId = "journal" }, branch: "child", actor: null, context: null),
            Start(6, "child", forkedFrom: "same-S"));
        FeedbackRecord[] reports = [
            new(1, "visible-feedback", DateTimeOffset.UtcNow, "run", "a", FeedbackSource.HumanConsole, 1,
                "visible feedback body", boundary, null, null),
            new(1, "future-feedback", DateTimeOffset.UtcNow, "run", "a", FeedbackSource.HumanConsole, 1,
                "parent-future-feedback-body", boundary, null, null)
        ];

        string text = CharacterHistoryExporter.Render(snapshot, "child", "a", reports);
        Assert.Contains("visible feedback body", text);
        Assert.DoesNotContain("parent-future-feedback-body", text);
        Assert.DoesNotContain("future-feedback", text);
    }

    [Fact]
    public void Unreadable_reasoning_and_unknown_block_show_gaps_without_dumping_unused_fields() {
        ModelOutcomeSnapshot outcome = ModelHistoryProjection.Outcome(Trace(LlmControllerTraceKind.Response, [
            new ActionBlock.OpaqueReasoningBlock("fixture-codec", Encoding.UTF8.GetBytes("secret native"), Origin)
        ]), 2);
        outcome = outcome with { Result = outcome.Result! with { Blocks = [.. outcome.Result!.Blocks,
            new ModelBlockSnapshot(1, "Unsupported", Text: "unrenderable-secret", ProjectionGap: "FutureImage")
        ] } };

        string text = CharacterHistoryExporter.Render(Snapshot(Start(1), Record(2, "model-response", outcome)), "parent", "a");
        Assert.Contains("没有可读 PlainText；不表示未思考", text);
        Assert.Contains("FutureImage", text);
        Assert.Contains("未转储原对象", text);
        Assert.DoesNotContain("unrenderable-secret", text);
        Assert.DoesNotContain("secret native", text);
        Assert.DoesNotContain("fixture-codec", text);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("run")]
    [InlineData("context")]
    [InlineData("branch")]
    [InlineData("missing-boundary")]
    public void Feedback_attachment_with_a_mismatched_identity_is_not_rendered(string mismatch) {
        var boundary = new PlaygroundDecisionBoundary("parent", "S1", "a", 1, 1, 1, 1, 0, 22);
        var report = new FeedbackRecord(1, "feedback-id", DateTimeOffset.UtcNow, "run", "a", FeedbackSource.HumanConsole,
            1, "wrong attachment body", boundary, null, null);
        report = mismatch switch {
            "actor" => report with { ActorId = "b" },
            "run" => report with { RunId = "different-run" },
            "context" => report with { ContextSequence = 2 },
            "branch" => report with { Boundary = boundary with { BranchId = "different-branch" } },
            _ => report with { Boundary = null }
        };
        var snapshot = Snapshot(Start(1), Record(2, "feedback-saved", new { FeedbackId = "feedback-id" }));

        string text = CharacterHistoryExporter.Render(snapshot, "parent", "a", [report]);
        Assert.DoesNotContain("wrong attachment body", text);
        Assert.Contains("身份不一致", text);
    }

    [Fact]
    public void Capture_world_position_is_displayed_separately_from_frozen_observation_and_context_must_match_envelope() {
        CharacterMaterialSnapshot material = Material("frozen observation at tick 14") with {
            LineageId = 3, TransitionCount = 8, TimeTicks = 200
        };
        var snapshot = Snapshot(Start(1), Record(2, "character-material", material));

        string text = CharacterHistoryExporter.Render(snapshot, "parent", "a");
        Assert.Contains("lineage 3；transition 8；tick 200", text);
        Assert.Contains("不是下方冻结 Observation 的观察时间", text);
        Assert.Contains("frozen observation at tick 14", text);
        var invalid = Snapshot(Start(1), Record(2, "character-material", material, context: 2));
        Assert.Throws<InvalidDataException>(() => CharacterHistoryExporter.Render(invalid, "parent", "a"));
    }

    private static readonly CompletionDescriptor Origin = new("fixture", "fixture-api", "fixture-model");
    private static LlmControllerTrace Trace(LlmControllerTraceKind kind, IReadOnlyList<ActionBlock>? blocks = null) =>
        new(kind, 1, "a", 1, new("fixture-model", new("system not projected", CompletionOutputContract.ProviderDefault([]),
            [new ObservationMessage("actual model observation")]), [
                new ActionMessage([new ActionBlock.Text("carried prior response")]),
                new ToolResultsMessage("tool wrapper", [ToolResult.FromText("feedback", "F-call", ToolExecutionStatus.Success, "saved: F1")])
            ]), blocks is null ? null : new(new(blocks), Origin), RequestId: "request-1", ConnectionId: "fixture-connection");
    private static CharacterMaterialSnapshot Material(string observation, string? itemText = null) =>
        new(1, "Open", "S1", observation, new("opening material", [], null),
            itemText is null ? [] : [new(1, 1, "item-operation", [1], itemText)]);
    private static PlaygroundHostRecord Start(long sequence, string branch = "parent", string? forkedFrom = null, bool detailed = true) =>
        Record(sequence, "run-start", new { Actors = new[] { new { ActorId = "a", Controller = "Human" } },
            DetailedRecording = detailed, StateCommitId = "S0", ForkedFromCommitId = forkedFrom, AuditPath = "raw-not-opened.jsonl" },
            branch, actor: null, context: null);
    private static PlaygroundHostRecord Record(long sequence, string kind, object payload, string branch = "parent",
        string? actor = "a", long? context = 1) => new(1, sequence, kind, "run", branch, actor, context,
            JsonSerializer.SerializeToElement(payload, payload.GetType()));
    private static PlaygroundHostJournalSnapshot Snapshot(params PlaygroundHostRecord[] records) => new("journal", records, 0);
}
