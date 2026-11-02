using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class HillsideEconomyTests {
    [Fact]
    public async Task Death_at_production_deadline_returns_escrow_without_producing_or_losing_estate() {
        PlaygroundWorld initial = AtWorkshop();
        PlaygroundCharacter actor = initial.GetCharacter("a").WithSurvival(new(initial.Time,
            100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale, 100 * PlaygroundSurvival.Scale,
            Rate(0), PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(true, true), warnedMask: 2));
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor]), initial.Time);
        var history = Memory(initial);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: Commands(PlaygroundCommand.Produce("ration")));
        await StepWorldEvent(kernel);
        Assert.Equal(PlaygroundEventKind.ProductionStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal(2, kernel.World.GetCharacter("a").Activity!.Production!.InputCount);
        await StepWorldEvent(kernel);
        Assert.Equal(PlaygroundEventKind.SurvivalFailed, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        PlaygroundCharacter dead = kernel.World.GetCharacter("a");
        Assert.Equal(PlaygroundExpeditionOutcome.Dead, dead.Ending!.Outcome);
        Assert.Equal(Hour(9), dead.Ending.At);
        Assert.Null(dead.Activity);
        Assert.Equal(3, dead.Backpack.Count("item.food"));
        Assert.Equal(0, dead.Backpack.Count("item.ration"));
        Assert.Equal(12, dead.Money);
        Assert.Empty(kernel.World.Business.Economy!.Totals.Produced);
        Assert.Empty(kernel.World.Business.Economy.Totals.RecipeInputs);
        kernel.World.Business.Economy.Totals.Verify(kernel.World.Business);
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(entry => entry.Facts), fact => fact.Kind == PlaygroundEventKind.ProductionCompleted);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Fact]
    public async Task Conservation_alone_cannot_authorize_money_transfer_hidden_in_speech() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Say("b", "一起出发吗")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        PlaygroundWorld before = kernel.World;
        await StepWorldEvent(kernel);
        PlaygroundFact speech = Assert.Single(kernel.LastCompletion!.Event.Facts);
        PlaygroundCharacter[] forgedActors = speech.ChangedCharacters.Select(actor => Copy(actor, money: actor.Money + (actor.Id == "a" ? -1 : 1))).ToArray();
        PlaygroundBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: forgedActors);
        conserved.Economy!.Totals.Verify(conserved);
        PlaygroundFact forged = speech.WithCharacters(forgedActors);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, forged));
        Assert.Contains("asset changes", error.Message, StringComparison.Ordinal);
        Assert.All(before.Characters, actor => Assert.Equal(12, actor.Money));
    }

    [Fact]
    public async Task Conserved_production_replacement_cannot_substitute_less_escrow_for_the_accepted_recipe() {
        PlaygroundWorld initial = AtWorkshop();
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Produce("ration")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        PlaygroundWorld before = kernel.World;
        await StepWorldEvent(kernel);
        PlaygroundFact started = Assert.Single(kernel.LastCompletion!.Event.Facts);
        PlaygroundCharacter actor = Assert.Single(started.ChangedCharacters);
        PlaygroundActivity activity = actor.Activity!;
        PlaygroundProductionOrder order = activity.Production! with { InputCount = 1 };
        var forgedActivity = new PlaygroundActivity(activity.Kind, activity.Id, activity.StartedAt, activity.DurationTicks,
            activity.Due, production: order);
        PlaygroundCharacter forgedActor = Copy(actor, backpack: actor.Backpack.Add("item.food"), activity: forgedActivity);
        PlaygroundBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: [forgedActor]);
        conserved.Economy!.Totals.Verify(conserved);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, started.WithCharacters([forgedActor])));
        Assert.Contains("asset changes", error.Message, StringComparison.Ordinal);
    }

    private static PlaygroundWorld AtWorkshop() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false, enableWeather: false).World;
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId,
            GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new("workshop"))]),
            [new("a", "workshop", initial.Time, [])]);
        return PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: expedition), initial.Time);
    }

    [Fact]
    public async Task Market_refresh_cannot_invent_a_remote_visit_even_when_economic_effects_are_exact() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateLivingWorld(Hour(23), characterCount: 1, enableNeeds: false, enableWeather: false).World;
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Wait(2)));
        await StepWorldEvent(kernel);
        PlaygroundWorld before = kernel.World;
        await StepWorldEvent(kernel);
        PlaygroundFact refresh = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.MarketRefreshed, refresh.Kind);
        Assert.True(before.Business.Economy!.Totals.SameState(kernel.World.Business.Economy!.Totals));
        PlaygroundFact forged = refresh.WithVisits(refresh.Visits.Append(new("a", "stream", refresh.At,
            kernel.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == "stream"))));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, forged));
        Assert.Contains("actual places", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Consumption_cannot_overheal_or_supply_the_wrong_need_while_assets_reconcile(bool wrongNeed) {
        PlaygroundWorld initial = AtWorkshop();
        var setup = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(PlaygroundCommand.Produce("ration")));
        await StepWorldEvent(setup);
        await StepWorldEvent(setup);
        PlaygroundCharacter actor = setup.World.GetCharacter("a").WithSurvival(PlaygroundSurvival.Create(setup.World.Time,
            food: 10, water: 40, warmth: 80, warmthRate: PlaygroundSurvivalRules.WarmthRate(true, true)));
        PlaygroundWorld ready = PlaygroundEngine.Rebuild(setup.World.Business.Replace(setup.World.Time, changedCharacters: [actor]), setup.World.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(ready), characterController: Commands(PlaygroundCommand.Use("item.ration")));
        // Low food generates a warning before the decision opens; stop immediately before the actual Use.
        for (int index = 0; index < 5 && kernel.World.Business.InteractionStates["a"].CurrentContext?.Status != CharacterContextStatus.Open; index++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        }
        PlaygroundWorld before = kernel.World;
        await StepWorldEvent(kernel);
        PlaygroundFact used = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(PlaygroundEventKind.ItemUsed, used.Kind);
        PlaygroundCharacter valid = Assert.Single(used.ChangedCharacters);
        PlaygroundSurvival body = valid.Survival!;
        PlaygroundCharacter forgedActor = valid.WithSurvival(new(body.BaselineAt,
            wrongNeed ? body.FoodUnits : 100 * PlaygroundSurvival.Scale,
            wrongNeed ? 100 * PlaygroundSurvival.Scale : body.WaterUnits, body.WarmthUnits,
            body.FoodRate, body.WaterRate, body.WarmthRate, body.WarnedMask, body.PendingMask, body.PendingSince));
        PlaygroundBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: [forgedActor]);
        conserved.Economy!.Totals.Verify(conserved);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, used.WithCharacters([forgedActor])));
        Assert.Contains("declared supply", error.Message, StringComparison.Ordinal);
    }

    private static PlaygroundCharacter Copy(PlaygroundCharacter actor, long? money = null, ItemInventory? backpack = null, PlaygroundActivity? activity = null) =>
        new(actor.Id, actor.Name, actor.SkillLevel, backpack ?? actor.Backpack, activity ?? actor.Activity, actor.Notices,
            actor.DecisionGeneration, actor.LastResult, actor.NextActivityId, actor.Survival, actor.Ending, actor.Communication,
            actor.FollowingLeaderId, money ?? actor.Money);
}
