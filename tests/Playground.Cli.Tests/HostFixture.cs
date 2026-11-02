using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

internal static class HostFixture {
    internal static ResolvedOptions Resolve(string[] args) {
        string home = Path.Combine(Path.GetTempPath(), "drama-options-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try {
            File.WriteAllText(Path.Combine(home, "playground.json"), "{\"version\":1}");
            return Options.Parse(["--home", home, .. args]).Resolve();
        } finally { Directory.Delete(home, recursive: true); }
    }

    internal static CharacterControlConfiguration Humans(params string[] actorIds) =>
        CharacterControlConfiguration.Create(actorIds.ToDictionary(id => id,
            _ => (CharacterControlBinding)new CharacterControlBinding.HumanConsole(), StringComparer.Ordinal));

    internal static async Task<int> RunHumanAsync(string? store, CancellationToken cancellationToken,
        Action<PlaygroundDiagnosticRecord>? diagnosticSink = null, TextReader? inputReader = null, TextWriter? outputWriter = null,
        int? characterCount = null, string? branch = null, ModelTime? until = null, int maxSteps = 10_000, string? content = null) {
        string[] actors;
        if (store is not null && Directory.Exists(store)) {
            using var saved = branch is null ? PlaygroundDurableHistory.Open(store) : PlaygroundDurableHistory.Open(store, branch);
            actors = saved.State.Characters.Select(actor => actor.Id).ToArray();
        } else {
            actors = new[] { "a", "b", "c" }.Take(characterCount ?? 3).ToArray();
        }
        string? temporaryLog = store is null ? Path.Combine(Path.GetTempPath(), "drama-human-fixture-" + Guid.NewGuid().ToString("N") + ".jsonl") : null;
        try {
            return await PlaygroundHost.RunAsync(store, new PlaygroundHostOptions(Controls: Humans(actors), Branch: branch,
                Until: until, MaxSteps: maxSteps, Content: content, LogPath: temporaryLog), cancellationToken,
                outputWriter: outputWriter, inputReader: inputReader, characterCount: characterCount, diagnosticSink: diagnosticSink);
        } finally {
            if (temporaryLog is not null && File.Exists(temporaryLog)) { File.Delete(temporaryLog); }
        }
    }
}
