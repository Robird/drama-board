using DramaBoard.Microtown;

namespace DramaBoard.Microtown.Cli;

public static class TownStatistics {
    public static void PrintStatistics(TownBusinessState state, TextWriter output) {
        state.Economy.Verify(state);
        output.WriteLine($"[Host statistics — developer view, not character memory] 0..{state.Time.Ticks / (double)TownContent.Hour:0.##} hours; ended={state.Ended}");
        output.WriteLine($"Cash={state.Characters.Sum(actor => actor.Money)}; initial={state.Economy.InitialMoney}; import expense={state.Economy.ImportExpense}; export income={state.Economy.ExportIncome}.");
        output.WriteLine($"Market refreshes={state.Market.Cycle}; next refresh at hour {state.Market.NextRefresh.Ticks / (double)TownContent.Hour:0.##}.");
        foreach (TownCharacter actor in state.Characters) {
            output.WriteLine($"Resident {actor.Id}: food={actor.Food:0.##}/100; minimum={actor.MinimumFood:0.##}; failed={actor.Failed}; cash={actor.Money}.");
        }
        output.WriteLine($"Worst current food: {state.WorstOff.Id} ({state.WorstOff.Food:0.##}); failed residents remain included.");
        foreach (string item in TownContent.Items) {
            long held = state.Characters.Sum(actor => (long)actor.Inventory.Count(item));
            long workInProgress = state.Characters.Sum(actor => actor.Activity?.InputItem == item ? (long)actor.Activity.InputCount : 0);
            output.WriteLine($"Item {item}: held={held}; work-in-progress={workInProgress}; imported={state.Economy.Imported.GetValueOrDefault(item)}; exported={state.Economy.Exported.GetValueOrDefault(item)}; produced={state.Economy.Produced.GetValueOrDefault(item)}; eaten={state.Economy.Consumed.GetValueOrDefault(item)}; recipe-inputs={state.Economy.RecipeInputs.GetValueOrDefault(item)}.");
        }
        output.WriteLine("Money and item balances reconcile. Inventory is not counted as cash or welfare.");
    }

}
