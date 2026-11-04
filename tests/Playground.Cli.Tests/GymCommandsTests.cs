using System.Security.Cryptography;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class GymCommandsTests {
    [Theory]
    [InlineData("--gym")]
    [InlineData("--llm", "--feedback")]
    [InlineData("--store", "somewhere", "--gym")]
    [InlineData("--feedback")]
    [InlineData("--store", "somewhere", "--branch", "not-an-id")]
    [InlineData("--branches", "--steps", "1")]
    [InlineData("--store", "somewhere", "--branches", "--llm")]
    [InlineData("--store", "somewhere", "--branches", "--feedbacks")]
    [InlineData("--store", "somewhere", "--gym-export", "out.md")]
    [InlineData("--store", "somewhere", "--feedbacks", "--model", "anything")]
    [InlineData("--store", "somewhere", "--fork-feedback", "feedback")]
    [InlineData("--store", "somewhere", "--gym-fork-request", "request", "--fork-label", "label", "--actor", "a")]
    [InlineData("--actor", "a")]
    [InlineData("--store", "somewhere", "--fork-label", "orphan")]
    public void Options_reject_ambiguous_or_unbacked_modes(params string[] args) =>
        Assert.Throws<ArgumentException>(() => Options.Parse(args));

    [Fact]
    public void Management_modes_and_branch_pass_to_host() {
        ResolvedOptions options = HostFixture.Resolve(["--characters", "2", "--llm-connections", "connections.json", "--store", "game", "--branch", "01000000"]);
        Assert.True(options.Host.Gym);
        Assert.Equal(options.Branch, options.Host.Branch);
        Assert.Equal(2, options.CharacterCount);
        Assert.Null(options.Management);
        Assert.Equal(GymManagementKind.Feedbacks,
            Options.Parse(["--store", "game", "--feedbacks", "--actor", "b"]).Resolve().Management!.Kind);
        Assert.Equal(GymManagementKind.ForkRequest,
            Options.Parse(["--store", "game", "--gym-fork-request", "r", "--fork-label", "test"]).Resolve().Management!.Kind);
    }

    [Fact]
    public async Task Request_fork_uses_recorded_prestate_and_export_excludes_parent_future() {
        using var fixture = await Fixture.Create();
        using (var parent = PlaygroundDurableHistory.Open(fixture.Store)) {
            var kernel = PlaygroundEngine.CreateKernel(parent, characterController: new Command("use item.water"));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        PlaygroundBranchInfo sourceBefore = Assert.Single(PlaygroundDurableHistory.ListBranches(fixture.Store));
        using var messages = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.ForkRequest, SourceId: "request-1", ForkLabel: "alternate"), messages);
        PlaygroundBranchInfo[] branches = PlaygroundDurableHistory.ListBranches(fixture.Store).ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Equal(sourceBefore, branches.Single(branch => branch.BranchId == fixture.Boundary.BranchId));
        PlaygroundBranchInfo child = branches.Single(branch => branch.BranchId != fixture.Boundary.BranchId);
        Assert.Equal(fixture.Boundary.StateCommitId, child.HeadCommitId);
        Assert.Contains(child.BranchId, messages.ToString());
        Assert.Contains("未打开或运行", messages.ToString());
        var fork = Assert.Single(PlaygroundHostJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
        Assert.Equal(fixture.RequestSequence - 1, fork.Payload.GetProperty("SourceThroughSequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, fork.Payload.GetProperty("SourceFeedbackId").ValueKind);

        string beforeRead = fixture.Fingerprint();
        string export = Path.Combine(fixture.Root, "child.md");
        GymCommands.Run(fixture.Store, child.BranchId, new(GymManagementKind.Export, "a", export), messages);
        string text = File.ReadAllText(export);
        Assert.Contains("prefix-visible", text);
        Assert.DoesNotContain("parent-future-secret", text);
        Assert.DoesNotContain("original-request-not-in-child", text);
        Assert.DoesNotContain("other-actor-secret", text);
        Assert.Equal(beforeRead, fixture.Fingerprint());
        using var opened = PlaygroundDurableHistory.Open(fixture.Store, child.BranchId);
        Assert.Equal(1, opened.State.GetCharacter("a").Backpack.Count("item.water"));
    }

    [Fact]
    public async Task Read_only_management_does_not_repair_tail_or_overwrite_export() {
        using var fixture = await Fixture.Create();
        File.AppendAllText(fixture.Journal, "incomplete-tail");
        string before = fixture.Fingerprint();
        using var output = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Branches), output);
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Feedbacks, Actor: "a"), output);
        Assert.Contains(fixture.FeedbackId, output.ToString());

        string export = Path.Combine(fixture.Root, "trace.md");
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Export, "a", export), output);
        byte[] saved = File.ReadAllBytes(export);
        Assert.Throws<IOException>(() => GymCommands.Run(fixture.Store, null, new(GymManagementKind.Export, "a", export), output));
        Assert.Equal(saved, File.ReadAllBytes(export));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Request_fork_is_supported_and_multibranch_export_requires_selection() {
        using var fixture = await Fixture.Create();
        using var output = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.ForkRequest, SourceId: "request-1", ForkLabel: "request-origin"), output);
        var fork = Assert.Single(PlaygroundHostJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, fork.Payload.GetProperty("SourceFeedbackId").ValueKind);
        Assert.Throws<ArgumentException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.Export, "a", Path.Combine(fixture.Root, "ambiguous.md")), output));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ambiguous.md")));
    }

    [Fact]
    public async Task Feedback_filter_normalizes_hex_but_preserves_actor_identity_and_readable_labels() {
        using var fixture = await Fixture.Create();
        string branch = fixture.Boundary.BranchId;
        // Always create a distinct branch; the initial ID can already contain hexadecimal letters.
        for (int i = 0; i < 16; i++) {
            branch = PlaygroundDurableHistory.Fork(fixture.Store, fixture.Boundary.BranchId,
                fixture.Boundary.StateCommitId, "a", "中文分支").Branch.BranchId;
            if (branch != branch.ToUpperInvariant()) { break; }
        }
        Assert.NotEqual(fixture.Boundary.BranchId, branch);
        Assert.NotEqual(branch, branch.ToUpperInvariant());
        using (var journal = FeedbackJournal.Open(FeedbackSession.JournalPath(fixture.Store))) {
            journal.Append(fixture.Feedback with { FeedbackId = Guid.NewGuid().ToString("N"),
                Boundary = fixture.Boundary with { BranchId = branch }, Text = "中文反馈" });
        }
        string before = fixture.Fingerprint();
        using var output = new StringWriter();
        GymCommands.Run(fixture.Store, branch.ToUpperInvariant(), new(GymManagementKind.Feedbacks, Actor: "a"), output);
        FeedbackRecord selected = Assert.Single(output.ToString().Split('\n')
            .Where(line => line.StartsWith('{')).Select(line => JsonSerializer.Deserialize<FeedbackRecord>(line)!));
        Assert.Equal("中文反馈", selected.Text);
        Assert.Equal(branch, selected.Boundary!.BranchId);
        Assert.Equal("a", selected.ActorId);
        Assert.Contains("中文反馈", output.ToString());
        using var wrongActor = new StringWriter();
        GymCommands.Run(fixture.Store, branch.ToUpperInvariant(), new(GymManagementKind.Feedbacks, Actor: "A"), wrongActor);
        Assert.DoesNotContain(wrongActor.ToString().Split('\n'), line => line.StartsWith('{'));
        using var branches = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Branches), branches);
        Assert.Contains("中文分支", branches.ToString());
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Full_boundary_mismatch_rejects_before_registration() {
        using var fixture = await Fixture.Create();
        using (var journal = PlaygroundHostJournal.Open(fixture.Journal)) {
            journal.Append("model-request", "fixture", fixture.Boundary.BranchId, "a", fixture.Boundary.ContextSequence,
                new ModelRequestSnapshot("wrong-boundary", 1, "fixture", "fixture-model", "fixture", null,
                    fixture.Boundary with { OperationSequence = fixture.Boundary.OperationSequence + 1 }, [], []));
        }
        string before = fixture.Fingerprint();
        Assert.Throws<InvalidDataException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkRequest, SourceId: "wrong-boundary", ForkLabel: "must-not-exist"), new StringWriter()));
        Assert.Single(PlaygroundDurableHistory.ListBranches(fixture.Store));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Registration_survives_log_writer_failure_and_reports_existing_branch_without_retry() {
        using var fixture = await Fixture.Create();
        using var heldWriter = PlaygroundHostJournal.Open(fixture.Journal);
        IOException error = Assert.Throws<IOException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkRequest, SourceId: "request-1", ForkLabel: "registered-despite-log-lock"), new StringWriter()));
        Assert.Contains("已注册", error.Message);
        Assert.Contains("不要盲目重试", error.Message);
        PlaygroundBranchInfo[] branches = PlaygroundDurableHistory.ListBranches(fixture.Store).ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Contains(branches.Single(branch => branch.BranchId != fixture.Boundary.BranchId).BranchId, error.Message);
        Assert.DoesNotContain(PlaygroundHostJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
    }

    [Fact]
    public async Task Independent_feedback_query_and_fork_need_no_gym_and_do_not_create_it() {
        using var fixture = await Fixture.Create();
        File.Delete(fixture.Journal);
        using var output = new StringWriter();
        string before = fixture.Fingerprint();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Feedbacks, Actor: "a"), output);
        FeedbackRecord record = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<FeedbackRecord>(line)!));
        Assert.Equal(fixture.FeedbackId, record.FeedbackId);
        Assert.Equal(before, fixture.Fingerprint());
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.ForkFeedback, SourceId: fixture.FeedbackId, ForkLabel: "human-feedback"), output);
        Assert.Equal(2, PlaygroundDurableHistory.ListBranches(fixture.Store).Count);
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public async Task Missing_feedback_file_query_is_empty_and_read_only() {
        using var fixture = await Fixture.Create();
        File.Delete(FeedbackSession.JournalPath(fixture.Store));
        File.Delete(fixture.Journal);
        string before = fixture.Fingerprint();
        using var output = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Feedbacks), output);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Feedback_query_keeps_jsonl_and_reports_incomplete_tail_only_to_diagnostics() {
        using var fixture = await Fixture.Create();
        using var cleanOutput = new StringWriter();
        using var cleanDiagnostics = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Feedbacks), cleanOutput, cleanDiagnostics);
        Assert.Equal(string.Empty, cleanDiagnostics.ToString());
        FeedbackRecord clean = Assert.Single(cleanOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<FeedbackRecord>(line)!));
        Assert.Equal(fixture.FeedbackId, clean.FeedbackId);

        const string tail = "{\"Text\":\"未完成";
        string feedbackPath = FeedbackSession.JournalPath(fixture.Store);
        File.AppendAllText(feedbackPath, tail, new System.Text.UTF8Encoding(false));
        string before = fixture.Fingerprint();
        byte[] beforeHash = SHA256.HashData(File.ReadAllBytes(feedbackPath));
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.Feedbacks), output, diagnostics);
        FeedbackRecord record = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<FeedbackRecord>(line)!));
        Assert.Equal(fixture.FeedbackId, record.FeedbackId);
        Assert.Equal(cleanOutput.ToString(), output.ToString());
        Assert.Contains($"{System.Text.Encoding.UTF8.GetByteCount(tail)} 字节", diagnostics.ToString());
        Assert.Contains("未修改原文件", diagnostics.ToString());
        Assert.Equal(beforeHash, SHA256.HashData(File.ReadAllBytes(feedbackPath)));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Independent_feedback_full_boundary_mismatch_rejects_before_registration() {
        using var fixture = await Fixture.Create();
        var record = fixture.Feedback with {
            FeedbackId = Guid.NewGuid().ToString("N"),
            Boundary = fixture.Boundary with { OperationSequence = fixture.Boundary.OperationSequence + 1 }
        };
        using (var journal = FeedbackJournal.Open(FeedbackSession.JournalPath(fixture.Store))) { journal.Append(record); }
        string before = fixture.Fingerprint();
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkFeedback, SourceId: record.FeedbackId, ForkLabel: "must-not-exist"), new StringWriter()));
        Assert.Contains("完整决定前状态不一致", error.Message);
        Assert.Single(PlaygroundDurableHistory.ListBranches(fixture.Store));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Feedback_without_boundary_cannot_fork() {
        using var fixture = await Fixture.Create();
        var record = fixture.Feedback with { FeedbackId = Guid.NewGuid().ToString("N"), Boundary = null };
        using (var journal = FeedbackJournal.Open(FeedbackSession.JournalPath(fixture.Store))) { journal.Append(record); }
        string before = fixture.Fingerprint();
        Assert.Throws<InvalidDataException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkFeedback, SourceId: record.FeedbackId, ForkLabel: "invalid"), new StringWriter()));
        Assert.Single(PlaygroundDurableHistory.ListBranches(fixture.Store));
        Assert.Equal(before, fixture.Fingerprint());
    }

    private sealed class Command(string text) : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlaygroundControllerOperation(Guid.NewGuid().ToString("N"), text, Guid.NewGuid().ToString("N")));
    }

    /// <summary>Recorded host evidence is a test fixture, not an actual model feedback claim.</summary>
    private sealed class Fixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-gym-management-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "game");
        public string Journal => PlaygroundHostJournal.JournalPath(Store);
        public PlaygroundDecisionBoundary Boundary { get; private set; } = null!;
        public long RequestSequence { get; private set; }
        public string FeedbackId => Feedback.FeedbackId;
        public FeedbackRecord Feedback { get; private set; } = null!;

        public static async Task<Fixture> Create() {
            var result = new Fixture();
            Directory.CreateDirectory(result.Root);
            var initial = PlaygroundGameFactory.CreateSurvival();
            var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
            var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new Command("wait 1"));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            using (var history = PlaygroundDurableHistory.Create(result.Store, kernel.World, kernel.Cursor)) {
                result.Boundary = history.CaptureDecisionBoundary("a");
            }
            using (var journal = PlaygroundHostJournal.Open(result.Journal)) {
                journal.Append("run-start", "fixture", result.Boundary.BranchId, null, null,
                    new { Actors = new[] { new { ActorId = "a" }, new { ActorId = "b" } }, DetailedRecording = true });
                var material = CharacterHistoryProjection.Capture(kernel.World.Business.InteractionStates["a"], result.Boundary.StateCommitId);
                journal.Append("character-material", "fixture", result.Boundary.BranchId, "a", result.Boundary.ContextSequence,
                    material with { ObservationText = "prefix-visible" });
                journal.Append("character-material", "fixture", result.Boundary.BranchId, "b", result.Boundary.ContextSequence,
                    material with { ObservationText = "other-actor-secret" });
                PlaygroundHostRecord request = journal.Append("model-request", "fixture", result.Boundary.BranchId, "a", result.Boundary.ContextSequence,
                    new ModelRequestSnapshot("request-1", 1, "fixture", "fixture-model", "fixture", null, result.Boundary,
                        [new("Shared", 0, "Observation", "original-request-not-in-child", [], [])], []));
                result.RequestSequence = request.Sequence;
                journal.Append("feedback-saved", "fixture", result.Boundary.BranchId, "a", result.Boundary.ContextSequence, new {
                    FeedbackId = "feedback-1", RequestId = "request-1", RequestSequence = request.Sequence,
                    Boundary = result.Boundary, Submission = new { Summary = "test feedback" }
                });
                journal.Append("character-material", "fixture", result.Boundary.BranchId, "a", result.Boundary.ContextSequence,
                    material with { ObservationText = "parent-future-secret" });
            }
            result.Feedback = new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "fixture", "a",
                FeedbackSource.HumanConsole, result.Boundary.ContextSequence, "test feedback", result.Boundary, null, result.Store);
            using (var feedback = FeedbackJournal.Open(FeedbackSession.JournalPath(result.Store))) { feedback.Append(result.Feedback); }
            return result;
        }

        public string Fingerprint() => string.Join("\n", Directory.GetFiles(Store, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Store, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

        public void Dispose() {
            string root = Path.GetFullPath(Root);
            string allowed = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "drama-gym-management-");
            if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Unexpected temporary directory."); }
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }
}
