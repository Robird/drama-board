using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class HillsideTradeTests {
    [Fact]
    public async Task Offer_identity_counts_rejected_operations_and_is_unique_across_contexts() {
        var driver = new Driver(new Dictionary<string, Queue<PlaygroundCommand>> {
            ["a"] = new([PlaygroundCommand.Accept("missing"), Offer(), PlaygroundCommand.Wait(1), Offer()]),
            ["b"] = new([PlaygroundCommand.Wait(24)])
        });
        var kernel = Kernel(driver);
        await Find(kernel, PlaygroundEventKind.TradeOffered);
        Assert.Equal("trade:a:1:2", Assert.Single(kernel.World.Business.Economy!.Trades.Offers).Id);
        await Find(kernel, PlaygroundEventKind.TradeOffered);
        Assert.Equal(new[] { "trade:a:1:2", "trade:a:2:1" }, kernel.World.Business.Economy!.Trades.Offers.Select(offer => offer.Id));
    }

    [Fact]
    public async Task Conserved_offer_cannot_change_raw_terms_or_fabricate_a_delivery_cursor() {
        var kernel = Kernel(new Driver(new() { ["a"] = new([Offer()]) }));
        var (before, fact) = await Find(kernel, PlaygroundEventKind.TradeOffered);
        PlaygroundTradeOffer offered = Assert.Single(fact.TradeChanges);
        foreach (PlaygroundTradeOffer changed in new[] {
            offered with { Want = new("money", 7) },
            offered with { ProposerDeliveredVersion = 1, ProposerDeliveryContextSequence = 1 }
        }) {
            PlaygroundFact forged = fact.WithTradeChanges([changed]).WithEconomy(fact.Economy!.Change(trades: new([changed])));
            AssertConserved(kernel.World, forged);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
                kernel.LastCompletion!.Event.TargetInstant, forged));
            Assert.Contains("exact transitions", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Accepted_trade_cannot_move_an_extra_coin_even_when_total_assets_reconcile() {
        var kernel = Kernel(new Driver(new() { ["a"] = new([Offer()]), ["b"] = new([PlaygroundCommand.Accept("trade:a:1:1")]) }));
        var (before, fact) = await Find(kernel, PlaygroundEventKind.TradeAccepted);
        PlaygroundFact forged = fact.WithCharacters(fact.ChangedCharacters.Select(actor => Copy(actor,
            money: actor.Money + (actor.Id == "a" ? 1 : -1))));
        AssertConserved(kernel.World, forged);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion!.Event.TargetInstant, forged));
        Assert.Contains("asset changes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_cannot_mark_the_other_participant_as_having_received_terms() {
        var kernel = Kernel(new Driver(new() { ["a"] = new([Offer()]) }));
        await Find(kernel, PlaygroundEventKind.TradeOffered);
        PlaygroundWorld before = kernel.World;
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        PlaygroundFact opening = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, opening.Kind);
        Assert.Equal("b", opening.ActorId);
        PlaygroundTradeOffer real = Assert.Single(opening.Economy!.Trades.Offers);
        PlaygroundTradeOffer forgedEntry = real with { ProposerDeliveredVersion = 1, ProposerDeliveryContextSequence = 1 };
        PlaygroundFact forged = opening.WithEconomy(opening.Economy.Change(trades: new([forgedEntry])));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, forged));
        Assert.Contains("ledger differs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrelated_fact_cannot_rewrite_trade_delivery_or_expiry() {
        var kernel = Kernel(new Driver(new() { ["a"] = new([Offer()]), ["b"] = new([PlaygroundCommand.Say("a", "收到")]) }));
        var (before, fact) = await Find(kernel, PlaygroundEventKind.SpeechSent);
        PlaygroundTradeOffer offer = Assert.Single(before.Business.Economy!.Trades.Offers);
        PlaygroundTradeOffer changed = offer with { ExpiresAt = Hour(23) };
        PlaygroundFact forged = fact.WithEconomy(before.Business.Economy.Change(trades: new([changed])));
        AssertConserved(kernel.World, forged);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion!.Event.TargetInstant, forged));
        Assert.Contains("ledger differs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Travelling_offer_participant_sees_close_commands_as_unavailable() {
        var kernel = Kernel(new Driver(new() {
            ["a"] = new([Offer(), PlaygroundCommand.Go("e-m2")]),
            ["b"] = new([PlaygroundCommand.Wait(24)])
        }));
        await Find(kernel, PlaygroundEventKind.MoveStarted);
        PlaygroundObservation travelling = PlaygroundDecisionContext.Observe(kernel.World, "a");
        Assert.NotNull(travelling.Journey);
        PlaygroundCommandCheck withdraw = Assert.Single(travelling.Commands, candidate => candidate.Command.Kind == PlaygroundCommandKind.Withdraw);
        Assert.NotNull(withdraw.Rejection);
        Assert.NotNull(PlaygroundActions.Check(kernel.World, "a", withdraw.Command));
    }

    [Fact]
    public async Task Reclaiming_a_delivered_terminal_offer_refreshes_the_other_open_context_without_an_operation() {
        var kernel = Kernel(new Driver(new() {
            ["a"] = new([Offer(), PlaygroundCommand.Wait(1), PlaygroundCommand.Wait(24)]),
            ["b"] = new([PlaygroundCommand.Reject("trade:a:1:1"), PlaygroundCommand.Wait(2), PlaygroundCommand.Say("a", "请回应")])
        }));
        await Find(kernel, PlaygroundEventKind.SpeechSent);
        PlaygroundWorld before = kernel.World;
        CharacterContext previous = before.Business.InteractionStates["b"].CurrentContext!;
        Assert.Equal(CharacterContextStatus.Open, previous.Status);
        Assert.Equal(PlaygroundTradeStatus.Rejected, Assert.Single(previous.LatestObservation.Trades).Status);
        Assert.All(before.Business.Economy!.Trades.Offers, offer => {
            Assert.Equal(2, offer.ProposerDeliveredVersion);
            Assert.Equal(2, offer.RecipientDeliveredVersion);
        });
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
        PlaygroundFact opening = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.DecisionContextOpened, opening.Kind);
        Assert.Equal("a", opening.ActorId);
        Assert.Empty(kernel.World.Business.Economy!.Trades.Offers);
        CharacterContext refreshed = kernel.World.Business.InteractionStates["b"].CurrentContext!;
        Assert.Empty(refreshed.LatestObservation.Trades);
        Assert.Equal(previous.LastOperationSequence, refreshed.LastOperationSequence);
        Assert.Equal(previous.Items.Count + 1, refreshed.Items.Count);
        Assert.Null(refreshed.Items[^1].OperationSequence);
        Assert.Empty(refreshed.Items[^1].Experiences);
        Assert.Equal(before.GetCharacter("b").DecisionGeneration, kernel.World.GetCharacter("b").DecisionGeneration);
        Assert.Empty(opening.ObservedEvents);
        Assert.Equal(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
    }

    [Fact]
    public async Task One_weather_occurrence_closes_both_dead_participants_offers_and_notifies_survivor_once() {
        PlaygroundWorld basis = PlaygroundGameFactory.CreateLivingWorld(characterCount: 3, enableNeeds: true, enableWeather: true, weatherSeed: 0).World;
        PlaygroundCharacter[] actors = basis.Characters.Select(actor => actor.WithSurvival(new(basis.Time,
            100 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale, (actor.Id == "c" ? 100 : 3) * PlaygroundSurvival.Scale,
            Rate(0), Rate(0), PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: actor.Id == "c" ? 0 : 4))).ToArray();
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId,
            GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, actors.Select(actor => new EntityPlacement(new(actor.Id), new("camp")))),
            actors.Select(actor => new PlaygroundVisit(actor.Id, "camp", basis.Time, [])), environment: new(0, 0, Hour(9)));
        PlaygroundWorld initial = PlaygroundEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: actors, expedition: expedition), basis.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new Driver(new() {
            ["a"] = new([PlaygroundCommand.Offer("c", new("item.food", 1), new("money", 5))]),
            ["b"] = new([PlaygroundCommand.Offer("c", new("item.water", 1), new("money", 5))])
        }));
        var (before, weather) = await Find(kernel, PlaygroundEventKind.WeatherResolved);
        Assert.Equal(Hour(9), weather.At);
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        Assert.False(kernel.World.GetCharacter("b").IsActive);
        Assert.True(kernel.World.GetCharacter("c").IsActive);
        Assert.Equal(2, weather.TradeChanges.Count);
        Assert.All(weather.TradeChanges, offer => Assert.Equal(PlaygroundTradeStatus.ParticipantFailed, offer.Status));
        Assert.All(weather.ChangedCharacters, actor => Assert.Equal(before.GetCharacter(actor.Id).DecisionGeneration + 1, actor.DecisionGeneration));
        PlaygroundObservedEvent closure = Assert.Single(weather.ObservedEvents, entry => entry.ObserverId == "c" && entry.Kind == PlaygroundObservedEventKind.TradeParticipantFailed);
        Assert.Equal(2, closure.TradeChanges.Count);
        Assert.DoesNotContain(weather.ObservedEvents, entry => entry.ObserverId == "c" && entry.Kind is PlaygroundObservedEventKind.SurvivalFailed or PlaygroundObservedEventKind.ExpeditionEnded);
        Assert.All(closure.Effects, effect => Assert.Equal("communication", effect.Key.Category));
        Assert.Null(closure.Detail);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, prediction => prediction.OwnerId == "trades");
        kernel.World.Business.Economy!.Totals.Verify(kernel.World.Business);
    }

    [Fact]
    public async Task Same_time_expiry_batches_offers_and_increments_each_participant_once() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 3, enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new Driver(new() {
            ["a"] = new([PlaygroundCommand.Offer("c", new("item.food", 1), new("money", 5), 1)]),
            ["b"] = new([PlaygroundCommand.Offer("c", new("item.water", 1), new("money", 5), 1)])
        }));
        var (before, expired) = await Find(kernel, PlaygroundEventKind.TradesExpired);
        Assert.Equal(2, expired.TradeChanges.Count);
        Assert.Equal(Hour(9), expired.At);
        Assert.All(expired.ChangedCharacters, actor => Assert.Equal(before.GetCharacter(actor.Id).DecisionGeneration + 1, actor.DecisionGeneration));
        Assert.Equal(2, Assert.Single(expired.ObservedEvents, entry => entry.ObserverId == "c" && entry.Kind == PlaygroundObservedEventKind.TradesExpired).TradeChanges.Count);
        Assert.DoesNotContain(kernel.World.Forecasts.Predictions, prediction => prediction.OwnerId == "trades");
    }

    private static PlaygroundCommand Offer() => PlaygroundCommand.Offer("b", new("item.food", 1), new("money", 5));
    private static SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> Kernel(IPlaygroundCharacterController driver) =>
        PlaygroundEngine.CreateKernel(Memory(PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false, enableWeather: false).World), characterController: driver);
    private static async Task<(PlaygroundWorld Before, PlaygroundFact Fact)> Find(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, PlaygroundEventKind kind) {
        for (int step = 0; step < 80; step++) {
            PlaygroundWorld before = kernel.World;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(48)));
            Assert.Equal(PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time).Forecasts.ToString(), kernel.World.Forecasts.ToString());
            PlaygroundFact fact = Assert.Single(kernel.LastCompletion!.Event.Facts);
            if (fact.Kind == kind) { return (before, fact); }
        }
        throw new InvalidOperationException($"Event {kind} was not reached.");
    }
    private static void AssertConserved(PlaygroundWorld after, PlaygroundFact fact) {
        PlaygroundBusinessState forged = after.Business.Replace(after.Time, changedCharacters: fact.ChangedCharacters, economy: fact.Economy);
        forged.Economy!.Totals.Verify(forged);
    }
    private static PlaygroundCharacter Copy(PlaygroundCharacter actor, long money) =>
        new(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack, actor.Activity, actor.Notices,
            actor.DecisionGeneration, actor.LastResult, actor.NextActivityId, actor.Survival, actor.Ending,
            actor.Communication, actor.FollowingLeaderId, money);
    private sealed class Driver(Dictionary<string, Queue<PlaygroundCommand>> commands) : IPlaygroundCharacterController {
        private int _ordinal;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundCommand command = commands.TryGetValue(context.Observation.Actor.Id, out Queue<PlaygroundCommand>? queue) && queue.Count > 0
                ? queue.Dequeue() : context.Observation.Actor.Activity is not null || context.Observation.FollowingLeaderId is not null
                    ? PlaygroundCommand.Continue() : PlaygroundCommand.Wait(24);
            return ValueTask.FromResult(Operation(command, ++_ordinal));
        }
    }
}
