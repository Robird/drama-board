namespace DramaBoard.Playground.Cli.Tests;

public sealed class PlaygroundHomeTests {
    [Fact]
    public void Init_creates_only_manifest_and_never_overwrites() {
        WithDirectory(path => {
            var output = new StringWriter();
            PlaygroundHome.Initialize(path, output);
            Assert.Contains("准备默认连接", output.ToString());
            Assert.Equal(new[] { "playground.json" }, Directory.GetFiles(path).Select(Path.GetFileName));
            Assert.False(Directory.Exists(Path.Combine(path, "store")));
            Assert.Equal(128, PlaygroundHome.Load(path).MaxCalls);
            Assert.Throws<IOException>(() => PlaygroundHome.Initialize(path, output));
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"unknown\":0}")]
    [InlineData("{\"version\":1,\"maxCalls\":\"1\"}")]
    [InlineData("{\"version\":1,\"steps\":0}")]
    [InlineData("{\"version\":1,\"gym\":\"invalid\"}")]
    [InlineData("{\"version\":1,\"gym\":\"feedback\"}")]
    [InlineData("{\"version\":1,\"store\":null}")]
    [InlineData("{\"version\":1,\"characterControls\":\" \"}")]
    public void Manifest_is_strict(string json) {
        WithDirectory(path => {
            File.WriteAllText(Path.Combine(path, "playground.json"), json);
            Assert.Throws<ArgumentException>(() => PlaygroundHome.Load(path));
        });
    }

    [Fact]
    public void Missing_manifest_does_not_search_parent() {
        WithDirectory(path => {
            PlaygroundHome.Initialize(path, TextWriter.Null);
            string child = Directory.CreateDirectory(Path.Combine(path, "child")).FullName;
            Assert.Throws<ArgumentException>(() => Options.Parse([], child).Resolve());
            Assert.True(Options.Parse(["--help"], child).Help);
        });
    }

    internal static void WithDirectory(Action<string> action) {
        string path = Path.Combine(Path.GetTempPath(), "playground-home-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, recursive: true); }
    }
}
