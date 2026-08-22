using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class PresentationGatedHumanPlayerDriverTests
{
    [Fact]
    public async Task RequestRemainsInvisibleUntilItsCapturedCommittedPrefixIsPresented()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        var second = new WorldVersion(17, 2);
        coordination.PublishCommitted(first);
        coordination.PublishCommitted(second);
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest request = Request(
            "decision.alice.1",
            new Intent(ActionKinds.Observe));

        ValueTask<PlayerDecision> pending = driver.DecideAsync(request, CancellationToken.None);

        Assert.Empty(terminal.Prompts);
        coordination.AcknowledgePresented(first);
        Assert.Empty(terminal.Prompts);
        coordination.AcknowledgePresented(second);
        await terminal.WaitForPromptCountAsync(1);
        Assert.Same(request, Assert.Single(terminal.Prompts));
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "observe"));
        PlayerDecision decision = await pending;
        Assert.Equal(new Intent(ActionKinds.Observe), decision.Intent);
        Assert.Equal(second, coordination.Snapshot().Committed);
    }

    [Theory]
    [MemberData(nameof(HumanDecisionParserTests.SupportedCommands),
        MemberType = typeof(HumanDecisionParserTests))]
    public async Task EverySupportedCommandPassesItsAdvertisedAffordance(
        string command,
        Intent expected)
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var terminal = new FakeTerminalUi();
        IPlayerDriver driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest request = Request("decision.alice.schema", expected);

        ValueTask<PlayerDecision> pending = driver.DecideAsync(request, CancellationToken.None);
        await terminal.WaitForPromptCountAsync(1);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, command));

        PlayerDecision actual = await pending;
        Assert.Equal(request.DecisionId, actual.DecisionId);
        Assert.Equal(expected, actual.Intent);
    }

    [Fact]
    public async Task MalformedAndUnavailableCommandsShowErrorsAndRetryTheSameRequest()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest request = Request(
            "decision.alice.retry",
            new Intent(ActionKinds.Observe));

        ValueTask<PlayerDecision> pending = driver.DecideAsync(request, CancellationToken.None);
        await terminal.WaitForPromptCountAsync(1);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "not-a-command"));
        await terminal.WaitForErrorCountAsync(1);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "reverse"));
        await terminal.WaitForErrorCountAsync(2);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "observe"));

        PlayerDecision decision = await pending;
        Assert.Equal(new Intent(ActionKinds.Observe), decision.Intent);
        Assert.Single(terminal.Prompts);
        Assert.Equal(2, terminal.Errors.Count);
        Assert.Equal(new WorldVersion(17, 0), coordination.Snapshot().Committed);
    }

    [Fact]
    public async Task HelpShowsCommandLanguageAndKeepsTheSameRequestPending()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest request = Request(
            "decision.alice.help",
            new Intent(ActionKinds.Observe));

        ValueTask<PlayerDecision> pending = driver.DecideAsync(request, CancellationToken.None);
        await terminal.WaitForPromptCountAsync(1);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "HELP"));
        await terminal.WaitForErrorCountAsync(1);
        Assert.Contains("travel-to <place>", Assert.Single(terminal.Errors));
        Assert.False(pending.IsCompleted);
        await terminal.WaitForActiveReadAsync(request.DecisionId);
        Assert.True(terminal.TrySubmit(request.DecisionId, "observe"));

        Assert.Equal(request.DecisionId, (await pending).DecisionId);
        Assert.Single(terminal.Prompts);
    }

    [Fact]
    public async Task CancellationClearsPendingReadAndLateInputCannotEnterNextRequest()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        coordination.PublishCommitted(first);
        coordination.AcknowledgePresented(first);
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest canceledRequest = Request(
            "decision.alice.canceled",
            new Intent(ActionKinds.Observe));
        using var cancellation = new CancellationTokenSource();

        ValueTask<PlayerDecision> canceled = driver.DecideAsync(
            canceledRequest,
            cancellation.Token);
        await terminal.WaitForPromptCountAsync(1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        Assert.False(terminal.TrySubmit(canceledRequest.DecisionId, "observe"));

        DecisionRequest nextRequest = Request(
            "decision.alice.next",
            new Intent(ActionKinds.Observe));
        ValueTask<PlayerDecision> next = driver.DecideAsync(nextRequest, CancellationToken.None);
        await terminal.WaitForPromptCountAsync(2);
        Assert.False(terminal.TrySubmit(canceledRequest.DecisionId, "reverse"));
        Assert.False(next.IsCompleted);
        await terminal.WaitForActiveReadAsync(nextRequest.DecisionId);
        Assert.True(terminal.TrySubmit(nextRequest.DecisionId, "observe"));
        Assert.Equal(nextRequest.DecisionId, (await next).DecisionId);
    }

    [Fact]
    public async Task CoordinationFailureClearsPendingInteractionWithoutShowingPrompt()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        coordination.PublishCommitted(new WorldVersion(17, 1));
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest request = Request(
            "decision.alice.fault",
            new Intent(ActionKinds.Observe));
        var expected = new ArithmeticException("presentation failed");

        ValueTask<PlayerDecision> pending = driver.DecideAsync(request, CancellationToken.None);
        coordination.Fail(expected);

        ArithmeticException actual = await Assert.ThrowsAsync<ArithmeticException>(async () =>
            await pending);
        Assert.Same(expected, actual);
        Assert.Empty(terminal.Prompts);
        Assert.False(terminal.TrySubmit(request.DecisionId, "observe"));
    }

    [Fact]
    public async Task ConcurrentHumanRequestIsRejectedUntilTheCurrentRequestEnds()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        coordination.PublishCommitted(new WorldVersion(17, 1));
        var terminal = new FakeTerminalUi();
        var driver = new PresentationGatedHumanPlayerDriver(coordination, terminal);
        DecisionRequest first = Request("decision.alice.first", new Intent(ActionKinds.Observe));
        DecisionRequest second = Request("decision.alice.second", new Intent(ActionKinds.Observe));
        using var cancellation = new CancellationTokenSource();

        ValueTask<PlayerDecision> current = driver.DecideAsync(first, cancellation.Token);
        ValueTask<PlayerDecision> concurrent = driver.DecideAsync(second, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await concurrent);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await current);
    }

    private static DecisionRequest Request(string decisionId, Intent advertisedIntent)
    {
        IReadOnlyList<ObservedExit> exits = advertisedIntent.ExitId is string exitId
            ? [new ObservedExit(exitId, "exit-destination", 1_000, isAvailable: true)]
            : [];
        IReadOnlyList<string>? actors = advertisedIntent.TargetActorId is string actorId
            ? [actorId]
            : null;
        IReadOnlyList<string>? objects = advertisedIntent.TargetObjectId is string objectId
            ? [objectId]
            : null;
        IReadOnlyList<string>? exitIds = advertisedIntent.ExitId is string selectedExit
            ? [selectedExit]
            : null;
        IReadOnlyList<string>? destinations = advertisedIntent.DestinationId is string destination
            ? [destination]
            : null;
        var observation = new Observation(
            "alice",
            "tavern",
            ModelTimeMs: 0,
            Exits: exits,
            VisibleActorIds: actors ?? [],
            VisibleObjectIds: objects ?? [],
            KnownFacts: []);
        var action = new AvailableAction(
            advertisedIntent.ActionKind,
            CandidateActorIds: actors,
            CandidateObjectIds: objects,
            CandidateExitIds: exitIds,
            CandidateDestinationIds: destinations);
        return new DecisionRequest(
            new DecisionId(decisionId),
            "alice",
            ModelTimeMs: 0,
            observation,
            [action]);
    }
}
