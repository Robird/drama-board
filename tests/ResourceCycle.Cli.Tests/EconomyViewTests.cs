using System.Globalization;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class EconomyViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_market_explains_payment_direction_inventory_demand_and_unsupported_directions(bool decision) {
        CycleWorld world = await RunUntil(["go market-home"], world => OpenAt(world, "market"));
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        Assert.NotEmpty(observation.MarketQuotes);
        Assert.Empty(observation.Recipes);
        string text = Layout(decision).Render(observation);
        Assert.Contains($"余额：{observation.Actor.Money} 钱", text);
        Assert.Contains("买入单价", text);
        Assert.Contains("卖出单价", text);
        Assert.Contains("可买库存", text);
        Assert.Contains("剩余收购额度", text);
        Assert.Contains("不出售", text);
        Assert.Contains("不收购", text);
        foreach (CycleMarketQuote quote in observation.MarketQuotes) {
            string row = Assert.Single(text.Split('\n'), line => line.StartsWith("| ", StringComparison.Ordinal) &&
                line.Contains($"`{quote.ItemId}`", StringComparison.Ordinal));
            Assert.Contains($"{quote.Stock} 份", row);
            Assert.Contains($"{quote.Demand} 份", row);
            if (quote.BuyPrice > 0) { Assert.Contains($"{quote.BuyPrice} 钱", row); }
            if (quote.SellPrice > 0) { Assert.Contains($"{quote.SellPrice} 钱", row); }
        }
        string help = Layout(decision).RenderActionHelp(observation);
        Assert.Contains("buy <item-id> <count>", help);
        Assert.Contains("sell <item-id> <count>", help);
        Assert.Contains("不取消活动", help);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workshop_lists_frozen_recipe_inputs_outputs_duration_and_cancellation_contract(bool decision) {
        CycleWorld world = await RunUntil(["go home-workshop"], world => OpenAt(world, "workshop"));
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        Assert.NotEmpty(observation.Recipes);
        Assert.Empty(observation.MarketQuotes);
        string text = Layout(decision).Render(observation);
        Assert.Contains("退回", text);
        Assert.Contains("预留投入", text);
        Assert.Contains("不产出成品", text);
        foreach (CycleRecipe recipe in observation.Recipes) {
            string row = Assert.Single(text.Split('\n'), line => line.StartsWith($"- `{recipe.Id}`：", StringComparison.Ordinal));
            Assert.Contains($"`{recipe.InputItem}` ×{recipe.InputCount}", row);
            Assert.Contains($"`{recipe.OutputItem}` ×{recipe.OutputCount}", row);
            Assert.Contains(((decimal)recipe.DurationTicks / 60_000).ToString("0.##", CultureInfo.InvariantCulture) + " 分钟", row);
        }
        Assert.Contains("produce <recipe-id> <batches>", Layout(decision).RenderActionHelp(observation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Personal_funds_are_visible_without_disclosing_another_residents_money_or_inventory(bool decision) {
        var initial = CycleGameFactory.CreateLivingWorld(enableNeeds: false, characterCount: 2);
        var self = new CycleCharacter("a", "阿青", money: 17);
        var other = new CycleCharacter("b", "阿白", backpack: new ItemInventory(new Dictionary<string, int> {
            ["item.woodware"] = 9347
        }), money: 987654321);
        var economy = initial.World.Business.Economy!.Change(totals: new CycleEconomicTotals(
            self.Money + other.Money, new Dictionary<string, long> { ["item.woodware"] = 9347 }));
        CycleWorld world = CycleEngine.Rebuild(initial.World.Business.Replace(initial.World.Time,
            changedCharacters: [self, other], economy: economy), initial.World.Time);
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        string rendered = Layout(decision).Render(observation);
        Assert.Contains("余额：17 钱", rendered);
        Assert.DoesNotContain("987654321", rendered);
        Assert.DoesNotContain("item.woodware", rendered);
        Assert.DoesNotContain("9347", rendered);
        Assert.Empty(observation.MarketQuotes);
        Assert.Empty(observation.Recipes);
        Assert.DoesNotContain("## 当前市场", rendered);
        Assert.DoesNotContain("## 当前工坊配方", rendered);
        CycleViewSnapshot snapshot = CycleViewSnapshot.Create(observation);
        Assert.Equal(17, snapshot.Values[new("money", "a", "balance")].Number);
        Assert.DoesNotContain(snapshot.Values.Keys, key => key.ObjectId == "b" && key.Category == "money");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_progress_production_shows_reserved_inputs_output_and_original_deadline(bool decision) {
        CycleWorld world = await RunUntil(["go home-workshop", "produce ration 1"], world => world.GetCharacter("a").Activity?.Kind == CycleActivityKind.Produce);
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        CycleActivity activity = Assert.IsType<CycleActivity>(observation.Actor.Activity);
        CycleProductionOrder production = Assert.IsType<CycleProductionOrder>(activity.Production);
        string rendered = Layout(decision).Render(observation);
        Assert.Contains("正在生产", rendered);
        Assert.Contains($"`{production.RecipeId}`", rendered);
        Assert.Contains($"`{production.InputItem}` ×{production.InputCount}", rendered);
        Assert.Contains($"`{production.OutputItem}` ×{production.OutputCount}", rendered);
        Assert.Contains(CycleUpdateRenderer.Format(CycleVisibleValue.Numeric(activity.Due.Ticks, "ticks")), rendered);
        Assert.Contains("不重置生产期限", rendered);
        Assert.Contains("cancel", rendered);
        Assert.Contains("退回预留投入", rendered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Produced_ration_is_a_visible_usable_supply(bool decision) {
        CycleWorld world = await RunUntil(["go home-workshop", "produce ration 1"], world =>
            world.GetCharacter("a").Backpack.Count("item.ration") > 0 && OpenAt(world, "workshop"));
        CycleObservation observation = CycleDecisionContext.Observe(world, "a");
        Assert.Null(observation.CheckDisplayed(CycleCommand.Use("item.ration")));
        string help = Layout(decision).RenderActionHelp(observation);
        Assert.Contains("use item.ration", help);
        Assert.Contains("口粮", help);
        Assert.Contains("55", help);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Immediate_purchase_delta_updates_personal_balance_and_local_market_stock(bool decision) {
        CycleObservation before = CycleDecisionContext.Observe(
            await RunUntil(["go market-home"], world => OpenAt(world, "market")), "a");
        CycleObservation after = CycleDecisionContext.Observe(
            await RunUntil(["go market-home", "buy item.food 1"], world => world.GetCharacter("a").Money == 9), "a");
        var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(before), CycleViewSnapshot.Create(after), [],
            PlayDeliveryPhase.ControllerInput);
        Assert.Equal(9, update.CurrentValues[new("money", "a", "balance")].Number);
        Assert.Equal(35, update.CurrentValues[new("market", "item.food", "stock")].Number);
        string rendered = Layout(decision).RenderUpdate(update);
        Assert.Contains("自己的资金余额", rendered);
        Assert.Contains("9钱", rendered);
        Assert.Contains("35份", rendered);
    }

    private static IPlayLayout Layout(bool decision) => decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
    private static bool OpenAt(CycleWorld world, string place) => CycleActions.CurrentPlace(world, "a") == place &&
        world.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open;
    private static async Task<CycleWorld> RunUntil(string[] commands, Func<CycleWorld, bool> done) {
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableWeather: false);
        var kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new Commands(commands));
        for (int step = 0; step < 100; step++) {
            if (done(kernel.World)) { return kernel.World; }
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
        throw new InvalidOperationException("Economic fixture did not reach the requested visible boundary.");
    }
    private sealed class Commands(string[] commands) : ICycleCharacterController {
        private readonly Queue<string> _commands = new(commands);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CycleControllerOperation(Guid.NewGuid().ToString("N"), _commands.Dequeue(), Guid.NewGuid().ToString("N")));
    }
}
