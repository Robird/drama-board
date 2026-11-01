using System.Security.Cryptography;
using System.Text.Json;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class GymCommandsTests {
    [Theory]
    [InlineData("--gym")]
    [InlineData("--survival", "--llm", "--feedback")]
    [InlineData("--survival", "--store", "somewhere", "--gym")]
    [InlineData("--branch", "01000000")]
    [InlineData("--store", "somewhere", "--branch", "not-an-id")]
    [InlineData("--branches")]
    [InlineData("--store", "somewhere", "--branches", "--survival")]
    [InlineData("--store", "somewhere", "--branches", "--gym-feedbacks")]
    [InlineData("--store", "somewhere", "--gym-export", "out.md")]
    [InlineData("--store", "somewhere", "--gym-feedbacks", "--model", "anything")]
    [InlineData("--store", "somewhere", "--gym-fork", "feedback")]
    [InlineData("--store", "somewhere", "--gym-fork-request", "request", "--fork-label", "label", "--actor", "a")]
    [InlineData("--survival", "--actor", "a")]
    [InlineData("--store", "somewhere", "--fork-label", "orphan")]
    public void Options_reject_ambiguous_or_unbacked_modes(params string[] args) =>
        Assert.Throws<ArgumentException>(() => Options.Parse(args));

    [Fact]
    public void Feedback_implies_recording_and_branch_passes_to_model_host() {
        Options options = Options.Parse(["--duo", "--llm", "--store", "game", "--feedback", "--branch", "01000000"]);
        Assert.True(options.Llm!.Gym);
        Assert.True(options.Llm.Feedback);
        Assert.Equal(options.Branch, options.Llm.Branch);
        Assert.Equal(2, options.CharacterCount);
        Assert.Null(options.Management);
        Assert.Equal(GymManagementKind.Feedbacks,
            Options.Parse(["--store", "game", "--gym-feedbacks", "--actor", "b"]).Management!.Kind);
        Assert.Equal(GymManagementKind.ForkRequest,
            Options.Parse(["--store", "game", "--gym-fork-request", "r", "--fork-label", "test"]).Management!.Kind);
    }

    [Fact]
    public async Task Feedback_fork_uses_recorded_prestate_and_export_excludes_parent_future() {
        using var fixture = await Fixture.Create();
        using (var parent = CycleDurableHistory.Open(fixture.Store)) {
            var kernel = CycleEngine.CreateKernel(parent, characterController: new Command("use item.water"));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        CycleBranchInfo sourceBefore = Assert.Single(CycleDurableHistory.ListBranches(fixture.Store));
        using var messages = new StringWriter();
        GymCommands.Run(fixture.Store, null, new(GymManagementKind.ForkFeedback, SourceId: "feedback-1", ForkLabel: "alternate"), messages);
        CycleBranchInfo[] branches = CycleDurableHistory.ListBranches(fixture.Store).ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Equal(sourceBefore, branches.Single(branch => branch.BranchId == fixture.Boundary.BranchId));
        CycleBranchInfo child = branches.Single(branch => branch.BranchId != fixture.Boundary.BranchId);
        Assert.Equal(fixture.Boundary.StateCommitId, child.HeadCommitId);
        Assert.Contains(child.BranchId, messages.ToString());
        Assert.Contains("未打开或运行", messages.ToString());
        var fork = Assert.Single(GymJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
        Assert.Equal(fixture.RequestSequence - 1, fork.Payload.GetProperty("SourceThroughSequence").GetInt64());
        Assert.Equal("feedback-1", fork.Payload.GetProperty("SourceFeedbackId").GetString());

        string beforeRead = fixture.Fingerprint();
        string export = Path.Combine(fixture.Root, "child.md");
        GymCommands.Run(fixture.Store, child.BranchId, new(GymManagementKind.Export, "a", export), messages);
        string text = File.ReadAllText(export);
        Assert.Contains("prefix-visible", text);
        Assert.DoesNotContain("parent-future-secret", text);
        Assert.DoesNotContain("original-request-not-in-child", text);
        Assert.DoesNotContain("other-actor-secret", text);
        Assert.Equal(beforeRead, fixture.Fingerprint());
        using var opened = CycleDurableHistory.Open(fixture.Store, child.BranchId);
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
        Assert.Contains("feedback-1", output.ToString());
        Assert.Contains("不完整", output.ToString());
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
        var fork = Assert.Single(GymJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
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
            branch = CycleDurableHistory.Fork(fixture.Store, fixture.Boundary.BranchId,
                fixture.Boundary.StateCommitId, "a", "中文分支").Branch.BranchId;
            if (branch != branch.ToUpperInvariant()) { break; }
        }
        Assert.NotEqual(fixture.Boundary.BranchId, branch);
        Assert.NotEqual(branch, branch.ToUpperInvariant());
        using (var journal = GymJournal.Open(fixture.Journal)) {
            journal.Append("feedback", "fixture", branch, "a", new { FeedbackId = "case-feedback", Summary = "中文反馈" });
        }
        string before = fixture.Fingerprint();
        using var output = new StringWriter();
        GymCommands.Run(fixture.Store, branch.ToUpperInvariant(), new(GymManagementKind.Feedbacks, Actor: "a"), output);
        GymRecord selected = Assert.Single(output.ToString().Split('\n')
            .Where(line => line.StartsWith('{')).Select(line => JsonSerializer.Deserialize<GymRecord>(line)!));
        Assert.Equal("case-feedback", selected.Payload.GetProperty("FeedbackId").GetString());
        Assert.Equal(branch, selected.BranchId);
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
        using (var journal = GymJournal.Open(fixture.Journal)) {
            journal.Append("request", "fixture", fixture.Boundary.BranchId, "a", new {
                RequestId = "wrong-boundary", Boundary = fixture.Boundary with { OperationSequence = fixture.Boundary.OperationSequence + 1 }
            });
        }
        string before = fixture.Fingerprint();
        Assert.Throws<InvalidDataException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkRequest, SourceId: "wrong-boundary", ForkLabel: "must-not-exist"), new StringWriter()));
        Assert.Single(CycleDurableHistory.ListBranches(fixture.Store));
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task Registration_survives_log_writer_failure_and_reports_existing_branch_without_retry() {
        using var fixture = await Fixture.Create();
        using var heldWriter = GymJournal.Open(fixture.Journal);
        IOException error = Assert.Throws<IOException>(() => GymCommands.Run(fixture.Store, null,
            new(GymManagementKind.ForkFeedback, SourceId: "feedback-1", ForkLabel: "registered-despite-log-lock"), new StringWriter()));
        Assert.Contains("已注册", error.Message);
        Assert.Contains("不要盲目重试", error.Message);
        CycleBranchInfo[] branches = CycleDurableHistory.ListBranches(fixture.Store).ToArray();
        Assert.Equal(2, branches.Length);
        Assert.Contains(branches.Single(branch => branch.BranchId != fixture.Boundary.BranchId).BranchId, error.Message);
        Assert.DoesNotContain(GymJournal.Read(fixture.Journal).Records, record => record.Kind == "branch-fork");
    }

    private sealed class Command(string text) : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(Guid.NewGuid().ToString("N"), text, Guid.NewGuid().ToString("N")));
    }

    /// <summary>Recorded host evidence is a test fixture, not an actual model feedback claim.</summary>
    private sealed class Fixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-gym-management-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "game");
        public string Journal => GymSession.JournalPath(Store);
        public CycleDecisionBoundary Boundary { get; private set; } = null!;
        public long RequestSequence { get; private set; }

        public static async Task<Fixture> Create() {
            var result = new Fixture();
            Directory.CreateDirectory(result.Root);
            var initial = CycleGameFactory.CreateSurvival();
            var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
            var kernel = CycleEngine.CreateKernel(memory, characterController: new Command("wait 1"));
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            using (var history = CycleDurableHistory.Create(result.Store, kernel.World, kernel.Cursor)) {
                result.Boundary = history.CaptureDecisionBoundary("a");
            }
            using (var journal = GymJournal.Open(result.Journal)) {
                journal.Append("perception", "fixture", result.Boundary.BranchId, "a", new { Material = "prefix-visible" });
                journal.Append("perception", "fixture", result.Boundary.BranchId, "b", new { Material = "other-actor-secret" });
                GymRecord request = journal.Append("request", "fixture", result.Boundary.BranchId, "a", new {
                    RequestId = "request-1", Boundary = result.Boundary, Shared = "original-request-not-in-child"
                });
                result.RequestSequence = request.Sequence;
                journal.Append("feedback", "fixture", result.Boundary.BranchId, "a", new {
                    FeedbackId = "feedback-1", RequestId = "request-1", RequestSequence = request.Sequence,
                    Boundary = result.Boundary, Submission = new { Summary = "test feedback" }
                });
                journal.Append("perception", "fixture", result.Boundary.BranchId, "a", new { Material = "parent-future-secret" });
            }
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
