using System.Diagnostics;
using System.Text.Json;
using DramaBoard.FirstBoard.Persistence.Process;

namespace DramaBoard.FirstBoard.Persistence.Tests;

public sealed class ColdProcessTests
{
    [Theory]
    [InlineData("Continue", 0)]
    [InlineData("Continue", 1)]
    [InlineData("Continue", 2)]
    [InlineData("Reverse", 0)]
    [InlineData("Reverse", 1)]
    [InlineData("Reverse", 2)]
    public async Task SeparateProcessReopenMatchesContinuousWorldCursorFactsAndNextRequest(string response, int splitAfter)
    {
        using var paths = new WitnessPaths();
        JsonElement expected = await RunAsync("create", paths.Path("continuous"), response, 2);
        JsonElement first = await RunAsync("create", paths.Path("split"), response, splitAfter);
        JsonElement reopened = await RunAsync("open", paths.Path("split"), response, 0);
        AssertBoundaryEqual(first, reopened);
        AssertNoReplay(reopened);

        JsonElement final = await RunAsync("open", paths.Path("split"), response, 2 - splitAfter);
        AssertBoundaryEqual(expected, final);
        Assert.Equal(Events(expected), Events(first).Concat(Events(final)).ToArray());
        AssertNoReplay(final);
        Assert.NotEqual(JsonValueKind.Null, final.GetProperty("NextRequest").ValueKind);
    }

    [Theory]
    [InlineData("Continue", 0)]
    [InlineData("Continue", 1)]
    [InlineData("Reverse", 0)]
    [InlineData("Reverse", 1)]
    public async Task SeparateProcessPendingRecoveryOnlyFoldsPublishedOccurrence(string response, int completedBeforeInterruption)
    {
        using var paths = new WitnessPaths();
        JsonElement expected = await RunAsync("create", paths.Path("continuous"), response, completedBeforeInterruption + 1);
        JsonElement interrupted = await RunAsync("pending", paths.Path("pending"), response, completedBeforeInterruption);
        Assert.NotEqual(JsonValueKind.Null, interrupted.GetProperty("Pending").ValueKind);
        JsonElement recovered = await RunAsync("open", paths.Path("pending"), response, 0);

        AssertBoundaryEqual(expected, recovered);
        Assert.True(recovered.GetProperty("Recovered").GetBoolean());
        Assert.True(recovered.GetProperty("PendingFactCount").GetInt32() > 0);
        Assert.Equal(recovered.GetProperty("PendingFactCount").GetInt32(), recovered.GetProperty("RecoveryFolds").GetInt32());
        Assert.Equal(0, recovered.GetProperty("RecoveryForecastCalls").GetInt32());
        Assert.Equal(0, recovered.GetProperty("RecoveryPlanCalls").GetInt32());
        Assert.Equal(Events(expected), Events(interrupted).Concat(Events(recovered)).ToArray());
        Assert.Equal(interrupted.GetProperty("Pending").GetRawText(), Assert.Single(Events(recovered)));

        JsonElement secondOpen = await RunAsync("open", paths.Path("pending"), response, 0);
        AssertBoundaryEqual(recovered, secondOpen);
        AssertNoReplay(secondOpen);
    }

    private static void AssertBoundaryEqual(JsonElement expected, JsonElement actual)
    {
        foreach (string name in new[] { "World", "Cursor", "NextRequest", "Pending" })
        {
            Assert.Equal(expected.GetProperty(name).GetRawText(), actual.GetProperty(name).GetRawText());
        }
    }

    private static void AssertNoReplay(JsonElement result)
    {
        Assert.False(result.GetProperty("Recovered").GetBoolean());
        Assert.Equal(0, result.GetProperty("RecoveryFolds").GetInt32());
        Assert.Equal(0, result.GetProperty("RecoveryForecastCalls").GetInt32());
        Assert.Equal(0, result.GetProperty("RecoveryPlanCalls").GetInt32());
    }

    private static string[] Events(JsonElement result) =>
        result.GetProperty("Completions").EnumerateArray().Select(value => value.GetRawText()).ToArray();

    private static async Task<JsonElement> RunAsync(string mode, string path, string response, int steps)
    {
        // The test deps graph includes this executable ProjectReference and all real
        // DurableGraph packages. Reuse it rather than invoke a nested build/dotnet run.
        string testAssembly = typeof(ColdProcessTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(ProcessWitness).Assembly.Location,
            mode, path, response, steps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("The cold-process witness did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Cold-process witness timed out: {await stderr}");
        }
        string output = await stdout;
        string error = await stderr;
        Assert.True(process.ExitCode == 0, $"Cold-process {mode} failed ({process.ExitCode}):\n{error}\n{output}");
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private sealed class WitnessPaths : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dramaboard-cold-" + Guid.NewGuid().ToString("N"));
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose()
        {
            if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        }
    }
}
