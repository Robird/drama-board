using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Microtown.Tests;

public sealed class TownSocialAcceptanceTests {
    [Theory]
    [InlineData("give b meal 1", 18, 18, 0, 2)]
    [InlineData("give b money 5", 13, 23, 1, 1)]
    public async Task Gift_publishes_both_sides_and_private_records_atomically_without_external_flow(
        string command, long moneyA, long moneyB, int mealsA, int mealsB) {
        var play = new Play();
        TownBusinessState before = play.State;
        TownFact fact = await play.Act("a", command);
        Assert.True(fact.Accepted, fact.Result);
        Assert.Equal(moneyA, play.State.Character("a").Money);
        Assert.Equal(moneyB, play.State.Character("b").Money);
        Assert.Equal(mealsA, play.State.Character("a").Inventory.Count("meal"));
        Assert.Equal(mealsB, play.State.Character("b").Inventory.Count("meal"));
        Assert.Equal(54, play.State.Characters.Sum(actor => actor.Money));
        Assert.Equal(3, play.State.Characters.Sum(actor => actor.Inventory.Count("meal")));
        Assert.Equal(0, play.State.Economy.ImportExpense);
        Assert.Equal(0, play.State.Economy.ExportIncome);
        Assert.Empty(play.State.Economy.Imported);
        Assert.Empty(play.State.Economy.Exported);
        var sent = Assert.Single(play.State.Character("a").Interaction.Entries);
        var received = Assert.Single(play.State.Character("b").Interaction.Entries);
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal("a", received.SenderId);
        Assert.Equal("b", received.RecipientId);
        Assert.Empty(play.State.Character("c").Interaction.Entries);
        Assert.Equal(1, play.State.Character("b").Interaction.PendingCount);
        Assert.Equal(0, play.State.Time.Ticks);
        Assert.All(before.Characters, actor => {
            Assert.Equal(18, actor.Money);
            Assert.Equal(1, actor.Inventory.Count("meal"));
            Assert.Empty(actor.Interaction.Entries);
        });
    }

    [Theory]
    [InlineData("give b meal 2")]
    [InlineData("give b money 19")]
    [InlineData("give b unknown 1")]
    [InlineData("give a meal 1")]
    [InlineData("give missing money 1")]
    [InlineData("give b money 0")]
    public async Task Refused_gift_preserves_both_balances_and_does_not_send_a_notification(string command) {
        var play = new Play();
        TownBusinessState before = play.State;
        TownFact fact = await play.Act("a", command);
        Assert.False(fact.Accepted);
        Assert.Equal("a", play.State.OpenDecisionActorId);
        Assert.Equal(0, play.State.Time.Ticks);
        Assert.All(play.State.Characters, actor => {
            Assert.Equal(before.Character(actor.Id).Inventory.Items, actor.Inventory.Items);
            Assert.Equal(before.Character(actor.Id).Money, actor.Money);
            Assert.Empty(actor.Interaction.Entries);
            Assert.False(actor.Interaction.NeedsResponse);
        });
    }

    [Fact]
    public async Task Two_unread_messages_survive_rendering_opening_and_busy_instant_action_until_explicit_continue() {
        var play = new Play();
        Assert.True((await play.Act("a", "wait 2")).Accepted);
        TownActivity activity = play.State.Character("a").Activity!;
        Assert.True((await play.Act("b", "say a first private message")).Accepted);
        Assert.True((await play.Act("c", "say a second private message")).Accepted);
        TownCharacter unread = play.State.Character("a");
        Assert.Equal(2, unread.Interaction.PendingCount);
        Assert.Equal(0, unread.Interaction.DeliveredThrough);
        Assert.Equal(2, unread.Interaction.Entries.Count);
        for (int repeat = 0; repeat < 3; repeat++) {
            TownObservation view = TownObservation.Capture(play.History.State, "a");
            _ = TownTextRenderer.Render(view);
        }
        Assert.Same(unread, play.State.Character("a"));
        Assert.Equal(2, unread.Interaction.PendingCount);

        await play.Open("a");
        TownCharacter opened = play.State.Character("a");
        Assert.Equal(2, opened.Interaction.DeliveredThrough);
        Assert.Equal(0, opened.Interaction.OpeningAfter);
        Assert.Equal(0, opened.Interaction.PendingCount);
        Assert.True(opened.Interaction.NeedsResponse);
        Assert.Equal(activity, opened.Activity);
        string rendered = TownTextRenderer.Render(TownObservation.Capture(play.History.State, "a"));
        Assert.Contains("first private message", rendered);
        Assert.Contains("second private message", rendered);
        Assert.Equal(2, unread.Interaction.PendingCount); // The earlier world is still immutable.

        Assert.True((await play.Act("a", "eat")).Accepted);
        Assert.True(play.State.Character("a").Interaction.NeedsResponse);
        Assert.Equal(activity, play.State.Character("a").Activity);
        Assert.True((await play.Act("b", "wait 3")).Accepted);
        Assert.True((await play.Act("c", "wait 3")).Accepted);
        Assert.True((await play.Act("a", "continue")).Accepted);
        Assert.False(play.State.Character("a").Interaction.NeedsResponse);
        Assert.Equal(activity, play.State.Character("a").Activity);
        Assert.Equal(0, play.State.Time.Ticks);
        await play.Open("a");
        Assert.Equal(activity.Due, play.State.Time);
        Assert.Null(play.State.Character("a").Activity);
        Assert.Equal(2, play.State.Character("a").Interaction.Entries.Count);
    }

    [Fact]
    public async Task Remote_speech_and_gift_are_refused_while_a_resident_is_in_transit() {
        var play = new Play();
        Assert.True((await play.Act("a", "go market-workshop")).Accepted);
        Assert.False((await play.Act("b", "say a remote message")).Accepted);
        Assert.False((await play.Act("b", "give a meal 1")).Accepted);
        Assert.All(play.State.Characters, actor => Assert.Empty(actor.Interaction.Entries));
        Assert.Equal(1, play.State.Character("a").Inventory.Count("meal"));
        Assert.Equal(1, play.State.Character("b").Inventory.Count("meal"));
        Assert.Equal(0, play.State.Time.Ticks);
    }

    [Fact]
    public async Task Money_gifts_use_long_amounts_without_the_item_quantity_limit() {
        var start = TownGameFactory.Create(4);
        var actors = start.World.Business.Characters.Select(actor => actor.Id == "a" ? actor with { Money = 3_000_000_000 } : actor).ToArray();
        var play = new Play(Seed(start.World.Business, actors), start.Cursor);
        Assert.True((await play.Act("a", "give b money 2147483648")).Accepted);
        Assert.Equal(852_516_352, play.State.Character("a").Money);
        Assert.Equal(2_147_483_666, play.State.Character("b").Money);
        Assert.Equal(3_000_000_036, play.State.Characters.Sum(actor => actor.Money));
        Assert.Equal(2_147_483_648, Assert.Single(play.State.Character("b").Interaction.Entries).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gifts_reserve_future_output_or_refund_space_and_rejection_does_not_break_later_settlement(bool cancel) {
        var start = TownGameFactory.Create(4);
        TownCharacter[] actors = start.World.Business.Characters.Select(actor => actor.Id switch {
            "a" => actor with { Inventory = new ItemInventory(new Dictionary<string, int> {
                ["grain"] = cancel ? int.MaxValue - 2 : 2,
                ["meal"] = cancel ? 1 : int.MaxValue - 3
            }) },
            "b" => actor with { Inventory = new ItemInventory(new Dictionary<string, int> { ["grain"] = 3, ["meal"] = 2 }) },
            _ => actor
        }).ToArray();
        var play = new Play(Seed(start.World.Business, actors), start.Cursor);
        Assert.True((await play.Act("a", "go market-workshop")).Accepted);
        Assert.True((await play.Act("b", "go market-workshop")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "produce meal 1")).Accepted);
        TownActivity activity = play.State.Character("a").Activity!;
        string item = cancel ? "grain" : "meal";
        int acceptedQuantity = cancel ? 2 : 1;
        Assert.True((await play.Act("b", $"give a {item} {acceptedQuantity}")).Accepted);
        Assert.True((await play.Act("a", "continue")).Accepted);
        TownBusinessState beforeRejected = play.State;
        Assert.False((await play.Act("b", $"give a {item} 1")).Accepted);
        foreach (string id in new[] { "a", "b" }) {
            Assert.Equal(beforeRejected.Character(id).Inventory.Items, play.State.Character(id).Inventory.Items);
            Assert.Equal(beforeRejected.Character(id).Interaction.LastSequence, play.State.Character(id).Interaction.LastSequence);
        }
        Assert.Equal(activity, play.State.Character("a").Activity);
        if (cancel) {
            Assert.True((await play.Act("b", "say a please cancel")).Accepted);
            Assert.True((await play.Act("a", "cancel")).Accepted);
            Assert.Null(play.State.Character("a").Activity);
            Assert.Equal(int.MaxValue, play.State.Character("a").Inventory.Count("grain"));
            Assert.Empty(play.State.Economy.Produced);
            Assert.Empty(play.State.Economy.RecipeInputs);
            Assert.True((await play.Act("b", "wait 4")).Accepted);
            Assert.False((await play.Act("a", "cancel")).Accepted);
            Assert.Equal(int.MaxValue, play.State.Character("a").Inventory.Count("grain"));
        } else {
            Assert.True((await play.Act("b", "wait 4")).Accepted);
            await play.Open("a");
            Assert.Equal(activity.Due, play.State.Time);
            Assert.Null(play.State.Character("a").Activity);
            Assert.Equal(int.MaxValue, play.State.Character("a").Inventory.Count("meal"));
            Assert.Equal(2, play.State.Economy.Produced["meal"]);
            Assert.Equal(2, play.State.Economy.RecipeInputs["grain"]);
        }
        play.State.Economy.Verify(play.State);
    }

    [Fact]
    public async Task Recipient_capacity_includes_future_production_and_refund_not_just_current_inventory() {
        var start = TownGameFactory.Create(4);
        var actors = start.World.Business.Characters.Select(actor => actor.Id switch {
            "a" => actor with { Inventory = new ItemInventory(new Dictionary<string, int> { ["grain"] = 2, ["meal"] = 1 }, unitCapacity: 3) },
            "b" => actor with { Inventory = actor.Inventory.Add("wood") },
            _ => actor
        }).ToArray();
        var play = new Play(Seed(start.World.Business, actors), start.Cursor);
        Assert.True((await play.Act("a", "go market-workshop")).Accepted);
        Assert.True((await play.Act("b", "go market-workshop")).Accepted);
        Assert.True((await play.Act("c", "wait 4")).Accepted);
        Assert.True((await play.Act("a", "produce meal 1")).Accepted);
        Assert.Equal(InventoryAdmission.Allowed, play.State.Character("a").Inventory.CheckAdd("wood"));
        Assert.False((await play.Act("b", "give a wood 1")).Accepted);
        Assert.Equal(1, play.State.Character("b").Inventory.Count("wood"));
        Assert.Equal(0, play.State.Character("a").Inventory.Count("wood"));
        Assert.True((await play.Act("b", "wait 4")).Accepted);
        await play.Open("a");
        Assert.Equal(3, play.State.Character("a").Inventory.TotalCount);
        Assert.Equal(3, play.State.Character("a").Inventory.Count("meal"));
    }

    private static TownWorld Seed(TownBusinessState before, TownCharacter[] actors) {
        var items = TownContent.Items.ToDictionary(item => item, item => actors.Sum(actor => (long)actor.Inventory.Count(item)));
        return TownEngine.Rebuild(new TownBusinessState(before.Time, before.EndTime, actors, before.Market,
            new TownEconomicTotals(actors.Sum(actor => actor.Money), items), before.Spatial));
    }

    private sealed class Play {
        public InMemoryOccurrenceHistory<TownWorld, TownFact> History { get; }
        public TownBusinessState State => History.State.Business;
        private readonly SimulationKernel<TownWorld, TownPrediction, TownFact> _kernel;
        private string? _expectedActor, _command;
        private int _calls;
        public Play() : this(TownGameFactory.Create(4)) { }
        private Play((TownWorld World, KernelCursor Cursor) initial) : this(initial.World, initial.Cursor) { }
        public Play(TownWorld world, KernelCursor cursor) {
            History = new(world, cursor);
            _kernel = TownEngine.CreateControlledKernel(History, (observation, _) => {
                Assert.Equal(_expectedActor, observation.ActorId);
                Assert.NotNull(_command);
                _calls++;
                return ValueTask.FromResult(new TownControllerOperation(_command!, $"social-tool-{_calls}", $"social-op-{_calls}"));
            });
        }
        public async Task<TownFact> Act(string actor, string command) {
            _expectedActor = actor; _command = command;
            int before = _calls;
            for (int step = 0; step < 100; step++) {
                Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(State.EndTime));
                State.Economy.Verify(State);
                if (_calls != before) {
                    _command = null;
                    return Assert.Single(_kernel.LastCompletion!.Event.Facts);
                }
            }
            throw new InvalidOperationException("Expected controller was not reached within 100 occurrences.");
        }
        public async Task Open(string actor) {
            _expectedActor = null; _command = null;
            for (int step = 0; step < 100; step++) {
                if (State.OpenDecisionActorId is { } open) { Assert.Equal(actor, open); return; }
                Assert.Equal(StepStatus.Committed, await _kernel.StepAsync(State.EndTime));
                State.Economy.Verify(State);
            }
            Assert.Fail("Expected published opening was not reached within 100 occurrences.");
        }
    }
}
