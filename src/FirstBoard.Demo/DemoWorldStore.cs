using DramaBoard.FirstBoard.Persistence;

namespace DramaBoard.FirstBoard.Demo;

/// <summary>Creates or opens the objective world before any Player resources are composed.</summary>
internal static class DemoWorldStore
{
    // The policy deliberately permits newly configured AI backends and fresh budgets on each run.
    // Human assignment and the controlled actor set remain part of the saved strategy contract.
    public static FirstBoardDriverBinding DriverBinding(DemoOptions options) =>
        new([BoardIds.Alice, BoardIds.Bob],
            $"firstboard-live/world-only-v1/human-{options.HumanActorId ?? "none"}");

    public static FirstBoardOccurrenceHistory? Open(DemoOptions options)
    {
        if (options.WorldStore is null) { return null; }
        FirstBoardDriverBinding binding = DriverBinding(options);
        if (options.ResumeWorld)
        {
            return FirstBoardOccurrenceHistory.Open(options.WorldStore, "main", binding);
        }
        ScenarioInstance scenario = ScenarioInstance.CreateDefault(options.WorldSeed);
        FirstBoardWorld world = scenario.CreateInitialWorld();
        return FirstBoardOccurrenceHistory.Create(options.WorldStore, "main", scenario, world,
            FirstBoardScenario.CreateMemoryHistory(world).Cursor,
            FirstBoardScenario.CreateRules(scenario), binding);
    }
}
