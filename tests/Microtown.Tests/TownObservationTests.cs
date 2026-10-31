using System.Text.Json;
using DramaBoard.Goals;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown.Tests;

public sealed class TownObservationTests {
    [Fact]
    public void Projection_exposes_only_own_private_state_and_copresent_public_identity() {
        TownWorld world = World(characters => characters.Select(actor => actor.Id switch {
            "a" => actor with { Goals = Goals("own-private-plan") },
            "b" => actor with { Money = 84713, Goals = Goals("other-private-plan"),
                Inventory = new ItemInventory(new Dictionary<string, int> { ["wood"] = 173 }),
                NextActivityId = 2, Activity = new(1, TownActivityKind.Wait, new(TownContent.Hour)) },
            _ => actor with { Name = "distant-secret-name" }
        }), places: new Dictionary<string, string> { ["a"] = "market", ["b"] = "market", ["c"] = "home" });

        TownObservation observation = TownObservation.Capture(world, "a");
        string projected = JsonSerializer.Serialize(observation);
        string rendered = TownTextRenderer.Render(observation);
        Assert.Contains("own-private-plan", rendered);
        Assert.DoesNotContain("other-private-plan", projected);
        Assert.DoesNotContain("84713", projected);
        Assert.DoesNotContain("distant-secret-name", projected);
        TownVisibleResident resident = Assert.Single(observation.Residents);
        Assert.Equal("b", resident.Id);
        Assert.Equal("waiting", resident.Activity);
        Assert.Equal(1, observation.Inventory.Count("meal"));
        Assert.Equal(0, observation.Inventory.Count("wood"));
        Assert.DoesNotContain(typeof(TownObservation).GetProperties(), property =>
            property.PropertyType == typeof(TownWorld) || property.PropertyType == typeof(TownBusinessState) ||
            property.PropertyType == typeof(TownCharacter) || typeof(Delegate).IsAssignableFrom(property.PropertyType));
    }

    [Fact]
    public void Remote_market_stock_does_not_affect_actor_material_but_prices_and_schedule_remain_visible() {
        var places = new Dictionary<string, string> { ["a"] = "home", ["b"] = "market", ["c"] = "workshop" };
        TownWorld first = World(places: places);
        var altered = new TownMarket(new Dictionary<string, int> { ["grain"] = 0, ["wood"] = 3 },
            new Dictionary<string, int> { ["meal"] = 1, ["woodware"] = 0 }, first.Business.Market.NextRefresh, 0);
        TownWorld second = World(places: places, market: altered);
        TownObservation observation = TownObservation.Capture(first, "a");
        Assert.Equal(TownTextRenderer.Render(observation), TownTextRenderer.Render(TownObservation.Capture(second, "a")));
        Assert.All(observation.Market, item => { Assert.Null(item.AvailableStock); Assert.Null(item.RemainingDemand); });
        Assert.Equal(2, observation.Market.Single(item => item.ItemId == "grain").BuyPrice);
        Assert.Equal(TownContent.Day, observation.NextMarketRefresh.Ticks);
        Assert.Contains("unknown", TownTextRenderer.Render(observation));
        Assert.Equal("External trading requires the market.", observation.Actions.Single(action => action.Example == "buy grain 1").BlockedReason);

        TownObservation local = TownObservation.Capture(second, "b");
        Assert.Equal(0, local.Market.Single(item => item.ItemId == "grain").AvailableStock);
        Assert.Equal("External stock is insufficient.", local.Actions.Single(action => action.Example == "buy grain 1").BlockedReason);
    }

    [Fact]
    public void Captured_material_remains_unchanged_after_a_successful_world_operation_and_collections_are_readonly() {
        TownWorld before = World(openDecision: "a");
        TownObservation snapshot = TownObservation.Capture(before, "a");
        string material = TownTextRenderer.Render(snapshot);
        TownFact purchase = TownPlanning.Plan(before, "a", new TownControllerOperation("buy grain 2", "call", "operation"));
        Assert.True(purchase.Accepted);
        TownWorld after = TownEngine.Rebuild(purchase.NextBusiness);
        TownObservation current = TownObservation.Capture(after, "a");

        Assert.Equal(material, TownTextRenderer.Render(snapshot));
        Assert.Equal(18, snapshot.Money);
        Assert.Equal(14, current.Money);
        Assert.Equal(0, snapshot.Inventory.Count("grain"));
        Assert.Equal(2, current.Inventory.Count("grain"));
        Assert.Null(snapshot.LastOperationReceipt);
        Assert.Equal("buy grain 2", current.LastOperationReceipt!.Operation.RawInput);
        Assert.Throws<NotSupportedException>(() => ((IList<TownVisibleAction>)snapshot.Actions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<TownVisibleResident>)snapshot.Residents).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<TownVisibleRoute>)snapshot.Routes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, int>)snapshot.Inventory.Items)["grain"] = 999);
    }

    [Theory]
    [InlineData("market")]
    [InlineData("workshop")]
    [InlineData("home")]
    public void Every_advertised_quantity_and_blocked_example_agrees_with_authoritative_rules(string place) {
        TownWorld world = World(characters => characters.Select(actor => actor.Id == "a" ? actor with {
            Inventory = new ItemInventory(new Dictionary<string, int> { ["grain"] = 7, ["wood"] = 5, ["meal"] = 2, ["woodware"] = 1 })
        } : actor), new Dictionary<string, string> { ["a"] = place, ["b"] = "market", ["c"] = "home" });
        TownObservation observation = TownObservation.Capture(world, "a");
        foreach (TownVisibleAction action in observation.Actions) {
            TownCommand command = TownCommand.Parse(action.Example);
            Assert.NotEqual(TownCommandKind.Invalid, command.Kind);
            if (!action.Available) {
                Assert.Equal(0, action.MaximumQuantity);
                Assert.Equal(TownPlanning.Check(world.Business, "a", command), action.BlockedReason);
                continue;
            }
            for (int count = 1; count <= action.MaximumQuantity; count++) {
                Assert.Null(TownPlanning.Check(world.Business, "a", command with { Quantity = count }));
            }
            if (action.HasQuantity) { Assert.NotNull(TownPlanning.Check(world.Business, "a", command with { Quantity = action.MaximumQuantity + 1 })); }
        }
        Assert.Equal(2, observation.Routes.Count);
        Assert.All(observation.Routes, route => Assert.NotEqual(place, route.Destination));
        Assert.Equal(place == "workshop" ? 3 : 0, observation.Actions.Single(action => action.Example == "produce meal 1").MaximumQuantity);
    }

    [Fact]
    public void Personal_receipt_and_experience_are_visible_without_other_residents_receipts() {
        TownWorld world = World(characters => characters.Select(actor => actor with {
            DecisionSequence = 1, OperationSequence = 1,
            LastOperationReceipt = new(actor.Id, new("wait 1", "call-" + actor.Id, "operation-" + actor.Id), true,
                actor.Id == "a" ? "own-success" : "other-secret-receipt", ModelTime.Zero, 1, 1),
            LastExperience = new(TownEventKind.WaitCompleted, ModelTime.Zero,
                actor.Id == "a" ? "own-completion" : "other-secret-experience")
        }));
        string rendered = TownTextRenderer.Render(TownObservation.Capture(world, "a"));
        Assert.Contains("own-success", rendered);
        Assert.Contains("own-completion", rendered);
        Assert.DoesNotContain("other-secret", rendered);
        Assert.Contains("already happened", rendered);
    }

    [Fact]
    public void Rendering_explains_time_food_risk_goals_and_complete_command_shapes_without_mutation() {
        TownWorld world = World(openDecision: "a");
        TownObservation observation = TownObservation.Capture(world, "a");
        string rendered = TownTextRenderer.Render(observation);
        Assert.Contains("Food decreases by 3", rendered);
        Assert.Contains("food exhaustion is due at hour 20", rendered);
        Assert.Contains("produce meal <batches>", rendered);
        Assert.Contains("go market-workshop", rendered);
        Assert.Contains("Production requires the workshop.", rendered);
        Assert.Contains("create-goal", rendered);
        Assert.Contains("criteria", rendered);
        Assert.Contains("reopen-goal", rendered);
        Assert.Contains("not guaranteed valid", rendered);
        Assert.Same(world.Business.Character("a").Goals, observation.Goals);
        Assert.Equal(ModelTime.Zero, world.Time);
        Assert.Equal(0, world.Business.Generation);
        Assert.Equal(rendered, TownTextRenderer.Render(observation));
    }

    private static GoalSet Goals(string title) => GoalRules.Apply(new(),
        new(GoalOperationKind.Create, JsonSerializer.Serialize(new { title, criteria = "self-authored criterion" })), 1, 1).After;

    private static TownWorld World(Func<IEnumerable<TownCharacter>, IEnumerable<TownCharacter>>? change = null,
        IReadOnlyDictionary<string, string>? places = null, TownMarket? market = null, string? openDecision = null) {
        TownBusinessState initial = TownGameFactory.Create().World.Business;
        TownCharacter[] actors = (change is null ? initial.Characters : change(initial.Characters))
            .Select(actor => openDecision == actor.Id ? actor with { DecisionSequence = Math.Max(1, actor.DecisionSequence) } : actor).ToArray();
        var spatial = GraphSpatialState.Create(TownContent.Graph, actors.Select(actor => new EntityPlacement(new(actor.Id),
            new(places?.GetValueOrDefault(actor.Id) ?? "market"))));
        var items = TownContent.Items.ToDictionary(item => item, item => actors.Sum(actor => (long)actor.Inventory.Count(item) +
            (actor.Activity?.InputItem == item ? actor.Activity.InputCount : 0)), StringComparer.Ordinal);
        var business = new TownBusinessState(initial.Time, initial.EndTime, actors, market ?? initial.Market,
            new TownEconomicTotals(actors.Sum(actor => actor.Money), items), spatial, openDecisionActorId: openDecision);
        return TownEngine.Rebuild(business);
    }
}
