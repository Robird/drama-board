using System.Collections;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Inventory;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class VisibleMaterialPersistenceTests {
    [Fact]
    public async Task Real_projected_material_cold_opens_and_historical_state_survives_an_instant_operation() {
        PlaygroundWorld world = RichSurvivalWorld();
        PlaygroundObservation before = PlaygroundDecisionContext.Observe(world, "a");
        PlaygroundBackground background = PlaygroundBackground.For(before);
        Assert.NotEmpty(before.Resources);
        Assert.Contains(before.Memories, memory => memory.Resources.Count != 0);
        Assert.Contains(before.Passages, passage => passage.KnownDestinationName is null);
        Assert.Contains(before.Passages, passage => passage.KnownDestinationName is not null);
        Assert.NotNull(before.Actor.Activity);
        Assert.NotEmpty(before.Actor.Notices);
        Assert.NotEmpty(before.Notices);
        Assert.NotEmpty(before.Survival!.PendingWarnings);
        Assert.Equal(24.5m, before.Survival.Food);
        Assert.Equal(PlaygroundWeatherKind.ColdWind, before.Weather!.LastKind);
        Assert.Contains(before.Commands, command => command.Rejection is not null);
        Assert.Null(before.CheckDisplayed(PlaygroundCommand.Use("item.water")));

        using var store = new TemporaryStore();
        var models = Models();
        BranchId branch;
        CommitId first;
        CommitId last;
        CommitId backgroundCommit;
        PlaygroundObservation after;
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            branch = repository.CreateRootBranchFromState("visible-observation", before).Id;
            first = repository.ReadCurrentCommit(branch).Commit.Id;
            BranchId backgroundBranch = repository.CreateRootBranchFromState("frozen-background", background).Id;
            backgroundCommit = repository.ReadCurrentCommit(backgroundBranch).Commit.Id;

            var kernel = PlaygroundEngine.CreateKernel(Memory(world), characterController: Commands(PlaygroundCommand.Use("item.water")));
            await StepWorldEvent(kernel);
            after = PlaygroundDecisionContext.Observe(kernel.World, "a");
            Assert.Equal(1, before.Actor.Backpack.Count("item.water"));
            Assert.Equal(0, after.Actor.Backpack.Count("item.water"));
            Assert.Equal(45.25m, before.Survival.Water);
            Assert.Equal(90.25m, after.Survival!.Water);
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            work.CommitState(after);
            last = repository.ReadCurrentCommit(branch).Commit.Id;
            Assert.NotEqual(first, last);
        }

        // A fresh registry and repository force disk hydration; no controller or world is supplied.
        using Repository reopened = Repository.OpenReadOnlyExisting(store.StorePath, Models());
        PlaygroundObservation restoredBefore = Assert.IsType<PlaygroundObservation>(reopened.LoadState(first));
        PlaygroundObservation restoredAfter = Assert.IsType<PlaygroundObservation>(reopened.LoadState(last));
        PlaygroundBackground restoredBackground = Assert.IsType<PlaygroundBackground>(reopened.LoadState(backgroundCommit));
        Assert.NotSame(before, restoredBefore);
        AssertMaterialEqual(before, restoredBefore);
        AssertMaterialEqual(after, restoredAfter);
        AssertMaterialEqual(background, restoredBackground);
        AssertFrozenVisibleGraph(restoredBefore);
        AssertFrozenVisibleGraph(restoredAfter);
        AssertFrozenVisibleGraph(restoredBackground);
        Assert.Equal(1, restoredBefore.Actor.Backpack.Count("item.water"));
        Assert.Equal(0, restoredAfter.Actor.Backpack.Count("item.water"));
        foreach (PlaygroundCommandCheck check in before.Commands) {
            PlaygroundCommand command = check.Command with { };
            Assert.Equal(check.Command, command);
            Assert.Equal(check.Rejection, restoredBefore.CheckDisplayed(command));
            Assert.Equal(check.Warning, restoredBefore.WarningDisplayed(command));
        }
    }

    [Fact]
    public async Task Gathering_initial_weather_and_travelling_nullable_material_round_trip_without_hidden_graphs() {
        var gathering = PlaygroundGameFactory.CreatePlayable(Hour(8), [
            new("a", "Alice"),
            new("b", "Bob", backpack: new ItemInventory(new Dictionary<string, int> { ["secret.gem"] = 7 }),
                activity: new(PlaygroundActivityKind.Wait, 1, Hour(8), PlaygroundTime.Hour, Hour(9)), nextActivityId: 2)
        ]).World;
        PlaygroundWorld survival = PlaygroundGameFactory.CreateSurvival().World;
        var moving = PlaygroundEngine.CreateKernel(Memory(survival), characterController: Commands(PlaygroundCommand.Go("trail-1")));
        await StepWorldEvent(moving);
        PlaygroundObservation[] snapshots = [PlaygroundDecisionContext.Observe(gathering, "a"),
            PlaygroundDecisionContext.Observe(survival, "a"), PlaygroundDecisionContext.Observe(moving.World, "a")];
        Assert.True(Assert.Single(snapshots[0].OtherCharacters).Waiting);
        Assert.Null(snapshots[0].Weather);
        Assert.Null(snapshots[0].Survival);
        Assert.Null(snapshots[1].Weather!.LastKind);
        Assert.Null(snapshots[1].Weather!.LastAt);
        Assert.Null(snapshots[1].ArrivalDue);
        // Ordinary actions reuse the shared attribute forecasts rather than duplicating risk prose.
        Assert.All(snapshots[1].Commands, command => Assert.Null(command.Warning));
        Assert.NotNull(snapshots[1].Survival!.FoodForecast.WarningAt);
        Assert.NotNull(snapshots[2].ArrivalDue);
        Assert.Null(snapshots[2].PlaceId);
        Assert.Equal(new PlaygroundJourneyObservation(PlaygroundExpeditionMap.GetPlace("camp").Name, "trail-1", "沿溪水声下坡", null), snapshots[2].Journey);
        Assert.Null(snapshots[1].Journey);

        using var store = new TemporaryStore();
        List<CommitId> commits = [];
        using (Repository repository = Repository.CreateNew(store.StorePath, Models())) {
            for (int index = 0; index < snapshots.Length; index++) {
                BranchId branch = repository.CreateRootBranchFromState($"observation-{index}", snapshots[index]).Id;
                commits.Add(repository.ReadCurrentCommit(branch).Commit.Id);
            }
        }
        using Repository reopened = Repository.OpenReadOnlyExisting(store.StorePath, Models());
        for (int index = 0; index < snapshots.Length; index++) {
            PlaygroundObservation restored = Assert.IsType<PlaygroundObservation>(reopened.LoadState(commits[index]));
            AssertMaterialEqual(snapshots[index], restored);
            AssertFrozenVisibleGraph(restored);
            foreach (PlaygroundCommandCheck check in snapshots[index].Commands) {
                Assert.Equal(check.Rejection, restored.CheckDisplayed(check.Command with { }));
                Assert.Equal(check.Warning, restored.WarningDisplayed(check.Command with { }));
            }
        }
    }

    [Fact]
    public void Construction_and_record_copy_freeze_input_collections_including_nested_material() {
        PlaygroundObservation source = PlaygroundDecisionContext.Observe(RichSurvivalWorld(), "a");
        List<PlaygroundResource> resources = [.. source.Resources];
        Dictionary<string, long> durations = new(source.HarvestDurations);
        Dictionary<string, string> names = new(source.ItemNames);
        List<PlaygroundOtherCharacterObservation> others = [new("b", "Bob", null, true)];
        List<PlaygroundNoticeObservation> notices = [.. source.Notices];
        List<PlaygroundCommandCheck> commands = [.. source.Commands];
        List<PlaygroundPassageObservation> passages = [.. source.Passages];
        List<PlaygroundResource> rememberedResources = [.. source.Memories.First(memory => memory.Resources.Count > 0).Resources];
        var memory = new PlaygroundPlaceMemory("旧地点", Hour(10), rememberedResources, "cabin");
        List<PlaygroundPlaceMemory> memories = [memory];
        List<PlaygroundNotice> actorNotices = [.. source.Actor.Notices];
        PlaygroundActorObservation actor = source.Actor with { Notices = actorNotices };
        List<string> warnings = ["保留的警告"];
        PlaygroundSurvivalObservation survival = source.Survival! with { PendingWarnings = warnings };
        var snapshot = new PlaygroundObservation {
            Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange,
            Actor = actor, Resources = resources, HarvestDurations = durations, ItemNames = names,
            OtherCharacters = others, Notices = notices, Commands = commands, IsExpedition = true,
            PlaceId = source.PlaceId, PlaceName = source.PlaceName, PlaceDescription = source.PlaceDescription,
            Passages = passages, Memories = memories, Survival = survival, Weather = source.Weather
        };
        PlaygroundBackground background = PlaygroundBackground.For(snapshot);
        string[] rules = [.. background.Rules];
        var replacementMemory = memory with { Resources = rememberedResources };
        var constructedActor = new PlaygroundActorObservation(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack,
            actor.Activity, actorNotices, actor.LastResult, actor.NextActivityId);
        var constructedBody = new PlaygroundSurvivalObservation(24.5m, 45.25m, 60.125m, Rate(-5), Rate(-8), Rate(6), false,
            warnings, new(Hour(10), Hour(20)), new(Hour(11), Hour(21)), new(Hour(12), Hour(22)), true, 4);

        resources.Clear(); durations.Clear(); names.Clear(); others.Clear(); notices.Clear(); commands.Clear();
        passages.Clear(); memories.Clear(); rememberedResources.Clear(); actorNotices.Clear(); warnings.Clear();

        Assert.NotEmpty(snapshot.Resources);
        Assert.NotEmpty(snapshot.HarvestDurations);
        Assert.NotEmpty(snapshot.ItemNames);
        Assert.Single(snapshot.OtherCharacters);
        Assert.NotEmpty(snapshot.Notices);
        Assert.NotEmpty(snapshot.Commands);
        Assert.NotEmpty(snapshot.Passages);
        Assert.NotEmpty(Assert.Single(snapshot.Memories).Resources);
        Assert.NotEmpty(replacementMemory.Resources);
        Assert.NotEmpty(snapshot.Actor.Notices);
        Assert.NotEmpty(constructedActor.Notices);
        Assert.Equal("保留的警告", Assert.Single(snapshot.Survival!.PendingWarnings));
        Assert.Equal("保留的警告", Assert.Single(constructedBody.PendingWarnings));
        Assert.Equal(rules, background.Rules);
        AssertFrozenVisibleGraph(snapshot);
    }

    private static PlaygroundWorld RichSurvivalWorld() {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Minute(930), weatherSeed: 0).World;
        var actor = new PlaygroundCharacter("a", "阿青", skillLevel: 2,
            backpack: initial.GetCharacter("a").Backpack,
            activity: new(PlaygroundActivityKind.Wait, 1, Minute(930), PlaygroundTime.Hour, Minute(990)),
            notices: [new(PlaygroundNoticeKind.ResourceDepleted, "berries", null, "fixture/depletion", Minute(920), "此前已耗尽")],
            lastResult: "已观察溪流", nextActivityId: 2,
            survival: new(Minute(930), PlaygroundSurvival.Scale * 49 / 2, PlaygroundSurvival.Scale * 181 / 4,
                PlaygroundSurvival.Scale * 481 / 8, PlaygroundSurvivalRules.FoodRate, PlaygroundSurvivalRules.WaterRate, PlaygroundSurvivalRules.WarmthRate(false, true), warnedMask: 1, pendingMask: 1, pendingSince: Minute(920)));
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, [new(new("a"), new("stream"))]);
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial, [
            new("a", "camp", Hour(14), []),
            new("a", "cabin", Hour(13), initial.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == "cabin"))
        ], environment: new(0, 1, Minute(990), Minute(870), true, [new("a", PlaygroundSurvival.Scale * 17 / 2, false)]));
        return PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor], expedition: expedition), initial.Time);
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        return models;
    }

    // Compare all public semantic properties recursively, without JSON storage, output formatting,
    // object identity or generated DurableGraph plumbing becoming the test oracle.
    private static void AssertMaterialEqual(object? expected, object? actual) {
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || expected is string or decimal) { Assert.Equal(expected, actual); return; }
        if (expected is IDictionary expectedMap) {
            IDictionary actualMap = Assert.IsAssignableFrom<IDictionary>(actual);
            Assert.Equal(expectedMap.Count, actualMap.Count);
            foreach (DictionaryEntry entry in expectedMap) {
                Assert.True(actualMap.Contains(entry.Key));
                AssertMaterialEqual(entry.Value, actualMap[entry.Key]);
            }
            return;
        }
        if (expected is IEnumerable expectedItems) {
            IEnumerable actualItems = Assert.IsAssignableFrom<IEnumerable>(actual);
            object?[] left = expectedItems.Cast<object?>().ToArray();
            object?[] right = actualItems.Cast<object?>().ToArray();
            Assert.Equal(left.Length, right.Length);
            for (int index = 0; index < left.Length; index++) { AssertMaterialEqual(left[index], right[index]); }
            return;
        }
        Assert.Equal(type, actual.GetType());
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) {
                AssertMaterialEqual(property.GetValue(expected), property.GetValue(actual));
            }
        }
    }

    private static void AssertFrozenVisibleGraph(object root) => Visit(root, new(ReferenceEqualityComparer.Instance));

    private static void Visit(object? value, HashSet<object> seen) {
        if (value is null) { return; }
        Assert.False(value is Delegate or PlaygroundWorld or PlaygroundBusinessState or PlaygroundCharacter or PlaygroundDecisionContext or
            CharacterInteractionState or CharacterContext or PlaygroundExpedition or GraphSpatialState, $"Forbidden visible reference: {value.GetType().Name}");
        Assert.NotEqual("secret.gem", value as string);
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or decimal || !seen.Add(value)) { return; }
        if (value is IDictionary dictionary) {
            Assert.True(dictionary.IsReadOnly, $"Mutable visible dictionary: {type.Name}");
            Assert.Throws<NotSupportedException>(() => dictionary.Clear());
        } else if (value is IList list) {
            Assert.True(list.IsReadOnly, $"Mutable visible list: {type.Name}");
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        if (value is IEnumerable entries) {
            foreach (object? entry in entries) { Visit(entry, seen); }
            return;
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.CanRead && property.GetIndexParameters().Length == 0) { Visit(property.GetValue(value), seen); }
        }
    }
}
