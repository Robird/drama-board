using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class TerminalUiTests
{
    [Fact]
    public void PlacePromptListsObservedExitsAndLegalTargetIds()
    {
        var request = new DecisionRequest(
            new DecisionId("decision.alice.1"),
            BoardIds.Alice,
            ModelTimeMs: 0,
            new Observation(
                BoardIds.Alice,
                BoardIds.Tavern,
                ModelTimeMs: 0,
                Exits:
                [
                    new ObservedExit(
                        $"exit:{BoardIds.TavernMarketRoad}",
                        BoardIds.Market,
                        expectedDurationMs: 300_000,
                        isAvailable: true),
                ],
                VisibleActorIds: [],
                VisibleObjectIds: [],
                KnownFacts: []),
            AvailableActions:
            [
                new AvailableAction(
                    ActionKinds.Travel,
                    CandidateExitIds: [$"exit:{BoardIds.TavernMarketRoad}"]),
                new AvailableAction(
                    ActionKinds.TravelTo,
                    CandidateDestinationIds: [BoardIds.Market]),
            ]);

        string text = TerminalUi.FormatPrompt(request);

        Assert.Contains(
            $"exit: exit:{BoardIds.TavernMarketRoad} -> {BoardIds.Market}",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{ActionKinds.Travel.Id} (exits=[exit:{BoardIds.TavernMarketRoad}])",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{ActionKinds.TravelTo.Id} (destinations=[{BoardIds.Market}])",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EncounterPromptMakesContinueAndReverseAffordancesExplicit()
    {
        var request = new DecisionRequest(
            new DecisionId("decision.alice.2"),
            BoardIds.Alice,
            ModelTimeMs: 150_000,
            new Observation(
                BoardIds.Alice,
                BoardIds.TavernMarketRoad,
                ModelTimeMs: 150_000,
                Exits: [],
                VisibleActorIds: [BoardIds.Bob],
                VisibleObjectIds: [],
                KnownFacts:
                [
                    new KnownFact(
                        new FactKind(BoardIds.PassageContactCounterpart),
                        BoardIds.Bob,
                        "You encountered Bob in the passage."),
                ]),
            AvailableActions:
            [
                new AvailableAction(ActionKinds.ContinueTravel),
                new AvailableAction(ActionKinds.ReverseTravel),
            ]);

        string text = TerminalUi.FormatPrompt(request);

        Assert.Contains($"visible actors: {BoardIds.Bob}", text, StringComparison.Ordinal);
        Assert.Contains($"    {ActionKinds.ContinueTravel.Id}", text, StringComparison.Ordinal);
        Assert.Contains($"    {ActionKinds.ReverseTravel.Id}", text, StringComparison.Ordinal);
    }
}
