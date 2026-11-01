using DramaBoard.Inventory;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Trade acceptance at the controller, frozen material, publication and recovery boundaries.</summary>
public sealed class HillsideTradeAcceptanceTests {
    [Fact]
    public async Task Accepted_trade_recovers_once_from_E_then_repeated_and_outsider_submissions_preserve_assets() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(enableNeeds: false);
        string id, before, settled;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var driver = new Driver("offer");
            var kernel = CycleEngine.CreateKernel(history, characterController: driver);
            await Act(kernel, driver, "a", CycleCommand.Offer("b", new("item.food", 1), new("money", 5)));
            id = Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id;
            Assert.All(kernel.World.Characters, actor => {
                Assert.Equal(12, actor.Money);
                Assert.Equal(3, actor.Backpack.Count("item.food"));
            });
            before = MaterialSnapshot(history.State.Business);

            // Plan the actual controller command on the exact committed S, then publish only its E.
            var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(history.State, history.Cursor);
            var accept = new Driver("accept");
            var normal = CycleEngine.CreateKernel(memory, characterController: accept);
            await Act(normal, accept, "b", CycleCommand.Accept(id));
            Assert.Equal(CycleEventKind.TradeAccepted, Assert.Single(normal.LastCompletion!.Event.Facts).Kind);
            AssertSettled(normal.World);
            settled = MaterialSnapshot(normal.World.Business);
            // The recipient opening is a separate publication; reproduce that boundary first.
            foreach (OccurrenceEvent<CycleFact> occurrence in memory.CompletedEvents.SkipLast(1)) {
                history.CommitEvent(occurrence);
                var recovery = RecoveryWithoutPlanning(history);
                Assert.True(recovery.RecoverPending());
            }
            before = MaterialSnapshot(history.State.Business);
            history.CommitEvent(normal.LastCompletion.Event);
            Assert.Equal(before, MaterialSnapshot(history.State.Business));
            Assert.NotNull(history.PendingEvent);
        }
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Equal(before, MaterialSnapshot(history.State.Business));
            var recovery = RecoveryWithoutPlanning(history);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(settled, MaterialSnapshot(history.State.Business));
            AssertSettled(history.State);
        }
        using (var history = CycleDurableHistory.Open(store.StorePath)) {
            Assert.Null(history.PendingEvent);
            Assert.Equal(settled, MaterialSnapshot(history.State.Business));
            var driver = new Driver("after-cold-open");
            var kernel = CycleEngine.CreateKernel(history, characterController: driver);
            await Act(kernel, driver, "c", CycleCommand.Accept(id), accepted: false);
            Assert.All(driver.Views.Where(view => view.Actor.Id == "c"), view => Assert.Empty(view.Trades));
            AssertSettled(kernel.World);
            await Act(kernel, driver, "b", CycleCommand.Accept(id), accepted: false);
            AssertSettled(kernel.World);
            Assert.Empty(kernel.World.Business.Economy!.Totals.Imported);
            Assert.Empty(kernel.World.Business.Economy.Totals.Exported);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreserved_or_net_affordable_assets_do_not_make_an_unfunded_acceptance_valid(bool sameMoney) {
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(enableNeeds: false).World;
        var driver = new Driver("unreserved");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: driver);
        await Act(kernel, driver, "a", sameMoney
            ? CycleCommand.Offer("b", new("money", 12), new("money", 13))
            : CycleCommand.Offer("b", new("item.food", 3), new("money", 5)));
        string id = Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id;
        Assert.Equal(12, kernel.World.GetCharacter("a").Money);
        Assert.Equal(3, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        if (!sameMoney) {
            // Keep b's window open without accepting, then spend the still-owned offered goods.
            await Act(kernel, driver, "b", CycleCommand.Handoff("consider the offer"));
            await Act(kernel, driver, "c", CycleCommand.Wait(4));
            await Act(kernel, driver, "a", CycleCommand.Give("c", "item.food", 3));
            Assert.Equal(0, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        }
        string assets = Assets(kernel.World);
        string totals = MaterialSnapshot(kernel.World.Business.Economy!.Totals);
        CycleTradeOffer beforeOffer = Assert.Single(kernel.World.Business.Economy.Trades.Offers);
        // The recipient can check its own funds, but cannot inspect the proposer's live purse.
        // A stale-looking available offer is legal to attempt; the authoritative commit rejects it.
        await Act(kernel, driver, "b", CycleCommand.Accept(id), accepted: false, visibleAllowed: !sameMoney);
        Assert.Equal(assets, Assets(kernel.World));
        Assert.Equal(totals, MaterialSnapshot(kernel.World.Business.Economy!.Totals));
        CycleTradeOffer afterOffer = Assert.Single(kernel.World.Business.Economy.Trades.Offers);
        Assert.Equal(CycleTradeStatus.Active, afterOffer.Status);
        // A separately published opening may mark delivery, but rejection changes no offer terms.
        Assert.Equal(beforeOffer with {
            ProposerDeliveredVersion = afterOffer.ProposerDeliveredVersion,
            RecipientDeliveredVersion = afterOffer.RecipientDeliveredVersion,
            ProposerDeliveryContextSequence = afterOffer.ProposerDeliveryContextSequence,
            RecipientDeliveryContextSequence = afterOffer.RecipientDeliveryContextSequence
        }, afterOffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trading_while_producing_preserves_due_and_both_capacity_outcomes(bool cancel) {
        CycleCharacter a = new("a", "Producer", backpack: new ItemInventory(
            new Dictionary<string, int> { ["item.wood"] = 2, ["item.food"] = 2 }, unitCapacity: 4), money: 12);
        CycleCharacter b = new("b", "Neighbour", backpack: new ItemInventory(new Dictionary<string, int> { ["item.water"] = 1 }), money: 12);
        CycleWorld initial = AtPlace("workshop", [a, b]);
        var driver = new Driver("busy");
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: driver);
        await Act(kernel, driver, "a", CycleCommand.Produce("woodware"));
        string activity = MaterialSnapshot(kernel.World.GetCharacter("a").Activity);
        await Act(kernel, driver, "b", CycleCommand.Offer("a", new("item.water", 1), new("money", 1)));
        string id = Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id;
        string assets = Assets(kernel.World);
        await Act(kernel, driver, "a", CycleCommand.Accept(id), accepted: false);
        Assert.Equal(assets, Assets(kernel.World));
        Assert.Equal(activity, MaterialSnapshot(kernel.World.GetCharacter("a").Activity));
        Assert.Equal(Hour(8), kernel.World.Time);
        // Rejecting an offer is zero-time and must not replace or acknowledge the production.
        await Act(kernel, driver, "a", CycleCommand.Reject(id));
        Assert.Equal(activity, MaterialSnapshot(kernel.World.GetCharacter("a").Activity));
        await Act(kernel, driver, "a", cancel ? CycleCommand.Cancel() : CycleCommand.Continue());
        if (!cancel) {
            await Until(kernel, world => world.GetCharacter("a").Activity is null);
        }
        Assert.Equal(cancel ? 2 : 0, kernel.World.GetCharacter("a").Backpack.Count("item.wood"));
        Assert.Equal(cancel ? 0 : 1, kernel.World.GetCharacter("a").Backpack.Count("item.woodware"));
        Assert.Equal(1, kernel.World.GetCharacter("b").Backpack.Count("item.water"));
        Assert.All(kernel.World.Characters, actor => Assert.Equal(12, actor.Money));
    }

    [Fact]
    public async Task Expiration_precedes_the_same_time_decision_and_wakes_busy_parties_without_replacing_their_work() {
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false).World;
        var driver = new Driver("expires");
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: driver);
        await Act(kernel, driver, "a", CycleCommand.Offer("b", new("item.food", 1), new("money", 5), 1));
        string id = Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id;
        await Act(kernel, driver, "b", CycleCommand.Wait(2));
        await Act(kernel, driver, "a", CycleCommand.Wait(2));
        string aWork = MaterialSnapshot(kernel.World.GetCharacter("a").Activity);
        string bWork = MaterialSnapshot(kernel.World.GetCharacter("b").Activity);
        await Until(kernel, world => world.Business.Economy!.Trades.Offers.Any(offer => offer.Status == CycleTradeStatus.Expired));
        Assert.Equal(Hour(9), kernel.World.Time);
        CycleTradeOffer expired = Assert.Single(kernel.World.Business.Economy!.Trades.Offers);
        Assert.Equal(expired.ExpiresAt, expired.ClosedAt);
        Assert.Equal(aWork, MaterialSnapshot(kernel.World.GetCharacter("a").Activity));
        Assert.Equal(bWork, MaterialSnapshot(kernel.World.GetCharacter("b").Activity));
        await Act(kernel, driver, "b", CycleCommand.Accept(id), accepted: false);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal(bWork, MaterialSnapshot(kernel.World.GetCharacter("b").Activity));
        Assert.Contains(driver.Views, view => view.Actor.Id == "b" && view.Trades.Any(offer => offer.Id == id && offer.Status == CycleTradeStatus.Expired));
        await Act(kernel, driver, "b", CycleCommand.Continue());
        // The other party may have been served by the driver while b was selected. Either way,
        // there is one expiry fact and continuing never re-enqueues another expiry owner.
        Assert.Single(history.CompletedEvents.SelectMany(occurrence => occurrence.Facts), fact => fact.Kind == CycleEventKind.TradesExpired);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, prediction => prediction.OwnerId == "trades");
        Assert.All(kernel.World.Characters, actor => Assert.Equal(12, actor.Money));
    }

    [Fact]
    public async Task Actual_death_closes_a_distant_offer_without_impersonating_the_survivor_or_informing_a_bystander() {
        CycleWorld basis = CycleGameFactory.CreateLivingWorld(enableNeeds: false).World;
        CycleCharacter a = basis.GetCharacter("a").WithSurvival(new(basis.Time,
            100 * CycleSurvival.Scale, 8 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            Rate(0), CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(true, true), warnedMask: 2));
        CycleWorld initial = CycleEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: [a]), basis.Time);
        var driver = new Driver("death");
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: driver);
        await Act(kernel, driver, "a", CycleCommand.Offer("b", new("item.food", 1), new("money", 5), 4));
        string id = Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id;
        await Act(kernel, driver, "b", CycleCommand.Go("market-home"));
        await Act(kernel, driver, "c", CycleCommand.Wait(4));
        await Act(kernel, driver, "a", CycleCommand.Wait(4));
        await Until(kernel, world => !world.GetCharacter("a").IsActive);
        Assert.Equal(Hour(9), kernel.World.Time);
        Assert.Equal("market", CycleActions.CurrentPlace(kernel.World, "b"));
        Assert.True(kernel.World.GetCharacter("b").IsActive);
        CycleTradeOffer terminal = Assert.Single(kernel.World.Business.Economy!.Trades.Offers);
        Assert.Equal(CycleTradeStatus.ParticipantFailed, terminal.Status);
        Assert.True(terminal.ProposerDeliveredVersion < terminal.Version);
        CycleFact death = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.SurvivalFailed, death.Kind);
        CycleObservedEvent recipient = Assert.Single(death.ObservedEvents, observed => observed.ObserverId == "b");
        Assert.NotEqual(CycleObservedEventKind.SurvivalFailed, recipient.Kind);
        Assert.Contains(recipient.TradeChanges, offer => offer.Id == id && offer.Status == CycleTradeStatus.ParticipantFailed);
        Assert.DoesNotContain(death.ObservedEvents, observed => observed.ObserverId == "c");
        await Act(kernel, driver, "b", CycleCommand.Continue());
        Assert.Contains(driver.Views, view => view.Actor.Id == "b" && view.Trades.Any(offer => offer.Id == id && offer.Status == CycleTradeStatus.ParticipantFailed));
        Assert.All(driver.Views.Where(view => view.Actor.Id == "c"), view => Assert.Empty(view.Trades));
        Assert.All(kernel.World.Characters, actor => Assert.Equal(12, actor.Money));
    }

    private static void AssertSettled(CycleWorld world) {
        Assert.Equal(17, world.GetCharacter("a").Money);
        Assert.Equal(7, world.GetCharacter("b").Money);
        Assert.Equal(12, world.GetCharacter("c").Money);
        Assert.Equal(2, world.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal(4, world.GetCharacter("b").Backpack.Count("item.food"));
        Assert.Equal(3, world.GetCharacter("c").Backpack.Count("item.food"));
    }

    private static string Assets(CycleWorld world) => MaterialSnapshot(world.Characters.Select(actor =>
        new { actor.Id, actor.Money, actor.Backpack, Production = actor.Activity?.Production }));

    private static CycleWorld AtPlace(string place, CycleCharacter[] residents) {
        CycleWorld basis = CycleGameFactory.CreateLivingWorld(characterCount: residents.Length, enableNeeds: false).World;
        var totals = new CycleEconomicTotals(residents.Sum(actor => actor.Money), residents.SelectMany(actor => actor.Backpack.Items)
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Value), StringComparer.Ordinal));
        var spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, residents.Select(actor => new EntityPlacement(new(actor.Id), new(place))));
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId, spatial,
            residents.Select(actor => new CycleVisit(actor.Id, place, basis.Time, [])));
        return CycleEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: residents, expedition: expedition,
            economy: new CycleEconomyState(CycleMarket.Fresh(Hour(24)), totals)), basis.Time);
    }

    private static async Task Act(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Driver driver,
        string actor, CycleCommand command, bool accepted = true, bool? visibleAllowed = null) {
        driver.Expect(actor, command, visibleAllowed ?? accepted);
        for (int step = 0; step < 200; step++) {
            await Advance(kernel);
            if (!driver.Fulfilled) { continue; }
            CycleFact operation = Assert.Single(kernel.LastCompletion!.Event.Facts, fact => fact.OperationRecordId == driver.LastOperationId);
            Assert.Equal(accepted ? CycleOperationDisposition.Succeeded : CycleOperationDisposition.Rejected, operation.OperationDisposition);
            return;
        }
        Assert.Fail($"Character {actor} never received a decision for {CycleCommandParser.Format(command)}.");
    }

    private static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> reached) {
        for (int step = 0; step < 200; step++) {
            if (reached(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The trade scenario did not reach its finite boundary.");
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        CycleTradeOffer[] active = kernel.World.Business.Economy!.Trades.Offers.Where(offer => offer.Status == CycleTradeStatus.Active).ToArray();
        if (active.Length == 0) {
            Assert.DoesNotContain(kernel.World.Forecasts.Predictions, prediction => prediction.OwnerId == "trades");
        } else {
            CyclePrediction expiry = Assert.Single(kernel.World.Forecasts.Predictions, prediction => prediction.OwnerId == "trades");
            Assert.Equal(active.Min(offer => offer.ExpiresAt), expiry.Due);
        }
        CycleEconomicTotals totals = kernel.World.Business.Economy!.Totals;
        Assert.Equal(totals.InitialMoney + totals.ExportIncome - totals.ImportExpense, kernel.World.Characters.Sum(actor => actor.Money));
        foreach (string item in LivingWorldEconomyContent.Items.Keys) {
            long held = kernel.World.Characters.Sum(actor => (long)actor.Backpack.Count(item) +
                (actor.Activity?.Production is { } order && order.InputItem == item ? order.InputCount : 0));
            Assert.Equal(totals.InitialItems.GetValueOrDefault(item) + totals.Imported.GetValueOrDefault(item) +
                totals.Harvested.GetValueOrDefault(item) + totals.Produced.GetValueOrDefault(item) -
                totals.Exported.GetValueOrDefault(item) - totals.Consumed.GetValueOrDefault(item) - totals.RecipeInputs.GetValueOrDefault(item), held);
        }
    }

    private static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> RecoveryWithoutPlanning(CycleDurableHistory history) =>
        new(history, new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
            (before, at, facts, folded) => CycleEngine.Complete(before, at, facts, folded, PredictionMode.Incremental), CycleEngine.Validate);

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending trade recovery must not forecast or ask any character.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world, OccurrenceCandidate<CyclePrediction> winner,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Pending trade recovery must not re-plan.");
    }

    private sealed class Driver(string session) : ICycleCharacterController {
        private int _calls;
        private string? _actor;
        private CycleCommand? _command;
        private bool _visibleAllowed;
        public bool Fulfilled { get; private set; }
        public string? LastOperationId { get; private set; }
        public List<CycleObservation> Views { get; } = [];
        public void Expect(string actor, CycleCommand command, bool visibleAllowed) {
            _actor = actor; _command = command; _visibleAllowed = visibleAllowed; Fulfilled = false;
        }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Views.Add(context.Observation);
            Assert.All(context.Observation.Trades, offer => {
                Assert.True(offer.Involves(context.Observation.Actor.Id));
                Assert.Equal(0, offer.ProposerDeliveredVersion);
                Assert.Equal(0, offer.RecipientDeliveredVersion);
                Assert.Equal(0, offer.ProposerDeliveryContextSequence);
                Assert.Equal(0, offer.RecipientDeliveryContextSequence);
            });
            CycleCommand command;
            if (!Fulfilled && context.Observation.Actor.Id == _actor) {
                command = _command!;
                Assert.Equal(_visibleAllowed, context.Check(command) is null);
                Fulfilled = true;
            } else {
                command = context.Observation.Actor.Activity is not null || context.Observation.ArrivalDue is not null
                    ? CycleCommand.Continue() : CycleCommand.Wait(24);
                Assert.Null(context.Check(command));
            }
            string id = $"{session}/{++_calls}";
            if (Fulfilled && context.Observation.Actor.Id == _actor) { LastOperationId = id; }
            return ValueTask.FromResult(new CycleControllerOperation(id, CycleCommandParser.Format(command), id));
        }
    }
}
