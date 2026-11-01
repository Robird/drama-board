using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class HillsideEconomyTests {
    [Fact]
    public async Task Death_at_production_deadline_returns_escrow_without_producing_or_losing_estate() {
        CycleWorld initial = AtWorkshop();
        CycleCharacter actor = initial.GetCharacter("a").WithSurvival(new(initial.Time,
            100 * CycleSurvival.Scale, 8 * CycleSurvival.Scale, 100 * CycleSurvival.Scale,
            Rate(0), CycleSurvivalRules.WaterRate, CycleSurvivalRules.WarmthRate(true, true), warnedMask: 2));
        initial = CycleEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor]), initial.Time);
        var history = Memory(initial);
        var kernel = CycleEngine.CreateKernel(history, characterController: Commands(CycleCommand.Produce("ration")));
        await StepWorldEvent(kernel);
        Assert.Equal(CycleEventKind.ProductionStarted, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        Assert.Equal(1, kernel.World.GetCharacter("a").Backpack.Count("item.food"));
        Assert.Equal(2, kernel.World.GetCharacter("a").Activity!.Production!.InputCount);
        await StepWorldEvent(kernel);
        Assert.Equal(CycleEventKind.SurvivalFailed, Assert.Single(kernel.LastCompletion!.Event.Facts).Kind);
        CycleCharacter dead = kernel.World.GetCharacter("a");
        Assert.Equal(CycleExpeditionOutcome.Dead, dead.Ending!.Outcome);
        Assert.Equal(Hour(9), dead.Ending.At);
        Assert.Null(dead.Activity);
        Assert.Equal(3, dead.Backpack.Count("item.food"));
        Assert.Equal(0, dead.Backpack.Count("item.ration"));
        Assert.Equal(12, dead.Money);
        Assert.Empty(kernel.World.Business.Economy!.Totals.Produced);
        Assert.Empty(kernel.World.Business.Economy.Totals.RecipeInputs);
        kernel.World.Business.Economy.Totals.Verify(kernel.World.Business);
        Assert.DoesNotContain(history.CompletedEvents.SelectMany(entry => entry.Facts), fact => fact.Kind == CycleEventKind.ProductionCompleted);
        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(Hour(24)));
    }

    [Fact]
    public async Task Conservation_alone_cannot_authorize_money_transfer_hidden_in_speech() {
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false, enableWeather: false).World;
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Say("b", "一起出发吗")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        CycleWorld before = kernel.World;
        await StepWorldEvent(kernel);
        CycleFact speech = Assert.Single(kernel.LastCompletion!.Event.Facts);
        CycleCharacter[] forgedActors = speech.ChangedCharacters.Select(actor => Copy(actor, money: actor.Money + (actor.Id == "a" ? -1 : 1))).ToArray();
        CycleBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: forgedActors);
        conserved.Economy!.Totals.Verify(conserved);
        CycleFact forged = speech.WithCharacters(forgedActors);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, forged));
        Assert.Contains("asset changes", error.Message, StringComparison.Ordinal);
        Assert.All(before.Characters, actor => Assert.Equal(12, actor.Money));
    }

    [Fact]
    public async Task Conserved_production_replacement_cannot_substitute_less_escrow_for_the_accepted_recipe() {
        CycleWorld initial = AtWorkshop();
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Produce("ration")));
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        CycleWorld before = kernel.World;
        await StepWorldEvent(kernel);
        CycleFact started = Assert.Single(kernel.LastCompletion!.Event.Facts);
        CycleCharacter actor = Assert.Single(started.ChangedCharacters);
        CycleActivity activity = actor.Activity!;
        CycleProductionOrder order = activity.Production! with { InputCount = 1 };
        var forgedActivity = new CycleActivity(activity.Kind, activity.Id, activity.StartedAt, activity.DurationTicks,
            activity.Due, production: order);
        CycleCharacter forgedActor = Copy(actor, backpack: actor.Backpack.Add("item.food"), activity: forgedActivity);
        CycleBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: [forgedActor]);
        conserved.Economy!.Totals.Verify(conserved);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, started.WithCharacters([forgedActor])));
        Assert.Contains("asset changes", error.Message, StringComparison.Ordinal);
    }

    private static CycleWorld AtWorkshop() {
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false, enableWeather: false).World;
        var expedition = new CycleExpedition(CycleExpeditionMap.ScenarioId,
            GraphSpatialState.Create(CycleExpeditionMap.Graph, [new(new("a"), new("workshop"))]),
            [new("a", "workshop", initial.Time, [])]);
        return CycleEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: expedition), initial.Time);
    }

    [Fact]
    public async Task Market_refresh_cannot_invent_a_remote_visit_even_when_economic_effects_are_exact() {
        CycleWorld initial = CycleGameFactory.CreateLivingWorld(Hour(23), characterCount: 1, enableNeeds: false, enableWeather: false).World;
        var kernel = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Wait(2)));
        await StepWorldEvent(kernel);
        CycleWorld before = kernel.World;
        await StepWorldEvent(kernel);
        CycleFact refresh = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.MarketRefreshed, refresh.Kind);
        Assert.True(before.Business.Economy!.Totals.SameState(kernel.World.Business.Economy!.Totals));
        CycleFact forged = refresh.WithVisits(refresh.Visits.Append(new("a", "stream", refresh.At,
            kernel.World.Resources.Where(resource => CycleExpeditionMap.GetResourcePlace(resource.Id) == "stream"))));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, forged));
        Assert.Contains("actual places", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Consumption_cannot_overheal_or_supply_the_wrong_need_while_assets_reconcile(bool wrongNeed) {
        CycleWorld initial = AtWorkshop();
        var setup = CycleEngine.CreateKernel(Memory(initial), characterController: Commands(CycleCommand.Produce("ration")));
        await StepWorldEvent(setup);
        await StepWorldEvent(setup);
        CycleCharacter actor = setup.World.GetCharacter("a").WithSurvival(CycleSurvival.Create(setup.World.Time,
            food: 10, water: 40, warmth: 80, warmthRate: CycleSurvivalRules.WarmthRate(true, true)));
        CycleWorld ready = CycleEngine.Rebuild(setup.World.Business.Replace(setup.World.Time, changedCharacters: [actor]), setup.World.Time);
        var kernel = CycleEngine.CreateKernel(Memory(ready), characterController: Commands(CycleCommand.Use("item.ration")));
        // Low food generates a warning before the decision opens; stop immediately before the actual Use.
        for (int index = 0; index < 5 && kernel.World.Business.InteractionStates["a"].CurrentContext?.Status != CharacterContextStatus.Open; index++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        }
        CycleWorld before = kernel.World;
        await StepWorldEvent(kernel);
        CycleFact used = Assert.Single(kernel.LastCompletion!.Event.Facts);
        Assert.Equal(CycleEventKind.ItemUsed, used.Kind);
        CycleCharacter valid = Assert.Single(used.ChangedCharacters);
        CycleSurvival body = valid.Survival!;
        CycleCharacter forgedActor = valid.WithSurvival(new(body.BaselineAt,
            wrongNeed ? body.FoodUnits : 100 * CycleSurvival.Scale,
            wrongNeed ? 100 * CycleSurvival.Scale : body.WaterUnits, body.WarmthUnits,
            body.FoodRate, body.WaterRate, body.WarmthRate, body.WarnedMask, body.PendingMask, body.PendingSince));
        CycleBusinessState conserved = kernel.World.Business.Replace(kernel.World.Time, changedCharacters: [forgedActor]);
        conserved.Economy!.Totals.Verify(conserved);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => CycleEngine.Fold(before,
            kernel.LastCompletion.Event.TargetInstant, used.WithCharacters([forgedActor])));
        Assert.Contains("declared supply", error.Message, StringComparison.Ordinal);
    }

    private static CycleCharacter Copy(CycleCharacter actor, long? money = null, ItemInventory? backpack = null, CycleActivity? activity = null) =>
        new(actor.Id, actor.Name, actor.SkillLevel, backpack ?? actor.Backpack, activity ?? actor.Activity, actor.Notices,
            actor.DecisionGeneration, actor.LastResult, actor.NextActivityId, actor.Survival, actor.Ending, actor.Communication,
            actor.FollowingLeaderId, money ?? actor.Money);
}
