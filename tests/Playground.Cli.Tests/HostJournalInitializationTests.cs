using System.Security.Cryptography;
using System.Text;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Host storage-mode and initialization boundaries; these tests make no model calls.</summary>
public sealed class HostJournalInitializationTests {
    [Theory]
    [InlineData("operations.jsonl")]
    [InlineData("branch-operations/parent.jsonl")]
    [InlineData("gym/events.jsonl")]
    [InlineData("gym/events.v2.jsonl")]
    public async Task Legacy_host_artifacts_refuse_resume_without_touching_world_or_old_logs(string oldRelativePath) {
        using var files = new Files();
        files.CreateDomainWorld();
        string oldFile = Path.Combine(files.Store, oldRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
        File.WriteAllText(oldFile, "pending old host evidence\n", new UTF8Encoding(false));
        var before = Fingerprint(files.Store);
        using var output = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => PlaygroundHost.RunAsync(files.Store,
            files.Options with { ConnectionsPath = Path.Combine(files.Root, "must-not-load-connections.json") }, default,
            output, inputReader: new NeverRead()));
        Assert.Contains("旧宿主", error.Message);
        Assert.Contains("新的 store", error.Message);
        AssertFingerprint(before, Fingerprint(files.Store));
        Assert.False(File.Exists(PlaygroundHostJournal.JournalPath(files.Store)));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "host")));
        Assert.False(File.Exists(files.Raw));
    }

    [Fact]
    public async Task Corrupt_new_journal_is_not_treated_as_absent_or_rebuilt() {
        using var files = new Files();
        files.CreateDomainWorld();
        string path = PlaygroundHostJournal.JournalPath(files.Store);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "complete corrupt host line\n", new UTF8Encoding(false));
        var before = Fingerprint(files.Store);
        using var output = new StringWriter();
        await Assert.ThrowsAsync<InvalidDataException>(() => PlaygroundHost.RunAsync(files.Store, files.Options,
            default, output, inputReader: new NeverRead()));
        AssertFingerprint(before, Fingerprint(files.Store));
        Assert.Equal("complete corrupt host line\n", File.ReadAllText(path));
        Assert.False(File.Exists(files.Raw));
    }

    [Fact]
    public async Task Domain_only_store_starts_new_coverage_at_its_existing_completed_state() {
        using var files = new Files();
        string baseline = files.CreateDomainWorld();
        using var input = new StringReader("");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options, default, output, inputReader: input));
        var snapshot = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        var start = Assert.Single(snapshot.Records, record => record.Kind == "run-start");
        Assert.Equal(baseline, start.Payload.GetProperty("StateCommitId").GetString());
        Assert.True(start.Payload.GetProperty("DetailedRecording").GetBoolean());
        Assert.Contains("earlier external operations", start.Payload.GetProperty("Coverage").GetString());
        Assert.Contains(snapshot.Records, record => record.Kind == "character-material");
        Assert.DoesNotContain(snapshot.Records, record => record.Kind == "operation-received");
        Assert.False(File.Exists(files.Raw));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Durable_default_records_details_and_explicit_off_preserves_operation_audit(bool? mode, bool detailed) {
        using var files = new Files();
        using var input = new StringReader(":look\n:quit\n");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync(files.Store, files.Options with { Gym = mode }, default,
            output, characterCount: 1, inputReader: input));
        var snapshot = PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(files.Store));
        var start = Assert.Single(snapshot.Records, record => record.Kind == "run-start");
        Assert.Equal(detailed, start.Payload.GetProperty("DetailedRecording").GetBoolean());
        Assert.Single(snapshot.Records, record => record.Kind == "run-end");
        var received = snapshot.Records.Where(record => record.Kind == "operation-received").ToArray();
        var completed = snapshot.Records.Where(record => record.Kind == "operation-completed").ToArray();
        Assert.Equal(2, received.Length);
        Assert.Equal(2, completed.Length);
        Assert.Equal(received.Select(record => record.ReadPayload<PlaygroundOperationReceipt>().OperationRecordId),
            completed.Select(record => record.ReadPayload<PlaygroundOperationCompletion>().OperationRecordId));
        Assert.All(completed, record => Assert.True(record.ReadPayload<PlaygroundOperationCompletion>().Completed.ExactResult));
        Assert.Equal(detailed, snapshot.Records.Any(record => record.Kind == "character-material"));
        Assert.DoesNotContain(snapshot.Records, record => record.Kind.StartsWith("model-", StringComparison.Ordinal));
        Assert.False(File.Exists(files.Raw));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "llm-runs")));
        Assert.False(File.Exists(Path.Combine(files.Store, "operations.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "branch-operations")));
        Assert.False(Directory.Exists(Path.Combine(files.Store, "gym")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Ephemeral_default_and_off_use_only_in_memory_host_audit(bool? mode) {
        using var files = new Files();
        using var input = new StringReader(":look\n:quit\n");
        using var output = new StringWriter();
        Assert.Equal(0, await PlaygroundHost.RunAsync((string?)null, files.Options with { Gym = mode }, default,
            output, characterCount: 1, inputReader: input));
        Assert.Contains("(内存)", output.ToString());
        Assert.False(Directory.Exists(files.Store));
        Assert.False(Directory.Exists(Path.Combine(files.Home, "host")));
        Assert.False(File.Exists(files.Raw));
        Assert.False(Directory.Exists(Path.Combine(files.Home, "llm-runs")));
    }

    [Fact]
    public async Task Ephemeral_explicit_record_is_rejected_before_creating_any_files_or_reading_input() {
        using var files = new Files();
        using var output = new StringWriter();
        await Assert.ThrowsAsync<ArgumentException>(() => PlaygroundHost.RunAsync((string?)null,
            files.Options with { Gym = true }, default, output, characterCount: 1, inputReader: new NeverRead()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(files.Root));
    }

    private static Dictionary<string, string> Fingerprint(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void AssertFingerprint(Dictionary<string, string> before, Dictionary<string, string> after) {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var pair in before) { Assert.Equal(pair.Value, after[pair.Key]); }
    }

    private sealed class NeverRead : TextReader {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Initialization must fail before reading character input.");
    }

    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-host-init-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "world");
        public string Home => Path.Combine(Root, "home");
        public string Raw => Path.Combine(Home, "explicit-raw.jsonl");
        public PlaygroundHostOptions Options => new(Controls: HostFixture.Humans("a"), LogPath: Raw, HomePath: Home);
        public Files() => Directory.CreateDirectory(Root);
        public string CreateDomainWorld() {
            var initial = PlaygroundGameFactory.Create(PlaygroundWorldDefinitions.Named("gentle", characterCount: 1));
            using var history = PlaygroundDurableHistory.Create(Store, initial.World, initial.Cursor);
            return history.CurrentBranch.CompletedStateCommitId!;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
