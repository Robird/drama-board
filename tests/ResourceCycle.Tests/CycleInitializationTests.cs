using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class CycleInitializationTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_games_own_independent_entity_subgraphs(bool playable) {
        var first = Create(playable, Hour(16));
        var second = Create(playable, Hour(16));
        Assert.Equal(first.World.DescribeState(), second.World.DescribeState());
        Assert.Equal(first.Cursor, second.Cursor);
        Assert.NotSame(first.World.Business, second.World.Business);
        Assert.NotSame(first.World.Light, second.World.Light);
        Assert.NotSame(first.World.Script, second.World.Script);
        Assert.NotSame(first.World.Forecasts, second.World.Forecasts);
        foreach (var pair in first.World.Resources.Zip(second.World.Resources)) {
            Assert.NotSame(pair.First, pair.Second);
        }
        foreach (var pair in first.World.Characters.Zip(second.World.Characters)) {
            Assert.NotSame(pair.First, pair.Second);
            Assert.NotSame(pair.First.Backpack, pair.Second.Backpack);
        }
        foreach (var pair in first.World.Script.Entries.Zip(second.World.Script.Entries)) {
            Assert.NotSame(pair.First, pair.Second);
        }
        if (playable) {
            CycleBackpack[] backpacks = first.World.Characters.Select(actor => actor.Backpack).ToArray();
            Assert.Equal(backpacks.Length, backpacks.Distinct(ReferenceEqualityComparer.Instance).Count());
        }
    }

    [Fact]
    public void Supplied_entities_are_retained_but_input_sequences_are_copied() {
        var actor = new CycleCharacter("custom", "Custom", backpack: new(new Dictionary<string, int> { ["keepsake"] = 2 }));
        var resource = Sun(Hour(9));
        var actors = new List<CycleCharacter> { actor };
        var resources = new List<CycleResource> { resource };
        var playable = CycleGameFactory.CreatePlayable(Hour(9), actors, resources);
        actors.Clear();
        resources.Clear();
        Assert.Same(actor, Assert.Single(playable.World.Characters));
        Assert.Same(resource, Assert.Single(playable.World.Resources));
        Assert.Same(actor.Backpack, playable.World.GetCharacter("custom").Backpack);

        var destruction = new CycleDestruction("sunflower", Hour(10));
        var script = new List<CycleDestruction> { destruction };
        var batch = CycleGameFactory.CreateBatch(Hour(9), script);
        script.Clear();
        Assert.Same(destruction, Assert.Single(batch.World.Script.Entries));
        Assert.Equal(Hour(9), playable.Cursor.GenesisTime);
        Assert.Equal(Hour(9), batch.Cursor.GenesisTime);
    }

    [Fact]
    public void Factory_rejects_invalid_initial_business_before_returning_a_boundary() {
        Assert.Throws<InvalidOperationException>(() => CycleGameFactory.CreatePlayable(Hour(10),
            resources: [Sun(Hour(6))])); // Its regeneration deadline already elapsed.
        Assert.Throws<InvalidOperationException>(() => CycleGameFactory.CreatePlayable(Hour(9),
            characters: [new("same", "First"), new("same", "Second")]));
        Assert.Throws<InvalidOperationException>(() => CycleGameFactory.CreateBatch(Hour(9),
            [new("missing-resource", Hour(10))]));
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(false, 20)]
    [InlineData(true, 9)]
    [InlineData(true, 20)]
    public async Task Memory_and_cold_opened_new_game_have_identical_boundaries_and_continuations(bool playable, long startHour) {
        using var store = new TemporaryStore();
        var initial = Create(playable, Hour(startHour));
        var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        using (var created = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            Assert.Equal(memory.State.DescribeState(), created.State.DescribeState());
            Assert.Equal(memory.Cursor, created.Cursor);
            Assert.Null(created.PendingEvent);
        }

        using var reopened = CycleDurableHistory.Open(store.StorePath);
        Assert.NotSame(memory.State.Business, reopened.State.Business);
        Assert.Equal(memory.State.DescribeState(), reopened.State.DescribeState());
        Assert.Equal(memory.Cursor, reopened.Cursor);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal(0, reopened.Cursor.Version.TransitionCount);
        Assert.Equal(Hour(startHour), reopened.Cursor.CurrentModelTime);
        AssertPredictions(reopened.State);

        var expected = CycleEngine.CreateKernel(memory,
            characterController: new StubCharacterController((_, _) => CycleCommand.Wait(1)));
        var actual = CycleEngine.CreateKernel(reopened,
            characterController: new StubCharacterController((_, _) => CycleCommand.Wait(1)));
        Assert.False(actual.RecoverPending());
        for (int i = 0; i < 8; i++) {
            Assert.Equal(StepStatus.Committed, await expected.StepAsync(Hour(96)));
            Assert.Equal(StepStatus.Committed, await actual.StepAsync(Hour(96)));
            Assert.Equal(expected.World.DescribeState(), actual.World.DescribeState());
            Assert.Equal(expected.Cursor, actual.Cursor);
            Assert.Equal(Describe(expected.LastCompletion!.Event), Describe(actual.LastCompletion!.Event));
        }
    }

    private static (CycleWorld World, KernelCursor Cursor) Create(bool playable, ModelTime start) =>
        playable ? CycleGameFactory.CreatePlayable(start) : CycleGameFactory.CreateBatch(start);
}
