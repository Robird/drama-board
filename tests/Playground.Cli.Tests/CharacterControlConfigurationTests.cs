using System.Text.Json;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class CharacterControlConfigurationTests {
    [Fact]
    public void Empty_and_empty_overrides_resolve_all_actors_to_default_llm() {
        using var files = new Files("""{"version":1,"overrides":{}}""");
        foreach (var configuration in new[] { CharacterControlConfiguration.Empty, files.Load() }) {
            var bindings = configuration.Resolve(["a", "b"], files.Connections.DefaultConnectionId);
            Assert.Equal(new CharacterControlBinding.Llm("main"), bindings["a"]);
            Assert.Equal(new CharacterControlBinding.Llm("main"), bindings["b"]);
            Assert.False(bindings is IDictionary<string, CharacterControlBinding> mutable && !mutable.IsReadOnly);
        }
    }

    [Fact]
    public void Mixed_bindings_are_frozen_and_exact_case_without_resolving_environment() {
        using var files = new Files("""{"version":1,"overrides":{"a":{"kind":"human-console"},"A":{"kind":"llm","connectionId":"Other"}}}""");
        var config = files.Load();
        File.WriteAllText(files.ControlsPath, "invalid");
        var bindings = config.Resolve(["a", "A", "b"], "main");
        Assert.IsType<CharacterControlBinding.HumanConsole>(bindings["a"]);
        Assert.Equal(new CharacterControlBinding.Llm("Other"), bindings["A"]);
        Assert.Equal(new CharacterControlBinding.Llm("main"), bindings["b"]);
        Assert.Equal(3, bindings.Count);
        Assert.Throws<InvalidDataException>(() => config.Resolve(["a", "b"], "main"));
    }

    [Fact]
    public void Programmatic_overrides_are_validated_and_snapshotted() {
        var source = new Dictionary<string, CharacterControlBinding> {
            ["a"] = new CharacterControlBinding.HumanConsole()
        };
        var config = CharacterControlConfiguration.Create(source);
        source["a"] = new CharacterControlBinding.Llm("main");
        Assert.IsType<CharacterControlBinding.HumanConsole>(config.Resolve(["a"], "main")["a"]);
        Assert.Throws<InvalidDataException>(() => CharacterControlConfiguration.Create(new Dictionary<string, CharacterControlBinding> {
            ["a"] = new CharacterControlBinding.Llm("")
        }));
        Assert.Throws<InvalidDataException>(() => CharacterControlConfiguration.Create(new Dictionary<string, CharacterControlBinding> {
            ["a"] = null!
        }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":1.0,\"overrides\":{}}")]
    [InlineData("{\"version\":2,\"overrides\":{}}")]
    [InlineData("{\"Version\":1,\"overrides\":{}}")]
    [InlineData("{\"version\":1,\"version\":1,\"overrides\":{}}")]
    [InlineData("{\"version\":1,\"overrides\":{},\"defaultConnectionId\":\"main\"}")]
    [InlineData("{\"version\":1,\"overrides\":null}")]
    [InlineData("{\"version\":1,\"overrides\":[]}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":null}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":\"main\"}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"human-console\",\"connectionId\":\"main\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"llm\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"LLM\",\"connectionId\":\"main\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"llm\",\"connectionId\":\"other\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"llm\",\"connectionId\":null}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"llm\",\"connectionId\":\" \"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"human-console\",\"extra\":0}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"human-console\",\"kind\":\"human-console\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\"a\":{\"kind\":\"human-console\"},\"a\":{\"kind\":\"human-console\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{\" \":{\"kind\":\"human-console\"}}}")]
    [InlineData("{\"version\":1,\"overrides\":{},}")]
    public void Rejects_invalid_shapes_versions_duplicates_and_connection_references(string json) {
        using var files = new Files(json);
        Assert.Throws<InvalidDataException>(() => files.Load());
    }

    [Fact]
    public void Unknown_actor_and_case_mismatch_are_rejected_only_when_world_is_known() {
        using var files = new Files("""{"version":1,"overrides":{"A":{"kind":"human-console"}}}""");
        var config = files.Load();
        Assert.Throws<InvalidDataException>(() => config.Resolve(["a"], "main"));
        Assert.IsType<CharacterControlBinding.HumanConsole>(config.Resolve(["A"], "main")["A"]);
        Assert.Throws<InvalidDataException>(() => config.Resolve(["A", "A"], "main"));
    }

    [Fact]
    public void Explicit_missing_file_and_non_utf8_input_fail() {
        using var files = new Files("{}");
        File.Delete(files.ControlsPath);
        Assert.Throws<FileNotFoundException>(() => files.Load());
        File.WriteAllBytes(files.ControlsPath, [0xff, 0xfe]);
        Assert.Throws<InvalidDataException>(() => files.Load());
        File.WriteAllText(files.ControlsPath, "");
        Assert.Throws<InvalidDataException>(() => files.Load());
    }

    private sealed class Files : IDisposable {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "control-config-" + Guid.NewGuid().ToString("N"));
        internal string ControlsPath => Path.Combine(_directory, "controls.json");
        internal LlmConnectionConfiguration Connections { get; }
        internal Files(string controls) {
            Directory.CreateDirectory(_directory);
            string connectionsPath = Path.Combine(_directory, "connections.json");
            File.WriteAllText(connectionsPath, JsonSerializer.Serialize(new {
                version = 1, defaultConnectionId = "main", catalog = new { v = 3, connections = new[] { Connection("main"), Connection("Other") } }
            }));
            Connections = LlmConnectionConfiguration.Load(connectionsPath);
            File.WriteAllText(ControlsPath, controls);
        }
        private static object Connection(string id) => new {
            id, kind = "openai-responses", modelId = "fixture", completionSurfaceId = "openai-responses",
            baseAddressEnv = "DRAMABOARD_NOT_RESOLVED_" + Guid.NewGuid().ToString("N")
        };
        internal CharacterControlConfiguration Load() => CharacterControlConfiguration.Load(ControlsPath, Connections);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
