using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PlayablePlaygroundTests {
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Finite_batches_deliver_exact_target_without_per_item_decisions(int count) {
        var source = Commands(PlaygroundCommand.Gather("sunflower", count));
        var initial = Solo();
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: source);
        await StepWorldEvent(kernel);
        Assert.Equal(count, kernel.World.GetCharacter("a").Activity!.TargetCount);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(count, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.Equal(PlaygroundGatherStopReason.TargetReached, Assert.Single(kernel.LastCompletion!.Event.Facts).GatherStopReason);
        Assert.Equal(1, source.Calls);
        Assert.Equal(Hour(16 + count), kernel.World.Time);
        Assert.Empty(initial.GetCharacter("a").Backpack.Items);
    }

    [Fact]
    public async Task All_freezes_accepted_stock_even_with_fast_regeneration() {
        var start = Hour(6);
        var source = Commands(PlaygroundCommand.GatherAll("sunflower"));
        var kernel = PlaygroundEngine.CreateKernel(Memory(Solo(start, [Sun(start, regenerationTicks: PlaygroundTime.Hour / 4), Moon(start)])), characterController: source);
        await StepWorldEvent(kernel);
        Assert.True(kernel.World.GetCharacter("a").Activity!.SelectedAll);
        Assert.Equal(3, kernel.World.GetCharacter("a").Activity!.TargetCount);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(3, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Joining_wakes_collector_and_continue_preserves_process_and_candidate() {
        var initial = PlaygroundWorld.CreatePlayable(characters: [new("a", "Alice"), new("b", "Bob")]);
        var source = Commands(PlaygroundCommand.Gather("sunflower", 2), PlaygroundCommand.Gather("sunflower", 2), PlaygroundCommand.Continue());
        var metrics = new PlaygroundMetrics();
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), metrics: metrics, characterController: source);
        await StepWorldEvent(kernel);
        PlaygroundActivity activity = kernel.World.GetCharacter("a").Activity!;
        PlaygroundPrediction prediction = ActivityPrediction(kernel.World, "a");
        long before = Recalculations(metrics, "actor:a:activity");
        await StepWorldEvent(kernel);
        var notice = Assert.Single(kernel.World.GetCharacter("a").Notices);
        Assert.Equal(PlaygroundNoticeKind.CollectorJoined, notice.Kind);
        Assert.Equal("b", notice.ActorId);
        Assert.Same(activity, kernel.World.GetCharacter("a").Activity);
        Assert.Same(prediction, ActivityPrediction(kernel.World, "a"));
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Wait(1)));
        await StepWorldEvent(kernel);
        Assert.Empty(kernel.World.GetCharacter("a").Notices);
        Assert.Same(activity, kernel.World.GetCharacter("a").Activity);
        Assert.Same(prediction, ActivityPrediction(kernel.World, "a"));
        Assert.Equal(before, Recalculations(metrics, "actor:a:activity"));
        Assert.Equal(new[] { "a", "b", "a" }, source.Requests.Select(r => r.Actor));
        Assert.Equal(Hour(16), kernel.World.Time);
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Continue()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stock_depletion_wakes_slow_collector_before_due_with_continue_or_cancel(bool cancel) {
        var start = Hour(6);
        var world = PlaygroundWorld.CreatePlayable(start, [new("a", "Alice"), new("b", "Bob", 2)], [Sun(start, stock: 1), Moon(start)]);
        var source = new StubCharacterController((w, id) => {
            var actor = w.Actor;
            if (actor.Activity is null) { return actor.Backpack.Items.Count == 0 ? PlaygroundCommand.Gather("sunflower") : PlaygroundCommand.Wait(1); }
            return actor.Notices.Any(n => n.Kind == PlaygroundNoticeKind.ResourceDepleted) && cancel ? PlaygroundCommand.Cancel() : PlaygroundCommand.Continue();
        });
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: source);
        await Until(kernel, w => w.GetCharacter("a").Notices.Any(n => n.Kind == PlaygroundNoticeKind.ResourceDepleted));
        Assert.Equal(Minute(400), kernel.World.Time);
        var slow = kernel.World.GetCharacter("a");
        Assert.Equal(Hour(7), slow.Activity!.Due);
        var depletion = Assert.Single(slow.Notices, n => n.Kind == PlaygroundNoticeKind.ResourceDepleted);
        Assert.Equal("b", depletion.ActorId);
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.sunflower"));
        await Until(kernel, w => w.GetCharacter("a").Notices.Count == 0);
        Assert.Equal(Minute(400), kernel.World.Time);
        if (cancel) {
            Assert.Null(kernel.World.GetCharacter("a").Activity);
            Assert.Empty(kernel.World.GetCharacter("a").Backpack.Items);
            Assert.Null(PlaygroundActions.Check(kernel.World, "a", PlaygroundCommand.Gather("moongrass")));
        } else {
            Assert.Same(slow.Activity, kernel.World.GetCharacter("a").Activity);
            await Until(kernel, w => w.GetCharacter("a").Activity is null);
            Assert.Equal(Hour(7), kernel.World.Time);
            Assert.Empty(kernel.World.GetCharacter("a").Backpack.Items);
        }
    }

    [Fact]
    public async Task Self_depletion_alerts_once_and_continue_can_receive_replenishment_before_due() {
        var start = Hour(6);
        var source = Commands(PlaygroundCommand.Gather("sunflower", 2), PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(Memory(Solo(start, [Sun(start, stock: 1, regenerationTicks: 90 * 60_000), Moon(start)])), characterController: source);
        await StepWorldEvent(kernel);
        await Until(kernel, w => w.GetCharacter("a").Notices.Count > 0);
        var actor = kernel.World.GetCharacter("a");
        Assert.Equal(Hour(7), kernel.World.Time);
        Assert.Equal(1, actor.Activity!.CompletedCount);
        Assert.Equal(Hour(8), actor.Activity.Due);
        Assert.Equal(Hour(7), actor.Activity.StartedAt);
        Assert.Equal(PlaygroundNoticeKind.ResourceDepleted, Assert.Single(actor.Notices).Kind);
        await StepWorldEvent(kernel);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(2, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.Equal(2, source.Calls);
        Assert.Empty(kernel.World.GetCharacter("a").Notices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Harvest_preserves_unfinished_regeneration_but_starts_fresh_from_full(bool initiallyFull) {
        var start = Hour(6);
        var world = Solo(start, [Sun(start, stock: initiallyFull ? 5 : 3), Moon(start)]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.Gather("sunflower")));
        await StepWorldEvent(kernel);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(initiallyFull ? Hour(7) : Hour(6), kernel.World.GetResource("sunflower").ActiveSince);
        Assert.Equal(initiallyFull ? Hour(10) : Hour(9), Assert.Single(kernel.World.Forecasts.Predictions, p => p.OwnerId == "resource:sunflower:regen").Due);
    }

    [Fact]
    public async Task Round_robin_assignments_are_followed_by_asynchronous_wait_completions() {
        var source = new StubCharacterController((_, id) => PlaygroundCommand.Wait(id == "a" ? 6 : id == "b" ? 1 : 3));
        var kernel = PlaygroundEngine.CreateKernel(Memory(PlaygroundWorld.CreatePlayable()), characterController: source);
        await StepWorldEvent(kernel);
        await StepWorldEvent(kernel);
        await StepWorldEvent(kernel);
        Assert.Equal(new[] { "a", "b", "c" }, source.Requests.Select(r => r.Actor));
        await Until(kernel, w => w.GetCharacter("b").Activity is null);
        Assert.Equal(Hour(17), kernel.World.Time);
        Assert.NotNull(kernel.World.GetCharacter("a").Activity);
        Assert.NotNull(kernel.World.GetCharacter("c").Activity);
        await StepWorldEvent(kernel);
        Assert.Equal("b", source.Requests.Last().Actor);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("zero-count")]
    [InlineData("negative-count")]
    [InlineData("skill")]
    [InlineData("wait-zero")]
    [InlineData("wait-long")]
    [InlineData("continue")]
    [InlineData("cancel")]
    public async Task Invalid_actions_publish_only_rejection_and_inspection_remains_read_only(string invalid) {
        var start = Hour(6);
        var initial = Solo(start, [Sun(start, stock: invalid == "empty" ? 0 : 3, requiredLevel: invalid == "skill" ? 1 : 0), Moon(start)]);
        PlaygroundCommand command = invalid switch {
            "unknown" => PlaygroundCommand.Gather("missing"), "zero-count" => PlaygroundCommand.Gather("sunflower", 0),
            "negative-count" => PlaygroundCommand.Gather("sunflower", -1), "wait-zero" => PlaygroundCommand.Wait(0),
            "wait-long" => PlaygroundCommand.Wait(25), "continue" => PlaygroundCommand.Continue(), "cancel" => PlaygroundCommand.Cancel(),
            _ => PlaygroundCommand.GatherAll("sunflower")
        };
        var history = Memory(initial);
        string before = initial.DescribeState();
        Assert.NotNull(PlaygroundActions.Check(initial, "a", command));
        Assert.NotNull(PlaygroundActions.Check(initial, "unknown", PlaygroundCommand.Wait(1)));
        Assert.NotEmpty(PlaygroundDiagnostics.Render(initial));
        Assert.Equal(before, initial.DescribeState());
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(command));
        await Step(kernel);
        PlaygroundWorld opened = kernel.World;
        await Step(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, Assert.Single(history.CompletedEvents[^1].Facts).Kind);
        Assert.Equal(2, history.CompletedEvents.Count);
        Assert.Equal(opened.Time, history.State.Time);
        Assert.Equal(opened.Resources, history.State.Resources);
        Assert.Same(opened.GetCharacter("a").Backpack, history.State.GetCharacter("a").Backpack);
        Assert.Null(history.State.GetCharacter("a").Activity);
        Assert.Equal(opened.GetCharacter("a").DecisionGeneration + 1, history.State.GetCharacter("a").DecisionGeneration);
        Assert.Null(history.State.Business.LastServedActorId);
        Assert.True(Assert.Single(history.State.Business.InteractionStates["a"].CurrentContext!.Items).IsRejected);
        Assert.Null(history.PendingEvent);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 48)]
    [InlineData(2, 40)]
    public async Task Skill_formula_uses_exact_safe_integer_duration(int skill, int minutes) {
        var kernel = PlaygroundEngine.CreateKernel(Memory(Solo(skill: skill)), characterController: Commands(PlaygroundCommand.Gather("sunflower")));
        await StepWorldEvent(kernel);
        Assert.Equal(minutes * 60_000L, kernel.World.GetCharacter("a").Activity!.DurationTicks);
        Assert.Equal(Minute(960 + minutes), kernel.World.GetCharacter("a").Activity!.Due);
    }

    [Fact]
    public async Task Waiting_for_external_input_does_not_advance_or_publish_and_cancellation_leaves_no_fact() {
        var initial = Solo();
        var history = Memory(initial);
        var source = new GatedCharacterController();
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: source);
        await Step(kernel);
        PlaygroundWorld opened = kernel.World;
        KernelCursor openedCursor = kernel.Cursor;
        using var cancellation = new CancellationTokenSource();
        Task<StepStatus> operation = kernel.StepAsync(Hour(96), cancellation.Token).AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.Same(opened, kernel.World);
        Assert.Equal(openedCursor, kernel.Cursor);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(Assert.Single(history.CompletedEvents).Facts).Kind);
        Assert.Null(history.PendingEvent);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Same(opened, kernel.World);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, Assert.Single(Assert.Single(history.CompletedEvents).Facts).Kind);
        Assert.Null(history.PendingEvent);
    }

    [Fact]
    public async Task Incremental_and_full_playable_traces_match_at_every_boundary() {
        static StubCharacterController Driver() => new((w, id) => w.Actor.Activity is not null ? PlaygroundCommand.Continue()
            : w.Resources.Single(resource => resource.Id == "sunflower").Stock > 0 ? PlaygroundCommand.Gather("sunflower", 2) : PlaygroundCommand.Wait(3));
        var ih = Memory(PlaygroundWorld.CreatePlayable());
        var fh = Memory(PlaygroundWorld.CreatePlayable());
        var im = new PlaygroundMetrics();
        var fm = new PlaygroundMetrics();
        var incremental = PlaygroundEngine.CreateKernel(ih, PredictionMode.Incremental, im, Driver());
        var full = PlaygroundEngine.CreateKernel(fh, PredictionMode.Full, fm, Driver());
        for (int i = 0; i < 100; i++) {
            await StepWorldEvent(incremental);
            await StepWorldEvent(full);
            Assert.Equal(full.World.DescribeState(), incremental.World.DescribeState());
            Assert.Equal(full.Cursor, incremental.Cursor);
            Assert.Equal(Describe(full.LastCompletion!.Event), Describe(incremental.LastCompletion!.Event));
        }
        Assert.True(im.Recalculations < fm.Recalculations);
        Assert.Contains(ih.CompletedEvents.SelectMany(e => e.Facts), f => f.Kind == PlaygroundEventKind.LightChanged);
    }

    [Fact]
    public async Task Cold_reopen_at_every_multiplayer_boundary_preserves_rotation_perceptions_and_trace() {
        static StubCharacterController Driver() => new((w, id) => w.Actor.Activity is not null ? PlaygroundCommand.Continue()
            : w.Resources.Single(resource => resource.Id == "sunflower").Stock > 0 ? PlaygroundCommand.GatherAll("sunflower") : PlaygroundCommand.Wait(3));
        var initial = PlaygroundWorld.CreatePlayable();
        var baselineSource = Driver();
        var resumedSource = Driver();
        var baseline = PlaygroundEngine.CreateKernel(Memory(initial), characterController: baselineSource);
        using var directory = new TemporaryStore();
        using (PlaygroundDurableHistory.Create(directory.StorePath, initial, InitialCursor(initial))) { }
        for (int i = 0; i < 50; i++) {
            await StepWorldEvent(baseline);
            using var history = PlaygroundDurableHistory.Open(directory.StorePath);
            AssertPredictions(history.State);
            var resumed = PlaygroundEngine.CreateKernel(history, characterController: resumedSource);
            await StepWorldEvent(resumed);
            Assert.Equal(baseline.World.DescribeState(), resumed.World.DescribeState());
            Assert.Equal(baseline.Cursor, resumed.Cursor);
            Assert.Equal(Describe(baseline.LastCompletion!.Event), Describe(resumed.LastCompletion!.Event));
            Assert.Null(history.PendingEvent);
        }
        Assert.Equal(baselineSource.Requests, resumedSource.Requests);
        using var final = PlaygroundDurableHistory.Open(directory.StorePath);
        Assert.Equal(baseline.World.DescribeState(), final.State.DescribeState());
        Assert.Equal(baseline.Cursor, final.Cursor);
    }
    [Fact]
    public async Task Regeneration_and_light_change_at_harvest_due_settle_before_delivery() {
        var start = Hour(15);
        var history = Memory(Solo(start, [Sun(start, harvestTicks: 3 * PlaygroundTime.Hour), Moon(start)]));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Gather("sunflower")));
        await StepWorldEvent(kernel);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(Hour(18), kernel.World.Time);
        Assert.Equal(new[] { PlaygroundEventKind.ResourceRegenerated, PlaygroundEventKind.LightChanged, PlaygroundEventKind.GatherYielded },
            history.CompletedEvents.Where(e => e.TargetInstant.ModelTime == Hour(18)).SelectMany(e => e.Facts).Select(f => f.Kind));
        Assert.Equal(3, kernel.World.GetResource("sunflower").Stock);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.False(kernel.World.Light.IsDay);
    }

    [Fact]
    public async Task Batch_can_request_more_than_stock_but_first_empty_completion_ends_it() {
        var start = Hour(6);
        var source = Commands(PlaygroundCommand.Gather("sunflower", 5), PlaygroundCommand.Continue());
        var kernel = PlaygroundEngine.CreateKernel(Memory(Solo(start, [Sun(start, stock: 1), Moon(start)])), characterController: source);
        await StepWorldEvent(kernel);
        Assert.Equal(5, kernel.World.GetCharacter("a").Activity!.TargetCount);
        await Until(kernel, w => w.GetCharacter("a").Activity is null);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.sunflower"));
        Assert.Equal(2, source.Calls);
        Assert.Equal(PlaygroundGatherStopReason.ResourceEmpty, Assert.Single(kernel.LastCompletion!.Event.Facts).GatherStopReason);
    }
    private static PlaygroundPrediction ActivityPrediction(PlaygroundWorld world, string actor) => Assert.Single(world.Forecasts.Predictions, p => p.OwnerId == $"actor:{actor}:activity");
    private sealed class GatedCharacterController : IPlaygroundCharacterController {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
