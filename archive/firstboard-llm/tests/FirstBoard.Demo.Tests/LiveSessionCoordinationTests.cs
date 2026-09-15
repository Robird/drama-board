namespace DramaBoard.FirstBoard.Demo.Tests;

public sealed class LiveSessionCoordinationTests
{
    [Fact]
    public async Task NonzeroBaselineStartsCaughtUpAndAdvancesOneExactSuffix()
    {
        var baseline = new WorldVersion(17, 41);
        var coordination = new LiveSessionCoordination(baseline);

        LiveFrontierSnapshot initial = coordination.Snapshot();
        Assert.Equal(baseline, initial.Committed);
        Assert.Equal(baseline, initial.Presented);
        Assert.Equal(0, initial.BacklogCount);
        await coordination.WaitUntilPresentedAsync(baseline, CancellationToken.None);

        Assert.Throws<ArgumentException>(() =>
            coordination.PublishCommitted(new WorldVersion(18, 42)));
        Assert.Throws<InvalidOperationException>(() =>
            coordination.PublishCommitted(new WorldVersion(17, 43)));

        var suffix = new WorldVersion(17, 42);
        coordination.PublishCommitted(suffix);
        Assert.Throws<InvalidOperationException>(() =>
            coordination.AcknowledgePresented(baseline));

        LiveFrontierSnapshot pending = coordination.Snapshot();
        Assert.Equal(suffix, pending.Committed);
        Assert.Equal(baseline, pending.Presented);
        Assert.Equal(1, pending.BacklogCount);

        ValueTask wait = coordination.WaitUntilPresentedAsync(suffix, CancellationToken.None);
        coordination.AcknowledgePresented(suffix);
        await wait;

        LiveFrontierSnapshot completed = coordination.Snapshot();
        Assert.Equal(suffix, completed.Committed);
        Assert.Equal(suffix, completed.Presented);
        Assert.Equal(0, completed.BacklogCount);
        Assert.Throws<InvalidOperationException>(() =>
            coordination.AcknowledgePresented(suffix));
    }

    [Fact]
    public async Task FrontiersAdvanceOneCompleteBatchAtATime()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        var second = new WorldVersion(17, 2);

        coordination.PublishCommitted(first);
        coordination.PublishCommitted(second);
        ValueTask wait = coordination.WaitUntilPresentedAsync(second, CancellationToken.None);

        coordination.AcknowledgePresented(first);
        Assert.False(wait.IsCompleted);
        coordination.AcknowledgePresented(second);
        await wait;

        LiveFrontierSnapshot snapshot = coordination.Snapshot();
        Assert.Equal(second, snapshot.Committed);
        Assert.Equal(second, snapshot.Presented);
        Assert.Equal(0, snapshot.BacklogCount);
    }

    [Fact]
    public void InvalidLineageGapRegressionAndPresentedAheadAreRejected()
    {
        var wrongLineage = new LiveSessionCoordination(new WorldVersion(17, 0));
        Assert.Throws<ArgumentException>(() =>
            wrongLineage.PublishCommitted(new WorldVersion(18, 1)));

        var gap = new LiveSessionCoordination(new WorldVersion(17, 0));
        Assert.Throws<InvalidOperationException>(() =>
            gap.PublishCommitted(new WorldVersion(17, 2)));

        var ahead = new LiveSessionCoordination(new WorldVersion(17, 0));
        Assert.Throws<InvalidOperationException>(() =>
            ahead.AcknowledgePresented(new WorldVersion(17, 1)));

        var regression = new LiveSessionCoordination(new WorldVersion(17, 0));
        regression.PublishCommitted(new WorldVersion(17, 1));
        regression.AcknowledgePresented(new WorldVersion(17, 1));
        Assert.Throws<InvalidOperationException>(() =>
            regression.AcknowledgePresented(new WorldVersion(17, 1)));
    }

    [Fact]
    public async Task FailureWakesPresentationWaiterWithOriginalError()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        coordination.PublishCommitted(first);
        ValueTask wait = coordination.WaitUntilPresentedAsync(first, CancellationToken.None);
        var expected = new ArithmeticException("projection failed");

        coordination.Fail(expected);

        ArithmeticException actual = await Assert.ThrowsAsync<ArithmeticException>(async () =>
            await wait);
        Assert.Same(expected, actual);
    }

    [Fact]
    public void FailureStillAllowsAlreadyCommittedPrefixToBePresented()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        coordination.PublishCommitted(first);
        coordination.Fail(new ArithmeticException("later authority step failed"));

        coordination.AcknowledgePresented(first);

        Assert.Equal(first, coordination.Snapshot().Presented);
    }

    [Fact]
    public async Task FailureWinsOverAnAlreadyCaughtUpPresentationWait()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var expected = new ArithmeticException("terminal failed while caught up");
        coordination.Fail(expected);

        ArithmeticException actual = await Assert.ThrowsAsync<ArithmeticException>(async () =>
            await coordination.WaitUntilPresentedAsync(
                new WorldVersion(17, 0),
                CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task WaitCanBeCanceledWithoutPoisoningFutureWaiters()
    {
        var coordination = new LiveSessionCoordination(new WorldVersion(17, 0));
        var first = new WorldVersion(17, 1);
        coordination.PublishCommitted(first);
        using var cancellation = new CancellationTokenSource();
        ValueTask canceledWait = coordination.WaitUntilPresentedAsync(first, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWait);
        ValueTask survivingWait = coordination.WaitUntilPresentedAsync(
            first,
            CancellationToken.None);
        coordination.AcknowledgePresented(first);
        await survivingWait;
    }
}
