using System.Globalization;
using DramaBoard.FirstBoard.Demo.Live;
using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal sealed record DemoBackendOptions(string Backend, string Model);

internal sealed record DemoOptions(
    DemoBackendOptions AliceBackend,
    DemoBackendOptions BobBackend,
    DemoBackendOptions MemoryBackend,
    string? HumanActorId,
    PresentationMode PresentationMode,
    TimeSpan PresentationInterval,
    string OutputDirectory,
    ulong WorldSeed,
    long UntilModelTimeMs,
    int MaxTurnsPerActor,
    TimeSpan OverallTimeout,
    TimeSpan RequestTimeout,
    string? BaseUrl,
    string ApiKeyEnvironmentVariable,
    string CodexCommand,
    string? ReasoningEffort,
    MemoryMaintenanceMode MemoryMaintenanceMode)
{
    private static readonly HashSet<string> AllowedOptionNames = new(
        [
            "backend",
            "model",
            "alice-backend",
            "alice-model",
            "bob-backend",
            "bob-model",
            "memory-backend",
            "memory-model",
            "human",
            "presentation",
            "presentation-interval-ms",
            "base-url",
            "api-key-env",
            "output",
            "seed",
            "until-ms",
            "max-turns-per-actor",
            "timeout-minutes",
            "request-timeout-seconds",
            "codex-command",
            "reasoning",
            "memory-maintenance",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static DemoOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--help", StringComparison.OrdinalIgnoreCase) || argument == "-h")
            {
                throw new DemoHelpRequestedException();
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--name value', but found '{argument}'.");
            }

            string optionName = argument[2..];
            if (!AllowedOptionNames.Contains(optionName))
            {
                throw new ArgumentException($"Unknown option '--{optionName}'.");
            }

            values[optionName] = args[++index];
        }

        string? humanActorId = ReadHumanActorId(values);
        PresentationMode presentationMode = ReadPresentationMode(values);
        if (presentationMode == PresentationMode.Player && humanActorId is null)
        {
            throw new ArgumentException("--presentation player requires --human alice or bob.");
        }

        TimeSpan presentationInterval = ReadPresentationInterval(values);
        string backend = ReadBackend(values, "backend", "codex");
        string model = Read(values, "model", DefaultModel(backend));
        string aliceBackend = ReadBackend(values, "alice-backend", backend);
        string bobBackend = ReadBackend(values, "bob-backend", backend);
        string aliceModel = Read(
            values,
            "alice-model",
            aliceBackend == backend ? model : DefaultModel(aliceBackend));
        string bobModel = Read(
            values,
            "bob-model",
            bobBackend == backend ? model : DefaultModel(bobBackend));
        string defaultMemoryBackend = humanActorId == BoardIds.Alice ? bobBackend : aliceBackend;
        string defaultMemoryModel = humanActorId == BoardIds.Alice ? bobModel : aliceModel;
        string memoryBackend = ReadBackend(values, "memory-backend", defaultMemoryBackend);
        string memoryModel = Read(
            values,
            "memory-model",
            memoryBackend == defaultMemoryBackend
                ? defaultMemoryModel
                : memoryBackend == aliceBackend
                    ? aliceModel
                    : memoryBackend == bobBackend
                        ? bobModel
                        : DefaultModel(memoryBackend));
        string output = Read(
            values,
            "output",
            Path.Combine(
                "artifacts",
                "live",
                $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-" +
                $"human-{humanActorId ?? "none"}-" +
                $"presentation-{presentationMode.ToString().ToLowerInvariant()}-" +
                $"alice-{aliceBackend}-{aliceModel}-bob-{bobBackend}-{bobModel}"));

        return new DemoOptions(
            new DemoBackendOptions(aliceBackend, aliceModel),
            new DemoBackendOptions(bobBackend, bobModel),
            new DemoBackendOptions(memoryBackend, memoryModel),
            humanActorId,
            presentationMode,
            presentationInterval,
            Path.GetFullPath(output),
            ReadUInt64(values, "seed", 20_260_817),
            ReadInt64(values, "until-ms", BoardTiming.RandomRunBoundaryTicks, minimum: 0),
            checked((int)ReadInt64(values, "max-turns-per-actor", 8, minimum: 1)),
            TimeSpan.FromMinutes(ReadDouble(values, "timeout-minutes", 15, minimum: 0.01)),
            TimeSpan.FromSeconds(ReadDouble(values, "request-timeout-seconds", 120, minimum: 0.01)),
            values.GetValueOrDefault("base-url") ??
                Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ??
                Environment.GetEnvironmentVariable("BASE_URL"),
            Read(values, "api-key-env", "DEEPSEEK_API_KEY"),
            Read(values, "codex-command", "codex"),
            values.GetValueOrDefault("reasoning") ??
                Environment.GetEnvironmentVariable("CODEX_REASONING_EFFORT") ??
                "low",
            ReadMemoryMaintenanceMode(values));
    }

    public static string HelpText =>
        """
        DramaBoard FirstBoard real-LLM demo

          dotnet run --project src/FirstBoard.Demo -- [options]

        Options:
          --human alice|bob|none           Console Human actor; default: none
          --presentation player|developer Presentation view; default: developer;
                                           player requires a Human actor
          --presentation-interval-ms N     Minimum visible cue interval; default: 250;
                                           use 0 for tests and fast playback
          --backend codex|deepseek|openai   Default backend for both actors: codex
          --model MODEL                    Default model for both actors
          --alice-backend BACKEND          Override Alice backend
          --alice-model MODEL              Override Alice model
          --bob-backend BACKEND            Override Bob backend
          --bob-model MODEL                Override Bob model
          --memory-backend BACKEND         Backend for all private shard maintainers;
                                           default: Alice backend
          --memory-model MODEL             Memory maintainer model; default follows matching actor
          --base-url URL                   OpenAI-compatible base URL; env fallback:
                                           DEEPSEEK_BASE_URL, then BASE_URL
          --api-key-env NAME               Credential env name; default: DEEPSEEK_API_KEY
          --output DIRECTORY               Drama record and memory snapshots
          --seed NUMBER                    Default: 20260817
          --until-ms NUMBER                Default: 4200000
          --max-turns-per-actor NUMBER     Default: 8, then a long wait ends the scene
          --timeout-minutes NUMBER         Whole-run timeout; default: 15
          --request-timeout-seconds NUMBER Per Codex request; default: 120
          --codex-command PATH             Default: codex
          --reasoning EFFORT               Codex effort; default: low
          --memory-maintenance MODE        blocking|pipelined; default: blocking
        """;

    private static string? ReadHumanActorId(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("human", out string? raw))
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "alice" => BoardIds.Alice,
            "bob" => BoardIds.Bob,
            "none" => null,
            _ => throw new ArgumentException("--human must be alice, bob, or none."),
        };
    }

    private static PresentationMode ReadPresentationMode(
        IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("presentation", out string? raw))
        {
            return PresentationMode.Developer;
        }

        return raw.ToLowerInvariant() switch
        {
            "player" => PresentationMode.Player,
            "developer" => PresentationMode.Developer,
            _ => throw new ArgumentException(
                "--presentation must be player or developer."),
        };
    }

    private static TimeSpan ReadPresentationInterval(
        IReadOnlyDictionary<string, string> values)
    {
        long milliseconds = ReadInt64(
            values,
            "presentation-interval-ms",
            fallback: 250,
            minimum: 0);
        if (milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
        {
            throw new ArgumentException(
                "--presentation-interval-ms exceeds the supported TimeSpan range.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static MemoryMaintenanceMode ReadMemoryMaintenanceMode(
        IReadOnlyDictionary<string, string> values)
    {
        string value = Read(values, "memory-maintenance", "blocking").ToLowerInvariant();
        return value switch
        {
            "blocking" => MemoryMaintenanceMode.Blocking,
            "pipelined" => MemoryMaintenanceMode.Pipelined,
            _ => throw new ArgumentException(
                "--memory-maintenance must be blocking or pipelined."),
        };
    }

    private static string ReadBackend(
        IReadOnlyDictionary<string, string> values,
        string name,
        string fallback)
    {
        string backend = Read(values, name, fallback).ToLowerInvariant();
        if (backend is not ("codex" or "deepseek" or "openai"))
        {
            throw new ArgumentException($"--{name} must be codex, deepseek, or openai.");
        }

        return backend;
    }

    private static string DefaultModel(string backend) =>
        backend == "codex"
            ? "gpt-5.6-luna"
            : Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-flash";

    private static string Read(
        IReadOnlyDictionary<string, string> values,
        string name,
        string fallback) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;

    private static long ReadInt64(
        IReadOnlyDictionary<string, string> values,
        string name,
        long fallback,
        long minimum)
    {
        if (!values.TryGetValue(name, out string? value))
        {
            return fallback;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ||
            parsed < minimum)
        {
            throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
        }

        return parsed;
    }

    private static ulong ReadUInt64(
        IReadOnlyDictionary<string, string> values,
        string name,
        ulong fallback)
    {
        if (!values.TryGetValue(name, out string? value))
        {
            return fallback;
        }

        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
        {
            throw new ArgumentException($"--{name} must be an unsigned integer.");
        }

        return parsed;
    }

    private static double ReadDouble(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback,
        double minimum)
    {
        if (!values.TryGetValue(name, out string? value))
        {
            return fallback;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) ||
            parsed < minimum)
        {
            throw new ArgumentException($"--{name} must be a finite number >= {minimum}.");
        }

        return parsed;
    }
}

internal sealed class DemoHelpRequestedException : Exception;
