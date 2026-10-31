using System.Globalization;
using System.Text;
using System.Text.Json;
using DramaBoard.Microtown;
using DramaBoard.Microtown.Cli;
using DramaBoard.Microtown.Cli.Gym;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
try {
    var options = new TownHostOptions();
    string? management = null, sourceId = null, exportPath = null, actor = null, label = null;
    for (int i = 0; i < args.Length; i++) {
        string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
        void Manage(string kind) {
            if (management is not null) { throw new ArgumentException("Choose one management operation."); }
            management = kind;
        }
        switch (args[i]) {
            case "--store": options = options with { Store = Next() }; break;
            case "--branch": options = options with { Branch = Next() }; break;
            case "--hours": options = options with { Hours = Positive(Next()), ExplicitHours = true }; break;
            case "--max-operations": options = options with { MaxOperations = Positive(Next()) }; break;
            case "--llm": options = options with { Llm = true }; break;
            case "--human": options = options with { HumanActor = Next() }; break;
            case "--gym": options = options with { Gym = true }; break;
            case "--feedback": options = options with { Feedback = true }; break;
            case "--model": options = options with { Connection = options.Connection with { Model = Next() } }; break;
            case "--auth-file": options = options with { Connection = options.Connection with { AuthFile = Next() } }; break;
            case "--catalog": options = options with { Connection = options.Connection with { Catalog = Next() } }; break;
            case "--connection": options = options with { Connection = options.Connection with { Connection = Next() } }; break;
            case "--max-calls": options = options with { MaxCalls = Positive(Next()) }; break;
            case "--max-failures": options = options with { MaxFailures = Positive(Next()) }; break;
            case "--max-context-chars": options = options with { MaxContextCharacters = Positive(Next()) }; break;
            case "--experiment-note": options = options with { ExperimentNote = File.ReadAllText(Next()) }; break;
            case "--branches": Manage("branches"); break;
            case "--gym-feedbacks": Manage("feedbacks"); break;
            case "--gym-export": Manage("export"); exportPath = Next(); break;
            case "--gym-fork": Manage("fork-feedback"); sourceId = Next(); break;
            case "--gym-fork-request": Manage("fork-request"); sourceId = Next(); break;
            case "--actor": actor = Next(); break;
            case "--fork-label": label = Next(); break;
            case "--help":
                Console.WriteLine("Microtown: [--store DIRECTORY] [--branch ID] [--hours 1..720] [--max-operations N]");
                Console.WriteLine("Human: one Console serves all residents. :look, :quit or EOF. --hours is only for a new game.");
                Console.WriteLine("LLM: --llm [--human a|b|c] [--model MODEL] [--auth-file PATH | --catalog PATH --connection ID]");
                Console.WriteLine("Budgets: --max-calls N --max-failures N --max-context-chars N; Ctrl+C pauses.");
                Console.WriteLine("Evidence: --gym or --feedback (requires --llm), both require --store; --experiment-note FILE adds explicit experimental instructions.");
                Console.WriteLine("Management: --store PATH [--branch ID] --branches | --gym-feedbacks [--actor ID] | --gym-export NEWFILE --actor ID");
                Console.WriteLine("Fork: --store PATH --gym-fork FEEDBACK_ID | --gym-fork-request REQUEST_ID, with --fork-label LABEL.");
                return 0;
            default: throw new ArgumentException($"Unknown option: {args[i]}");
        }
    }
    if (management is not null) {
        if (options.Store is null || !Directory.Exists(options.Store)) { throw new ArgumentException("Management requires an existing --store."); }
        switch (management) {
            case "branches":
                foreach (TownBranchInfo branch in TownDurableHistory.ListBranches(options.Store)) {
                    Console.WriteLine(JsonSerializer.Serialize(new {
                        BranchId = TownDurableHistory.FormatBranchId(branch.Id), branch.DisplayLabel,
                        HeadCommitId = TownDurableHistory.FormatCommitId(branch.HeadCommitId),
                        CompletedStateCommitId = TownDurableHistory.FormatCommitId(branch.CompletedStateCommitId),
                        ForkedFromCommitId = branch.ForkedFromCommitId is { } origin ? TownDurableHistory.FormatCommitId(origin) : null,
                        branch.HasPendingEvent
                    }));
                }
                break;
            case "feedbacks": TownGymCommands.ListFeedbacks(options.Store, options.Branch, actor, Console.Out); break;
            case "export":
                ArgumentException.ThrowIfNullOrWhiteSpace(actor); ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
                TownGymCommands.Export(options.Store, options.Branch, actor, exportPath);
                Console.WriteLine($"Exported {actor}: {exportPath}"); break;
            default:
                ArgumentException.ThrowIfNullOrWhiteSpace(label); ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
                TownBranchInfo created = TownGymCommands.Fork(options.Store, sourceId, management == "fork-feedback", label);
                Console.WriteLine($"Branch registered: {TownDurableHistory.FormatBranchId(created.Id)}; not opened or run."); break;
        }
        return 0;
    }
    if (actor is not null || label is not null) { throw new ArgumentException("--actor and --fork-label require a management operation."); }
    return await TownConsole.RunAsync(options, cancellationToken: cancellation.Token);
} catch (Exception error) when (error is ArgumentException or FormatException or IOException or InvalidOperationException or KeyNotFoundException) {
    Console.Error.WriteLine($"Microtown stopped: {error.Message}");
    return 1;
}

static int Positive(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
    && value > 0 ? value : throw new ArgumentException("Expected a positive integer.");
