using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Disk-host routing evidence with offline clients, never online-model evidence.</summary>
public sealed class MultiConnectionHostTests {
    [Fact]
    public async Task Default_override_shared_budget_and_cold_rebind_record_actual_gym_identity() {
        using var files = new Files();
        var factory = new Factory();
        using var output = new StringWriter();
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options(), default, output,
            characterCount: 3, clientFactory: factory));
        Assert.Equal(new[] { "main", "other" }, factory.Created);
        Assert.Equal(new[] { "model-main", "model-other", "model-main" }, factory.Requests.Select(x => x.ModelId));
        var requests = DramaBoard.CharacterGym.GymJournal.Read(GymSession.JournalPath(files.Store)).Records
            .Where(record => record.Kind == "request").ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, requests.Select(x => x.ActorId));
        Assert.Equal(new[] { "main", "other", "main" }, requests.Select(x => x.Payload.GetProperty("ConnectionId").GetString()));
        Assert.Equal(new[] { "openai-responses-v2", "openai-chat-v1", "openai-responses-v2" },
            requests.Select(request => request.Payload.GetProperty("ApiSpecId").GetString()));
        // Binding changes on resume; no role binding is persisted in the game world.
        files.WriteOverrides(new { a = "other" });
        var resumed = new Factory();
        Assert.Equal(2, await LlmConsole.RunAsync(files.Store, files.Options() with { MaxCalls = 1 }, default,
            output, clientFactory: resumed));
        Assert.Equal("other", Assert.Single(resumed.Created));
    }

    [Fact]
    public async Task Human_dormant_override_and_unused_environment_connection_never_create_client() {
        using var files = new Files();
        files.WriteOverrides(new { a = "other" });
        var factory = new Factory();
        Assert.Equal(0, await LlmConsole.RunAsync(files.Store, files.Options() with { HumanActor = "a" }, default,
            new StringWriter(), characterCount: 3, inputReader: new StringReader(""), clientFactory: factory));
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Unknown_role_and_missing_explicit_configuration_fail_before_provider_creation() {
        using var files = new Files();
        files.WriteOverrides(new { unknown = "main" });
        var factory = new Factory();
        await Assert.ThrowsAsync<ArgumentException>(() => LlmConsole.RunAsync(files.Store, files.Options(), default,
            new StringWriter(), clientFactory: factory));
        Assert.False(Directory.Exists(files.Store));
        files.WriteOverrides(new { a = "main" });
        await Assert.ThrowsAsync<ArgumentException>(() => LlmConsole.RunAsync(files.Store,
            files.Options() with { HumanActor = "unknown" }, default, new StringWriter(), clientFactory: factory));
        Assert.False(Directory.Exists(files.Store));
        await Assert.ThrowsAsync<FileNotFoundException>(() => LlmConsole.RunAsync(files.Store,
            files.Options() with { ConnectionsPath = Path.Combine(files.Root, "missing.json") }, default,
            new StringWriter(), clientFactory: factory));
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Precancelled_run_does_not_read_configuration_or_create_store() {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new Factory();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LlmConsole.RunAsync(files.Store,
            files.Options() with { ConnectionsPath = Path.Combine(files.Root, "missing.json") }, cancellation.Token,
            new StringWriter(), clientFactory: factory));
        Assert.False(Directory.Exists(files.Store));
        Assert.Empty(factory.Created);
    }

    private sealed class Factory : ICompletionClientFactory {
        public List<string> Created { get; } = [];
        public List<CompletionRequest> Requests { get; } = [];
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Created.Add(connection.Id);
            return new Client(Requests, connection.Kind == "openai-chat" ? "openai-chat-v1" : "openai-responses-v2");
        }
    }
    private sealed class Client(List<CompletionRequest> requests, string apiSpecId) : ICompletionClient {
        public string Name => "offline-routing-fixture";
        public string ApiSpecId => apiSpecId;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            requests.Add(request);
            return Task.FromResult(new CompletionResult(new ActionMessage([
                new ActionBlock.ToolCall(new("act", Guid.NewGuid().ToString("N"), "{\"command\":\"handoff routing-fixture\"}"))
            ]), new("fixture", ApiSpecId, request.ModelId)));
        }
    }
    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "drama-routing-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "game");
        private string Connections => Path.Combine(Root, "connections.json");
        private string Overrides => Path.Combine(Root, "overrides.json");
        public Files() {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Connections, JsonSerializer.Serialize(new {
                version = 1, defaultConnectionId = "main", catalog = new { v = 3, connections = new object[] {
                    new { id = "main", kind = "openai-responses", completionSurfaceId = "openai-responses", modelId = "model-main", baseAddress = "https://fixture.invalid/" },
                    new { id = "other", kind = "openai-chat", completionSurfaceId = "openai-chat/strict", modelId = "model-other", baseAddress = "https://fixture.invalid/" },
                    new { id = "unused", kind = "openai-responses", completionSurfaceId = "openai-responses", modelId = "model-unused", baseAddressEnv = "DRAMA_ROUTING_INTENTIONALLY_MISSING_ENV" }
                } }
            }));
            WriteOverrides(new { b = "other" });
        }
        public void WriteOverrides(object overrides) => File.WriteAllText(Overrides, JsonSerializer.Serialize(new { version = 1, overrides }));
        public LlmHostOptions Options() => new(MaxCalls: 3, ConnectionsPath: Connections, CharacterConnectionsPath: Overrides,
            Gym: true, LogPath: Path.Combine(Root, Guid.NewGuid().ToString("N") + ".jsonl"));
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, true); } }
    }
}
