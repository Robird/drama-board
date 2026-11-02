using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Time;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class RateExplanationContextTests {
    [Theory]
    [InlineData("order")]
    [InlineData("duplicate")]
    [InlineData("overflow")]
    [InlineData("source-label")]
    public void Cold_open_validates_historical_explanations_even_when_physical_rates_are_valid(string corruption) {
        var historical = new RateBreakdown([new("a", "甲", 18), new("b", "乙", -12)]);
        PlaygroundWorld opened = OpenWorld(historical);
        PlaygroundWorld world = PlaygroundEngine.Fold(opened, new(opened.Time, 0),
            ChangeRate(opened, PlaygroundSurvivalRules.WarmthRate(false, true)));
        var root = new PlaygroundDurableRoot(world.Business, InitialCursor(world));
        List<RateContribution> invalid = corruption switch {
            "order" => [new("b", "乙", -12), new("a", "甲", 18)],
            "duplicate" => [new("a", "甲", 3), new("a", "甲", 3)],
            "overflow" => [new("a", "甲", int.MaxValue), new("b", "乙", 1)],
            "source-label" => [new(PlaygroundSurvivalRules.FoodRate.Contributions[0].SourceKey, "冲突名称", 6)],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        // Simulate constructor-bypassing hydration, and persist the malformed historical leaf.
        typeof(RateBreakdown).GetField("_contributions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(historical, invalid);
        world.GetCharacter("a").Survival!.Validate();
        using var store = new TemporaryStore();
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        using (var repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("malformed-history", root);
        }
        if (corruption == "overflow") {
            Assert.Throws<OverflowException>(() => PlaygroundDurableHistory.Open(store.StorePath));
        } else {
            Assert.Throws<InvalidOperationException>(() => PlaygroundDurableHistory.Open(store.StorePath));
        }
    }

    [Fact]
    public void Same_total_new_cause_appends_snapshot_only_item_and_missing_or_false_delivery_is_rejected() {
        PlaygroundWorld before = OpenWorld();
        CharacterContext opening = Context(before);
        var changedRate = new RateBreakdown([new("fixture.sun", "日照", 18), new("fixture.wet", "湿身", -12)]);
        PlaygroundFact fact = ChangeRate(before, changedRate);
        Assert.Empty(fact.ObservedEvents);
        var change = Assert.Single(fact.InteractionChanges);
        CharacterContext next = change.CurrentContext!;
        var item = Assert.Single(next.Items);
        Assert.Empty(item.Experiences);
        Assert.Null(item.OperationSequence);
        Assert.NotNull(item.Observation);
        Assert.Equal(opening.LatestObservation.Survival!.WarmthPerHour, item.Observation.Survival!.WarmthPerHour);
        Assert.True(changedRate.SameAs(item.Observation.Survival.WarmthRate));
        Assert.False(PlaygroundObservationEquality.Same(opening.LatestObservation, item.Observation));

        PlaygroundWorld after = PlaygroundEngine.Fold(before, new(before.Time, 0), fact);
        Assert.True(PlaygroundVisibleEndpointChanges.Changed(before.Business, after.Business, "a"));
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before, new(before.Time, 0), fact.WithInteractions([])));
        PlaygroundFact forged = ReplaceLastObservation(fact, CopyObservation(item.Observation,
            item.Observation.Survival with { WarmthRate = opening.LatestObservation.Survival.WarmthRate }));
        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before, new(before.Time, 0), forged));
        Assert.Same(opening.Opening, next.Opening);
        Assert.False(changedRate.SameAs(opening.Opening.Observation.Survival!.WarmthRate));
    }

    [Fact]
    public void Reordering_contributions_does_not_create_an_item_or_visible_endpoint_change() {
        var originalRate = new RateBreakdown([new("a", "甲", 18), new("b", "乙", -12)]);
        PlaygroundWorld before = OpenWorld(originalRate);
        PlaygroundFact fact = ChangeRate(before, new RateBreakdown(originalRate.Contributions.Reverse()));
        Assert.Empty(fact.ObservedEvents);
        Assert.Empty(fact.InteractionChanges);
        PlaygroundWorld after = PlaygroundEngine.Fold(before, new(before.Time, 0), fact);
        Assert.False(PlaygroundVisibleEndpointChanges.Changed(before.Business, after.Business, "a"));
        Assert.True(PlaygroundObservationEquality.Same(PlaygroundDecisionContext.Observe(before, "a"), PlaygroundDecisionContext.Observe(after, "a")));
    }

    [Fact]
    public void Interaction_boundary_cannot_smuggle_a_different_explanation_with_equal_total() {
        PlaygroundWorld before = OpenWorld();
        PlaygroundControllerOperation operation = Operation(PlaygroundCommand.Handoff("记录"));
        PlaygroundFact handoff = PlaygroundInteractionRules.Enrich(before, PlaygroundInteractionRules.EditHandoff(before, "a"), operation)
            .WithOperation(operation);
        PlaygroundCharacter actor = Assert.Single(handoff.ChangedCharacters);
        PlaygroundSurvival body = actor.Survival!;
        var tampered = actor.WithSurvival(body.WithRates(before.Time, body.FoodRate, body.WaterRate, Rate(body.WarmthPerHour, "false-source")));

        Assert.Throws<InvalidOperationException>(() => PlaygroundEngine.Fold(before, new(before.Time, 0), handoff.WithCharacters([tampered])));
    }

    [Fact]
    public void Same_public_source_has_one_label_across_attributes_and_can_be_grouped_without_game_objects() {
        PlaygroundSurvivalObservation body = Context(OpenWorld()).Opening.Observation.Survival!;
        var attributes = new[] { (Name: "food", Rate: body.FoodRate), (Name: "water", Rate: body.WaterRate), (Name: "warmth", Rate: body.WarmthRate) };
        var groups = attributes.SelectMany(attribute => attribute.Rate.Contributions.Select(part =>
                (Attribute: attribute.Name, Part: part))).GroupBy(entry => entry.Part.SourceKey).ToArray();
        var metabolism = Assert.Single(groups, group => group.Count() == 2);
        Assert.Single(metabolism.Select(entry => entry.Part.SourceLabel).Distinct());
        Assert.Equal(new[] { "food", "water" }, metabolism.Select(entry => entry.Attribute).Order());
        Assert.Equal(new[] { -8, -5 }, metabolism.Select(entry => entry.Part.Amount).Order());
        Assert.Equal(0, body.Minimum);
        Assert.Equal(100, body.Maximum);

        var falseLabel = new RateBreakdown(body.WaterRate.Contributions.Select(part => part with { SourceLabel = "不同原因" }));
        Assert.Throws<InvalidOperationException>(() => (body with { WaterRate = falseLabel }).Validate());
        CharacterContextLifecycleTests.AssertFrozenVisibleGraph(body);
    }

    [Theory]
    [InlineData("past")]
    [InlineData("order")]
    [InlineData("current-value")]
    public void Cold_open_rejects_malformed_historical_forecasts_even_when_latest_material_is_valid(string corruption) {
        PlaygroundWorld opened = OpenWorld();
        PlaygroundAttributeForecast historical = Context(opened).Opening.Observation.Survival!.FoodForecast;
        PlaygroundWorld world = PlaygroundEngine.Fold(opened, new(opened.Time, 0),
            ChangeRate(opened, new([new("fixture.updated", "新来源", 6)])));
        PlaygroundAttributeForecast latest = Context(world).LatestObservation.Survival!.FoodForecast;
        Assert.NotSame(historical, latest);
        latest.Validate(world.Time);
        var root = new PlaygroundDurableRoot(world.Business, InitialCursor(world));
        var invalidWarning = corruption switch {
            "past" => Hour(7),
            "order" => Hour(15),
            "current-value" => world.Time,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        typeof(PlaygroundAttributeForecast).GetField("<WarningAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(historical, invalidWarning);
        using var store = new TemporaryStore();
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        using (var repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("malformed-forecast", root);
        }
        Assert.Throws<InvalidOperationException>(() => PlaygroundDurableHistory.Open(store.StorePath));
    }

    [Fact]
    public void Observation_equality_includes_each_forecast_even_when_values_rates_and_earliest_depletion_are_equal() {
        PlaygroundObservation before = Context(OpenWorld()).Opening.Observation;
        PlaygroundSurvivalObservation body = before.Survival!;
        PlaygroundSurvivalObservation[] alternatives = [
            body with { FoodForecast = body.FoodForecast with { WarningAt = Hour(10) } },
            body with { WaterForecast = body.WaterForecast with { WarningAt = Hour(11) } },
            body with { WarmthForecast = body.WarmthForecast with { WarningAt = Hour(23) } },
            body with { WarmthForecast = body.WarmthForecast with { DepletionAt = Hour(25) } },
            body with { WarningThreshold = 24 }
        ];
        foreach (PlaygroundSurvivalObservation alternative in alternatives) {
            Assert.Equal(body.FatalAt, alternative.FatalAt);
            Assert.False(PlaygroundObservationEquality.Same(before, CopyObservation(before, alternative)));
        }
    }

    private static PlaygroundWorld OpenWorld(RateBreakdown? warmth = null) {
        PlaygroundWorld world = PlaygroundGameFactory.CreateSurvival(Hour(8), enableWeather: false).World;
        if (warmth is not null) {
            PlaygroundCharacter actor = world.GetCharacter("a");
            PlaygroundSurvival body = actor.Survival!;
            world = PlaygroundEngine.Rebuild(world.Business.Replace(world.Time,
                changedCharacters: [actor.WithSurvival(body.WithRates(world.Time, body.FoodRate, body.WaterRate, warmth))]), world.Time);
        }
        return PlaygroundEngine.Fold(world, new LogicalInstant(world.Time, 0), PlaygroundInteractionRules.OpenContext(world, "a"));
    }

    // A hidden resource event is a fixture carrier for a same-total physical replacement. It has
    // no visible event/effect for camp, isolating source-driven delivery from scalar event changes.
    private static PlaygroundFact ChangeRate(PlaygroundWorld world, RateBreakdown rate) {
        PlaygroundCharacter actor = world.GetCharacter("a");
        PlaygroundSurvival body = actor.Survival!;
        var replacement = actor.WithSurvival(body.WithRates(world.Time, body.FoodRate, body.WaterRate, rate));
        var fact = new PlaygroundFact(PlaygroundEventKind.ResourceDestroyed, world.Time,
            resourceId: "wood", changedResources: [world.GetResource("wood")], script: world.Script, changedCharacters: [replacement]);
        // Keep the normal location-observation boundary while varying only the rate explanation.
        return PlaygroundInteractionRules.Enrich(world, PlaygroundPlanning.Observe(world, fact));
    }

    private static CharacterContext Context(PlaygroundWorld world) => world.Business.InteractionStates["a"].CurrentContext!;

    private static PlaygroundObservation CopyObservation(PlaygroundObservation source, PlaygroundSurvivalObservation survival) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, ItemNames = source.ItemNames,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Journey = source.Journey,
        Passages = source.Passages, Memories = source.Memories, Outcome = source.Outcome,
        EndingReason = source.EndingReason, Survival = survival, Weather = source.Weather
    };

    private static PlaygroundFact ReplaceLastObservation(PlaygroundFact fact, PlaygroundObservation observation) {
        var state = Assert.Single(fact.InteractionChanges);
        var context = state.CurrentContext!;
        var item = Assert.Single(context.Items);
        var nextItem = new CharacterContextItem(item.ItemSequence, item.OperationSequence, item.RawInput,
            item.ToolCallId, item.OperationRecordId, item.Result, item.Experiences, observation, item.IsRejected);
        var nextContext = new CharacterContext(context.ContextSequence, context.Status, context.Opening,
            [nextItem], context.NextItemSequence, context.LastOperationSequence);
        return fact.WithInteractions([new CharacterInteractionState("a", nextContext, state.PendingAdvance,
            state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence)]);
    }
}
