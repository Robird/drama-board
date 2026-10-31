using System.Globalization;
using System.Text;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Microtown;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

try {
    return await RunAsync(args);
} catch (Exception error) when (error is ArgumentException or FormatException or IOException or InvalidOperationException or KeyNotFoundException) {
    Console.Error.WriteLine($"Microtown stopped: {error.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] arguments) {
    string? store = null, branch = null;
    int hours = 48, maxOperations = 200;
    bool explicitHours = false;
    for (int i = 0; i < arguments.Length; i++) {
        string Next() => ++i < arguments.Length ? arguments[i] : throw new ArgumentException("Missing option value.");
        switch (arguments[i]) {
            case "--store": store = Next(); break;
            case "--branch": branch = Next(); break;
            case "--hours": hours = Positive(Next(), 720); explicitHours = true; break;
            case "--max-operations": maxOperations = Positive(Next(), 100000); break;
            case "--help":
                Console.WriteLine("Microtown: [--store DIRECTORY] [--branch ID] [--hours 1..720] [--max-operations N]");
                Console.WriteLine("One Human Console serves all three residents. Enter :look or :quit; EOF pauses.");
                Console.WriteLine("--hours configures a new game only. Without --store the game is held in memory.");
                return 0;
            default: throw new ArgumentException($"Unknown option: {arguments[i]}");
        }
    }
    if (branch is not null && (store is null || !Directory.Exists(store))) {
        throw new ArgumentException("--branch requires an existing --store.");
    }
    if (store is not null && Directory.Exists(store) && explicitHours) {
        throw new ArgumentException("--hours cannot change a saved game's endpoint; omit it when reopening.");
    }

    using TownDurableHistory? durable = store is null ? null : Directory.Exists(store)
        ? TownDurableHistory.Open(store, branch is null ? null : TownDurableHistory.ParseBranchId(branch))
        : CreateStore(store, hours);
    IOccurrenceHistory<TownWorld, TownFact> history;
    if (durable is not null) {
        history = durable;
        Console.WriteLine($"[Host] Store: {Path.GetFullPath(store!)}; branch: {TownDurableHistory.FormatBranchId(durable.BranchId)}");
    } else {
        var created = TownGameFactory.Create(hours);
        history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(created.World, created.Cursor);
        Console.WriteLine("[Host] In-memory game; use --store to retain progress.");
    }

    int operations = 0;
    var kernel = TownEngine.CreateControlledKernel(history, (observation, cancellationToken) => {
        cancellationToken.ThrowIfCancellationRequested();
        if (operations >= maxOperations) { throw new PauseRequested("Operation budget reached."); }
        string material = TownTextRenderer.Render(observation);
        Console.WriteLine(material);
        while (true) {
            Console.Write($"action[{observation.ActorId}] (:look, :quit)> ");
            string? raw = Console.ReadLine();
            if (raw is null || raw.Trim() == ":quit") { throw new PauseRequested(raw is null ? "End of input." : "Requested by Human."); }
            if (raw.Trim() == ":look") { Console.WriteLine(material); continue; }
            operations++;
            return ValueTask.FromResult(new TownControllerOperation(raw, $"console-{Guid.NewGuid():N}", Guid.NewGuid().ToString("N")));
        }
    });
    if (kernel.RecoverPending()) { Console.WriteLine("[Host] Recovered one pending occurrence without requesting another action."); }
    try {
        int steps = 0;
        while (!kernel.World.Business.Ended) {
            if (++steps > 100000) { throw new PauseRequested("Host transition budget reached."); }
            StepStatus status = await kernel.StepAsync(kernel.World.Business.EndTime);
            if (status != StepStatus.Committed) { throw new InvalidOperationException($"Unexpected simulation stop: {status}"); }
            foreach (TownFact fact in kernel.LastCompletion!.Event.Facts) {
                if (fact.Kind == TownEventKind.Operation) {
                    Console.WriteLine($"[Result: {fact.ActorId}] {(fact.Accepted ? "Accepted" : "Rejected")}: {fact.Result}");
                }
            }
        }
        Console.WriteLine("[Host] Experiment endpoint reached.");
    } catch (PauseRequested pause) {
        Console.WriteLine($"[Host] Paused: {pause.Message}");
    }
    PrintStatistics(kernel.World.Business);
    if (durable is not null) {
        Console.WriteLine($"[Host] Completed state: {TownDurableHistory.FormatCommitId(durable.CompletedStateCommitId)}; reopen the same store to continue.");
    }
    return 0;
}

static int Positive(string text, int maximum) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
    && value > 0 && value <= maximum ? value : throw new ArgumentException($"Expected an integer from 1 to {maximum}.");

static TownDurableHistory CreateStore(string path, int hours) {
    var initial = TownGameFactory.Create(hours);
    return TownDurableHistory.Create(path, initial.World, initial.Cursor);
}

static void PrintStatistics(TownBusinessState state) {
    state.Economy.Verify(state);
    Console.WriteLine($"[Host statistics — developer view, not character memory] 0..{state.Time.Ticks / (double)TownContent.Hour:0.##} hours; ended={state.Ended}");
    Console.WriteLine($"Cash={state.Characters.Sum(actor => actor.Money)}; initial={state.Economy.InitialMoney}; import expense={state.Economy.ImportExpense}; export income={state.Economy.ExportIncome}.");
    Console.WriteLine($"Market refreshes={state.Market.Cycle}; next refresh at hour {state.Market.NextRefresh.Ticks / (double)TownContent.Hour:0.##}.");
    foreach (TownCharacter actor in state.Characters) {
        Console.WriteLine($"Resident {actor.Id}: food={actor.Food:0.##}/100; minimum={actor.MinimumFood:0.##}; failed={actor.Failed}; cash={actor.Money}.");
    }
    Console.WriteLine($"Worst current food: {state.WorstOff.Id} ({state.WorstOff.Food:0.##}); failed residents remain included.");
    foreach (string item in TownContent.Items) {
        long held = state.Characters.Sum(actor => (long)actor.Inventory.Count(item));
        long workInProgress = state.Characters.Sum(actor => actor.Activity?.InputItem == item ? (long)actor.Activity.InputCount : 0);
        Console.WriteLine($"Item {item}: held={held}; work-in-progress={workInProgress}; imported={state.Economy.Imported.GetValueOrDefault(item)}; exported={state.Economy.Exported.GetValueOrDefault(item)}; produced={state.Economy.Produced.GetValueOrDefault(item)}; eaten={state.Economy.Consumed.GetValueOrDefault(item)}; recipe-inputs={state.Economy.RecipeInputs.GetValueOrDefault(item)}.");
    }
    Console.WriteLine("Money and item balances reconcile. Inventory is not counted as cash or welfare.");
}

sealed class PauseRequested(string message) : Exception(message);
