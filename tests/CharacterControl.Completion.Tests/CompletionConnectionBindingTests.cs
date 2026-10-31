using System.Text;
using System.Text.Json;

namespace DramaBoard.CharacterControl.Completion.Tests;

public sealed class CompletionConnectionBindingTests {
    [Fact]
    public async Task Selects_exact_connection_applies_model_override_and_does_not_construct_unused_clients() {
        using var catalog = new CatalogFile(Connection("selected"), new {
            id = "unsupported-unused", kind = "fixture-provider-with-no-factory", modelId = "unused-model",
            completionSurfaceId = "fixture-surface", baseAddress = "https://unused.invalid/"
        });
        await using CompletionConnectionBinding binding = await CompletionConnectionBinding.ConnectAsync(
            new(Model: "overridden-model", Catalog: catalog.Path, Connection: "selected"));
        Assert.Equal("selected", binding.ConnectionId);
        Assert.Equal("overridden-model", binding.Model);
        Assert.Equal("fixture.invalid", binding.Client.Name);
        Assert.Same(binding.Client, binding.Client);
        // Constructing a client creates only local transport state; no completion request is sent.
    }

    [Fact]
    public async Task Unselected_environment_secrets_are_not_required_to_open_the_selected_connection() {
        string missingVariable = "DRAMABOARD_UNUSED_SECRET_" + Guid.NewGuid().ToString("N");
        Assert.Null(Environment.GetEnvironmentVariable(missingVariable));
        using var catalog = new CatalogFile(Connection("selected"), new {
            id = "unused", kind = "openai-responses", modelId = "unused-model",
            completionSurfaceId = "openai-responses", baseAddress = "https://unused.invalid/", apiKeyEnv = missingVariable
        });
        await using CompletionConnectionBinding binding = await CompletionConnectionBinding.ConnectAsync(
            new(Catalog: catalog.Path, Connection: "selected"));
        Assert.Equal("selected", binding.ConnectionId);
        Assert.Equal("fixture.invalid", binding.Client.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    public async Task Catalog_selection_never_silently_falls_back(string? requested) {
        using var catalog = new CatalogFile(Connection("selected"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => CompletionConnectionBinding.ConnectAsync(
            new(Catalog: catalog.Path, Connection: requested)));
        Assert.Contains("connection", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_option_combinations_are_rejected_before_file_or_credential_access(bool catalogAndAuth) {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.json");
        CompletionConnectionOptions options = catalogAndAuth
            ? new(AuthFile: missing, Catalog: missing, Connection: "selected")
            : new(AuthFile: missing, Connection: "selected");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => CompletionConnectionBinding.ConnectAsync(options));
        Assert.Contains("catalog", error.Message);
    }

    [Fact]
    public async Task Precancelled_binding_does_not_read_a_catalog_or_auth_file() {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.json");
        using var source = new CancellationTokenSource();
        source.Cancel();
        foreach (CompletionConnectionOptions options in new[] {
            new CompletionConnectionOptions(Catalog: missing, Connection: "selected"),
            new CompletionConnectionOptions(AuthFile: missing)
        }) {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CompletionConnectionBinding.ConnectAsync(options, source.Token));
            Assert.Equal(source.Token, error.CancellationToken);
        }
    }

    [Fact]
    public async Task Binding_owns_the_created_registry_and_rejects_client_access_after_disposal() {
        using var catalog = new CatalogFile(Connection("selected"));
        CompletionConnectionBinding binding = await CompletionConnectionBinding.ConnectAsync(new(Catalog: catalog.Path, Connection: "selected"));
        _ = binding.Client;
        await binding.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => binding.Client);
        await binding.DisposeAsync();
    }

    private static object Connection(string id) => new {
        id, kind = "openai-responses", modelId = "catalog-model", completionSurfaceId = "openai-responses",
        baseAddress = "https://fixture.invalid/"
    };

    private sealed class CatalogFile : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dramaboard-connection-tests", Guid.NewGuid().ToString("N") + ".json");
        public CatalogFile(params object[] connections) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(new { v = 3, connections }), new UTF8Encoding(false));
        }
        public void Dispose() => File.Delete(Path);
    }
}
