using DramaBoard.Protocol;

namespace DramaBoard.Player.Tests;

public sealed class PlayerDriverTests
{
    [Fact]
    public async Task NullPlayerDriver_ReturnsCorrelatedWaitIntent()
    {
        DecisionRequest request = Request();
        var driver = new NullPlayerDriver();

        PlayerDecision decision = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal(request.DecisionId, decision.DecisionId);
        Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
        Assert.Null(decision.Intent.TargetActorId);
        Assert.Null(decision.Intent.TargetObjectId);
        Assert.Null(decision.Intent.ExitId);
        Assert.Null(decision.Intent.DestinationId);
    }

    [Fact]
    public async Task ScriptedPlayerDriver_ConsumesFactoriesInOrderThenThrows()
    {
        DecisionRequest request = Request();
        var driver = new ScriptedPlayerDriver(
        [
            current => Decision(current, new Intent(ActionKinds.Travel, ExitId: "exit.left")),
            current => Decision(current, new Intent(ActionKinds.Wait)),
        ]);

        PlayerDecision first = await driver.DecideAsync(request, CancellationToken.None);
        PlayerDecision second = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal("exit.left", first.Intent.ExitId);
        Assert.Equal(ActionKinds.Wait, second.Intent.ActionKind);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.DecideAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task RandomPlayerDriver_RepeatedRequestIsIdempotent()
    {
        DecisionRequest request = Request(
        [
            new(ActionKinds.Travel, CandidateExitIds: ["exit.left", "exit.right", "exit.forward"]),
            new(ActionKinds.Talk, CandidateActorIds: ["actor.bob", "actor.cara"]),
            new(ActionKinds.Wait),
        ]);
        var first = new RandomPlayerDriver(0xC0FFEE);
        var second = new RandomPlayerDriver(0xC0FFEE);
        Intent expected = (await first.DecideAsync(request, CancellationToken.None)).Intent;

        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(expected, (await first.DecideAsync(request, CancellationToken.None)).Intent);
            Assert.Equal(expected, (await second.DecideAsync(request, CancellationToken.None)).Intent);
        }
    }

    [Fact]
    public async Task RandomPlayerDriver_DecisionIdAddressesDifferentSamples()
    {
        var driver = new RandomPlayerDriver(0xC0FFEE);
        AvailableAction[] actions =
        [
            new(
                ActionKinds.Travel,
                CandidateExitIds: [.. Enumerable.Range(0, 16).Select(index => $"exit.{index}")]),
        ];
        var exits = new HashSet<string?>();

        for (long sequence = 1; sequence <= 16; sequence++)
        {
            exits.Add((await driver.DecideAsync(
                Request(actions, $"decision-{sequence}"),
                CancellationToken.None)).Intent.ExitId);
        }

        Assert.True(exits.Count > 1);
    }

    [Fact]
    public async Task RandomPlayerDriver_EmptyAffordancesFallsBackToWait()
    {
        var driver = new RandomPlayerDriver(1);

        PlayerDecision decision = await driver.DecideAsync(
            Request([]),
            CancellationToken.None);

        Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
    }

    private static DecisionRequest Request(
        IReadOnlyList<AvailableAction>? actions = null,
        string decisionId = "decision-1")
    {
        IReadOnlyList<AvailableAction> availableActions =
            actions ?? [new AvailableAction(ActionKinds.Wait)];
        ObservedExit[] exits =
        [
            .. availableActions
                .SelectMany(action => action.CandidateExitIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .Select(exitId => new ObservedExit(exitId, $"place.{exitId}", 1, true)),
        ];
        var observation = new Observation("actor.alice", "place.square", 10, exits, [], [], []);
        return new DecisionRequest(
            new DecisionId(decisionId),
            "actor.alice",
            10,
            observation,
            availableActions);
    }

    private static PlayerDecision Decision(DecisionRequest request, Intent intent) =>
        new(request.DecisionId, intent);
}
