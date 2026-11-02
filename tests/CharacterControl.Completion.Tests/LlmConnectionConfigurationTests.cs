using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class LlmConnectionConfigurationTests {
    [Fact]
    public async Task Frozen_routes_share_one_lazy_client_and_unused_environment_is_not_resolved() {
        string missing = "DRAMABOARD_UNUSED_" + Guid.NewGuid().ToString("N");
        using var file = Configuration(Connection("main"), Connection("unused", missing));
        var config = LlmConnectionConfiguration.Load(file.Path);
        Assert.Equal("main", config.DefaultConnectionId);
        Assert.Equal(["main", "unused"], config.ConnectionIds);
        File.WriteAllText(file.Path, "invalid after snapshot");
        var factory = new Factory();
        var pool = new LlmConnectionPool(config, factory);
        Assert.Equal(0, factory.Created);
        LlmDecisionBinding first = await pool.GetAsync(config.DefaultConnectionId);
        Assert.Same(first, await pool.GetAsync("main"));
        Assert.Equal("main", first.ConnectionId);
        Assert.Equal("model-main", first.ModelId);
        Assert.Equal(1, factory.Created);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.GetAsync("unused"));
        Assert.Equal(1, factory.Created);
        await pool.DisposeAsync();
        await pool.DisposeAsync();
        Assert.Equal(1, factory.Client.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.GetAsync("main"));
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"defaultConnectionId\":\"main\",\"catalog\":CATALOG}")]
    [InlineData("{\"version\":1.0,\"defaultConnectionId\":\"main\",\"catalog\":CATALOG}")]
    [InlineData("{\"version\":1,\"defaultConnectionId\":\"main\",\"catalog\":CATALOG,\"extra\":true}")]
    public void Rejects_noncanonical_or_duplicate_host_fields(string json) {
        string catalog = JsonSerializer.Serialize(new { v = 3, connections = new[] { Connection("main") } });
        using var file = new JsonFile(json.Replace("CATALOG", catalog, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => LlmConnectionConfiguration.Load(file.Path));
    }

    [Fact]
    public async Task Ids_are_ordinal_and_precancelled_selection_does_not_create_clients() {
        using var file = Configuration(Connection("main"), Connection("Main"));
        var config = LlmConnectionConfiguration.Load(file.Path);
        var factory = new Factory();
        await using var pool = new LlmConnectionPool(config, factory);
        using var source = new CancellationTokenSource(); source.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.GetAsync("main", source.Token));
        Assert.Equal(source.Token, error.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => pool.GetAsync("MAIN"));
        Assert.Equal(0, factory.Created);
        Assert.Equal(2, config.ConnectionIds.Count);
    }

    [Fact]
    public async Task Distinct_connection_ids_are_cached_separately_and_shared_factory_clients_dispose_once() {
        using var file = Configuration(Connection("main"), Connection("other"));
        var factory = new Factory();
        var pool = new LlmConnectionPool(LlmConnectionConfiguration.Load(file.Path), factory);
        var first = await pool.GetAsync("main");
        var second = await pool.GetAsync("other");
        Assert.NotSame(first, second);
        Assert.Equal("model-other", second.ModelId);
        Assert.Same(second, await pool.GetAsync("other"));
        Assert.Equal(2, factory.Created);
        await pool.DisposeAsync();
        Assert.Equal(1, factory.Client.Disposed);
    }

    [Fact]
    public void Missing_default_is_rejected_without_falling_back_to_first_connection() {
        using var file = Configuration(Connection("other"));
        Assert.Throws<InvalidDataException>(() => LlmConnectionConfiguration.Load(file.Path));
    }

    [Fact]
    public void Unused_catalog_entries_still_receive_strict_validation() {
        using var file = Configuration(Connection("main"), Connection("main"));
        Assert.Throws<InvalidDataException>(() => LlmConnectionConfiguration.Load(file.Path));
    }

    private static object Connection(string id, string? env = null) => env is null
        ? new { id, kind = "openai-responses", modelId = "model-" + id, completionSurfaceId = "openai-responses", baseAddress = "https://fixture.invalid/" }
        : (object)new { id, kind = "openai-responses", modelId = "model-" + id, completionSurfaceId = "openai-responses", baseAddressEnv = env };
    private static JsonFile Configuration(params object[] connections) => new(JsonSerializer.Serialize(new {
        version = 1, defaultConnectionId = "main", catalog = new { v = 3, connections }
    }));
    private sealed class JsonFile : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        public JsonFile(string json) => File.WriteAllText(Path, json);
        public void Dispose() => File.Delete(Path);
    }
    private sealed class Factory : ICompletionClientFactory {
        public int Created { get; private set; }
        public Client Client { get; } = new();
        public ICompletionClient Create(CompletionConnectionConfig connection) { Created++; return Client; }
    }
    private sealed class Client : ICompletionClient, IDisposable {
        public int Disposed { get; private set; }
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposed++;
    }
}
