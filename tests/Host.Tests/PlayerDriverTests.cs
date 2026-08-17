using DramaBoard.Protocol;

namespace DramaBoard.Host.Tests;

public sealed class PlayerDriverTests
{
    [Fact]
    public async Task NullPlayerDriver_ReturnsCorrelatedWaitIntent()
    {
        DecisionRequest request = Request();
        var driver = new NullPlayerDriver();

        PlayerDecision decision = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal(request.DecisionId, decision.DecisionId);
        Assert.Equal(request.BasedOnWorldVersion, decision.BasedOnWorldVersion);
        Assert.Equal(request.LineageId, decision.LineageId);
        Assert.Equal(ActionKinds.Wait, decision.Intent.ActionKind);
        Assert.Null(decision.Intent.TargetActorId);
        Assert.Null(decision.Intent.TargetObjectId);
        Assert.Null(decision.Intent.DestinationId);
    }

    [Fact]
    public async Task ScriptedPlayerDriver_ConsumesFactoriesInOrderThenThrows()
    {
        DecisionRequest request = Request();
        var driver = new ScriptedPlayerDriver(
        [
            current => Decision(current, new Intent(ActionKinds.Travel, DestinationId: "left")),
            current => Decision(current, new Intent(ActionKinds.Wait)),
        ]);

        PlayerDecision first = await driver.DecideAsync(request, CancellationToken.None);
        PlayerDecision second = await driver.DecideAsync(request, CancellationToken.None);

        Assert.Equal("left", first.Intent.DestinationId);
        Assert.Equal(ActionKinds.Wait, second.Intent.ActionKind);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.DecideAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task RandomPlayerDriver_SameSeedProducesSameIntentSequence()
    {
        DecisionRequest request = Request(
        [
            new(ActionKinds.Travel, CandidateDestinationIds: ["left", "right", "forward"]),
            new(ActionKinds.Talk, CandidateActorIds: ["actor.bob", "actor.cara"]),
            new(ActionKinds.Wait),
        ]);
        var first = new RandomPlayerDriver(0xC0FFEEUL);
        var second = new RandomPlayerDriver(0xC0FFEEUL);
        var firstIntents = new List<Intent>();
        var secondIntents = new List<Intent>();

        for (int index = 0; index < 12; index++)
        {
            firstIntents.Add((await first.DecideAsync(request, CancellationToken.None)).Intent);
            secondIntents.Add((await second.DecideAsync(request, CancellationToken.None)).Intent);
        }

        Assert.Equal(firstIntents, secondIntents);
        Assert.Contains(firstIntents, intent => intent.DestinationId is not null);
        Assert.Contains(firstIntents, intent => intent.TargetActorId is not null);
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

    private static DecisionRequest Request(IReadOnlyList<AvailableAction>? actions = null)
    {
        var observation = new Observation("actor.alice", "place.square", 10, 2, [], [], []);
        return new DecisionRequest(
            new DecisionId("decision-1"),
            7,
            42,
            10,
            2,
            "actor.alice",
            observation,
            DecisionReasons.Scheduled,
            actions ?? [new AvailableAction(ActionKinds.Wait)]);
    }

    private static PlayerDecision Decision(DecisionRequest request, Intent intent) =>
        new(request.DecisionId, request.BasedOnWorldVersion, request.LineageId, intent);
}
