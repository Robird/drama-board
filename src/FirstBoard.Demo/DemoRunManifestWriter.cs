using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DramaBoard.FirstBoard;

namespace DramaBoard.FirstBoard.Demo;

internal sealed class DemoRunManifestWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _manifestPath;
    private readonly DemoOptions _options;
    private readonly ScenarioInstance _instance;
    private readonly string _runId = Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly string _runConfigurationSha256;

    public DemoRunManifestWriter(
        string outputDirectory,
        DemoOptions options,
        ScenarioInstance instance)
    {
        _manifestPath = Path.Combine(outputDirectory, "run-manifest.json");
        _options = options;
        _instance = instance;
        File.WriteAllBytes(
            Path.Combine(outputDirectory, "scenario-definition.json"),
            instance.Definition.ToCanonicalJsonUtf8());
        _runConfigurationSha256 = ComputeRunConfigurationSha256(options, instance);
        Write("running", result: null, errorType: null);
    }

    public void Complete(
        BoardRunCapture capture,
        int llmTurnCount,
        int forcedSceneEndCount) =>
        Write(
            "completed",
            new RunResultManifest(
                capture.Result.Status.ToString(),
                capture.Result.CurrentModelTime.Ticks,
                capture.Journal.Batches.Count,
                llmTurnCount,
                forcedSceneEndCount),
            errorType: null);

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("failed", result: null, exception.GetType().Name);
    }

    private void Write(string status, RunResultManifest? result, string? errorType)
    {
        (string? gitCommit, bool? gitDirty) = ReadGitState();
        var manifest = new
        {
            Schema = "dramaboard.run-manifest/1",
            RunId = _runId,
            Status = status,
            StartedAtUtc = _startedAtUtc,
            FinishedAtUtc = status == "running" ? (DateTimeOffset?)null : DateTimeOffset.UtcNow,
            Mode = "natural",
            Scenario = new
            {
                DefinitionId = _instance.Definition.Id,
                DefinitionRevision = _instance.Definition.Revision,
                _instance.Definition.RulesetId,
                _instance.DefinitionSha256,
                _instance.InstanceSha256,
                _instance.WorldSeed,
                DefinitionArtifact = "scenario-definition.json",
            },
            Origin = new { Kind = "root" },
            Simulation = new
            {
                LineageId = FirstBoardScenario.LineageId,
                UntilModelTimeMs = _options.UntilModelTimeMs,
                _options.MaxTurnsPerActor,
            },
            Players = new[]
            {
                Player(BoardIds.Alice, _options.AliceBackend),
                Player(BoardIds.Bob, _options.BobBackend),
            },
            MemoryRuntime = new
            {
                _options.MemoryBackend.Backend,
                _options.MemoryBackend.Model,
                ThinkingEffort = ThinkingEffort(_options.MemoryBackend),
                MaintenanceMode = _options.MemoryMaintenanceMode.ToString().ToLowerInvariant(),
            },
            Operational = new
            {
                OverallTimeoutMs = checked((long)_options.OverallTimeout.TotalMilliseconds),
                RequestTimeoutMs = checked((long)_options.RequestTimeout.TotalMilliseconds),
                EndpointIdentity = SafeEndpointIdentity(_options.BaseUrl),
            },
            Software = new
            {
                AssemblyInformationalVersion = typeof(FirstBoardScenario).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                GitCommit = gitCommit,
                GitDirty = gitDirty,
                DotnetRuntime = RuntimeInformation.FrameworkDescription,
            },
            RunConfigurationSha256 = _runConfigurationSha256,
            Result = result,
            ErrorType = errorType,
            ReplayAuthority = "Committed world journal; rerunning LLMs is not deterministic replay.",
        };
        File.WriteAllText(
            _manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
            Utf8NoBom);
    }

    private object Player(string actorId, DemoBackendOptions backend) => new
    {
        ActorId = actorId,
        backend.Backend,
        backend.Model,
        ThinkingEffort = ThinkingEffort(backend),
    };

    private string ThinkingEffort(DemoBackendOptions backend) =>
        backend.Backend == "codex"
            ? _options.ReasoningEffort ?? "provider-default"
            : "provider-default";

    private static string ComputeRunConfigurationSha256(
        DemoOptions options,
        ScenarioInstance instance)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "dramaboard.run-configuration/1");
            writer.WriteString("definitionSha256", instance.DefinitionSha256);
            writer.WriteString("instanceSha256", instance.InstanceSha256);
            writer.WriteNumber("lineageId", FirstBoardScenario.LineageId);
            writer.WriteNumber("untilModelTimeMs", options.UntilModelTimeMs);
            writer.WriteNumber("maxTurnsPerActor", options.MaxTurnsPerActor);
            WriteBackend(writer, "alice", options.AliceBackend, options.ReasoningEffort);
            WriteBackend(writer, "bob", options.BobBackend, options.ReasoningEffort);
            WriteBackend(writer, "memory", options.MemoryBackend, options.ReasoningEffort);
            writer.WriteString(
                "memoryMaintenanceMode",
                options.MemoryMaintenanceMode.ToString().ToLowerInvariant());
            writer.WriteNumber(
                "overallTimeoutMs",
                checked((long)options.OverallTimeout.TotalMilliseconds));
            writer.WriteNumber(
                "requestTimeoutMs",
                checked((long)options.RequestTimeout.TotalMilliseconds));
            string? endpointIdentity = SafeEndpointIdentity(options.BaseUrl);
            if (endpointIdentity is null)
            {
                writer.WriteNull("endpointIdentity");
            }
            else
            {
                writer.WriteString("endpointIdentity", endpointIdentity);
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteBackend(
        Utf8JsonWriter writer,
        string propertyName,
        DemoBackendOptions backend,
        string? codexReasoningEffort)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("backend", backend.Backend);
        writer.WriteString("model", backend.Model);
        writer.WriteString(
            "thinkingEffort",
            backend.Backend == "codex"
                ? codexReasoningEffort ?? "provider-default"
                : "provider-default");
        writer.WriteEndObject();
    }

    private static string? SafeEndpointIdentity(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return safe.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static (string? Commit, bool? Dirty) ReadGitState()
    {
        (int commitExit, string commit) = RunGit("rev-parse --verify HEAD");
        (int statusExit, string status) = RunGit("status --porcelain --untracked-files=no");
        return commitExit == 0 && statusExit == 0
            ? (commit.Trim(), !string.IsNullOrWhiteSpace(status))
            : (null, null);
    }

    private static (int ExitCode, string Output) RunGit(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(milliseconds: 2_000))
            {
                try
                {
                    process?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // A concurrently exiting process is already stopped.
                }

                return (-1, string.Empty);
            }

            return (process.ExitCode, process.StandardOutput.ReadToEnd());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (-1, string.Empty);
        }
    }

    private sealed record RunResultManifest(
        string Status,
        long FinalModelTimeMs,
        int WorldTransitionCount,
        int LlmTurnCount,
        int ForcedSceneEndCount);
}
