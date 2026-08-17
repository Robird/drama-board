using System.Globalization;

namespace DramaBoard.FirstBoard.Demo;

internal sealed record DemoBackendOptions(string Backend, string Model);

internal sealed record DemoOptions(
    DemoBackendOptions AliceBackend,
    DemoBackendOptions BobBackend,
    string OutputDirectory,
    ulong WorldSeed,
    long UntilModelTimeMs,
    int MaxTurnsPerActor,
    TimeSpan OverallTimeout,
    TimeSpan RequestTimeout,
    string? BaseUrl,
    string ApiKeyEnvironmentVariable,
    string CodexCommand,
    string? ReasoningEffort)
{
    public static DemoOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
            {
                throw new DemoHelpRequestedException();
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--name value', but found '{argument}'.");
            }

            values[argument[2..]] = args[++index];
        }

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
        string output = Read(
            values,
            "output",
            Path.Combine(
                "artifacts",
                "wp18",
                $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-" +
                $"alice-{aliceBackend}-{aliceModel}-bob-{bobBackend}-{bobModel}"));

        return new DemoOptions(
            new DemoBackendOptions(aliceBackend, aliceModel),
            new DemoBackendOptions(bobBackend, bobModel),
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
                "low");
    }

    public static string HelpText =>
        """
        DramaBoard FirstBoard real-LLM demo

          dotnet run --project src/FirstBoard.Demo -- [options]

        Options:
          --backend codex|deepseek|openai   Default backend for both actors: codex
          --model MODEL                    Default model for both actors
          --alice-backend BACKEND          Override Alice backend
          --alice-model MODEL              Override Alice model
          --bob-backend BACKEND            Override Bob backend
          --bob-model MODEL                Override Bob model
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
        """;

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
