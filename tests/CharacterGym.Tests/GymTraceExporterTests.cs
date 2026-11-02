using System.Text;
using System.Text.Json;

namespace DramaBoard.CharacterGym.Tests;

/// <summary>Hand-authored journal fixtures verify presentation contracts; these are not real model traces.</summary>
public sealed class GymTraceExporterTests {
    [Fact]
    public void RequestPreservesFullPayloadAndProvidesReadableTextWithSafeFences() {
        GymRecord request = Record(1, "request", new {
            RequestId = "q1", Boundary = new { StateCommitId = "s1" },
            SystemPrompt = "系统\n````\n原始文本", OutputContract = "one act",
            Shared = new[] { new { Role = "User", Content = "角色材料\n下一行", Extra = "preserve-me" } },
            Tail = new[] { new { Content = "局部工具收据" } }, FutureField = new { Data = 42 },
        });
        string text = Render(request);
        Assert.Contains("系统\n````\n原始文本", text);
        Assert.Contains("`````text", text);
        Assert.Contains("角色材料\n下一行", text);
        Assert.Contains("局部工具收据", text);
        Assert.Contains("\"Extra\": \"preserve-me\"", text);
        Assert.Contains("\"FutureField\"", text);
        Assert.Contains("\"StateCommitId\": \"s1\"", text);
        Assert.Contains("未见响应", text);
    }

    [Fact]
    public void ActorFilterExcludesOtherPrivateAndUnclassifiedHostPayloads() {
        string text = Render(
            Record(1, "run-start", new { Public = "public-config" }, actor: null),
            Record(2, "request", new { RequestId = "a-private", Secret = "a-secret" }, actor: "a"),
            Record(3, "perception", new { Material = "b-visible" }),
            Record(4, "terminal", new { Result = "a-terminal-secret" }, actor: "a"),
            Record(5, "internal-diagnostic", new { Secret = "unclassified-secret" }, actor: null),
            Record(6, "run-end", new { Failure = "public-failure" }, actor: null));
        Assert.Contains("public-config", text);
        Assert.Contains("public-failure", text);
        Assert.Contains("b-visible", text);
        Assert.Contains("不是实际模型输入", text);
        Assert.DoesNotContain("a-secret", text);
        Assert.DoesNotContain("a-terminal-secret", text);
        Assert.DoesNotContain("unclassified-secret", text);
    }

    [Fact]
    public void NestedForksIncludeOnlyRecordedAncestorPrefixesAndSourceReferences() {
        GymRecord[] records = [
            Record(1, "perception", new { Material = "grand-prefix" }, branch: "grand"),
            Record(2, "request", new { RequestId = "source-q", Shared = "grand-future-secret" }, branch: "grand"),
            Record(3, "branch-fork", new { SourceBranchId = "grand", SourceStateCommitId = "s1", SourceThroughSequence = 1, SourceRequestId = "source-q" }, "parent", null),
            Record(4, "perception", new { Material = "parent-prefix" }, branch: "parent"),
            Record(5, "feedback", new { FeedbackId = "source-f", Content = "parent-future-secret" }, branch: "parent"),
            Record(6, "branch-fork", new { SourceBranchId = "parent", SourceStateCommitId = "s4", SourceThroughSequence = 4, SourceFeedbackId = "source-f" }, "child", null),
            Record(7, "perception", new { Material = "child-own" }, branch: "child"),
            Record(8, "perception", new { Material = "sibling-secret" }, branch: "sibling"),
        ];
        string text = GymTraceExporter.Render(new("store", records, 0), "child", "b");
        Assert.Contains("grand-prefix", text);
        Assert.Contains("parent-prefix", text);
        Assert.Contains("child-own", text);
        Assert.Contains("source-q", text);
        Assert.Contains("source-f", text);
        Assert.DoesNotContain("grand-future-secret", text);
        Assert.DoesNotContain("parent-future-secret", text);
        Assert.DoesNotContain("sibling-secret", text);
        Assert.True(text.IndexOf("grand-prefix", StringComparison.Ordinal) < text.IndexOf("parent-prefix", StringComparison.Ordinal));
        Assert.True(text.IndexOf("parent-prefix", StringComparison.Ordinal) < text.IndexOf("child-own", StringComparison.Ordinal));
    }

    [Fact]
    public void UnpairedRecordsAreGapsNotClaimsOfUnexecutedEffects() {
        string text = Render(
            Record(1, "request", new { RequestId = "no-response" }),
            Record(2, "response", new { RequestId = "missing-request", Operation = new { OperationRecordId = "op-unseen", RawInput = "wait 1" } }),
            Record(3, "feedback", new { FeedbackId = "f1", RequestId = "missing-request", Content = "obstacle" }),
            Record(4, "run-start", new { Build = "fixture" }, actor: null));
        Assert.Contains("请求记录 1 未见响应", text);
        Assert.Contains("响应记录 2 未见对应请求", text);
        Assert.Contains("未见可配对结果", text);
        Assert.Contains("不能据此判断世界未执行", text);
        Assert.Contains("未见回执", text);
        Assert.Contains("未见 run-end", text);
    }

    [Fact]
    public void OperationIdPairsRecoveredCommitAndFeedbackReceiptDoesNotImplyGameCommit() {
        string text = Render(
            Record(1, "request", new { RequestId = "q1" }),
            Record(2, "response", new { RequestId = "q1", Operation = new { OperationRecordId = "op1" } }),
            Record(3, "operation", new { OperationRecordId = "op1", Result = new {
                ToolCallId = "call1", Status = "Rejected", Result = "Rejected", WorldVersion = "world:2",
                DomainEvent = "event:2", ExactResult = false
            }, StateCommitId = "s2" }),
            Record(4, "feedback", new { FeedbackId = "f1", RequestId = "q1" }),
            Record(5, "feedback-receipt", new { FeedbackId = "f1", Accepted = true }));
        Assert.DoesNotContain("未见可配对结果", text);
        Assert.DoesNotContain("未见回执", text);
        Assert.Contains("Rejected", text);
        Assert.Contains("含领域事件引用", text);
        Assert.Contains("恢复补录：ExactResult=false", text);
    }

    [Fact]
    public void IndependentFeedbackReceiptDoesNotRequireFeedbackOrRequestInGym() {
        string text = Render(Record(1, "feedback-receipt", new { FeedbackId = "external-feedback", Accepted = true }));
        Assert.Contains("external-feedback", text);
        Assert.DoesNotContain("未见对应请求", text);
        Assert.DoesNotContain("未见回执", text);
        string legacy = Render(Record(1, "feedback", new { FeedbackId = "f", Text = "independent" }),
            Record(2, "feedback-receipt", new { FeedbackId = "f", Accepted = true }));
        Assert.DoesNotContain("未见对应请求", legacy);
    }

    [Fact]
    public void InterruptedHostResultPairsIdentityWithoutProvingGameCommit() {
        var receipt = new { OperationRecordId = "op1", BatchId = "batch", ToolCallId = "call1", ActorId = "b",
            ContextSequence = 1, ToolName = "act", RawInput = "wait 1" };
        var interrupted = new { ToolCallId = "call1", Status = "Interrupted", Result = "host interrupted",
            WorldVersion = (string?)null, DomainEvent = (string?)null, ExactResult = true };
        string text = Render(
            Record(1, "request", new { RequestId = "q1" }),
            Record(2, "response", new { RequestId = "q1", Operation = new { OperationRecordId = "op1" } }),
            Record(3, "operation", new { receipt.OperationRecordId, Received = receipt, Result = interrupted, StateCommitId = "s1" }));
        Assert.DoesNotContain("未见可配对结果", text);
        Assert.Contains("Interrupted，未证实游戏提交", text);
        Assert.DoesNotContain("含领域事件引用", text);
    }

    [Fact]
    public void LegacyV1InterruptedStatusRemainsReadableWithoutGameEnum() {
        string text = Render(Record(1, "operation", new {
            OperationRecordId = "old-op", Result = new { Status = 2, DomainEvent = (string?)null }
        }));
        Assert.Contains("Interrupted，未证实游戏提交", text);
        Assert.DoesNotContain("含领域事件引用", text);
    }

    [Fact]
    public void SuccessfulConsoleQueryResultIsNotPresentedAsGameFact() {
        string text = Render(Record(1, "operation", new {
            OperationRecordId = "query", StateCommitId = "s1",
            Result = new { ToolCallId = "call", Status = "Succeeded", Result = "help text", WorldVersion = "world:1",
                DomainEvent = (string?)null, ExactResult = true },
        }));
        Assert.Contains("没有领域事件引用，不作为游戏提交证明", text);
        Assert.DoesNotContain("恢复补录", text);
    }

    [Fact]
    public void MissingForkEvidenceIsVisibleAndDoesNotGuessParent() {
        string text = Render(
            Record(1, "perception", new { Material = "unrelated-parent-secret" }, branch: "other"),
            Record(2, "run-start", new { ForkedFromCommitId = "some-old-s" }, actor: null),
            Record(3, "perception", new { Material = "own-material" }));
        Assert.Contains("缺少 branch-fork 证据", text);
        Assert.Contains("own-material", text);
        Assert.DoesNotContain("unrelated-parent-secret", text);
    }

    [Fact]
    public void ForkWithMissingAncestorAndInvalidCutReportsEvidenceGap() {
        string missing = Render(Record(4, "branch-fork", new { SourceBranchId = "absent", SourceStateCommitId = "s", SourceThroughSequence = 2 }, actor: null));
        Assert.Contains("祖先证据可能缺失", missing);
        string invalid = Render(Record(4, "branch-fork", new { SourceBranchId = "root", SourceStateCommitId = "s", SourceThroughSequence = 10 }, actor: null));
        Assert.Contains("缺少有效来源分支/历史切点", invalid);
    }

    [Fact]
    public void ExportReadsWithoutRepairingIncompleteTailOrChangingFileBytes() {
        string directory = Path.Combine(Path.GetTempPath(), "drama-gym-export-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "gym.jsonl");
        try {
            using (var journal = GymJournal.Open(path)) {
                journal.Append("perception", "run", "root", "b", new { Material = "fixture-visible" });
            }
            File.AppendAllText(path, "{unfinished-fixture", new UTF8Encoding(false));
            byte[] before = File.ReadAllBytes(path);
            using var writer = new StringWriter();
            GymTraceExporter.Export(path, "root", "b", writer);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Contains("不完整记录", writer.ToString());
            Assert.Contains("fixture-visible", writer.ToString());
        } finally {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    private static GymRecord Record(long sequence, string kind, object payload, string branch = "root", string? actor = "b") =>
        new(1, sequence, kind, "run", branch, actor, JsonSerializer.SerializeToElement(payload));

    private static string Render(params GymRecord[] records) => GymTraceExporter.Render(new("fixture-store", records, 0), "root", "b");
}
