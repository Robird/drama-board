using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.PathQueryTests;
using static DramaBoard.Playground.Tests.NavigationTests;

namespace DramaBoard.Playground.Tests;

public sealed class NavigationCompositionTests {
    [Fact]
    public async Task A_durable_fork_can_navigate_its_inherited_candidate_replace_it_and_cancel_without_changing_the_parent() {
        using var store = new TemporaryStore();
        PlaygroundWorld initial = World();
        PlaygroundDecisionBoundary source;
        string parentHead;
        string parentBusiness;
        KernelCursor parentCursor;
        string inheritedId;
        using (var parent = PlaygroundDurableHistory.Create(store.StorePath, initial, InitialCursor(initial))) {
            var kernel = PlaygroundEngine.CreateKernel(parent, characterController: new NavigationScript("path ravine"));
            await UntilOperation(kernel);
            source = parent.CaptureDecisionBoundary("a");
            parentHead = PlaygroundDurableHistory.FormatCommitId(parent.HeadCommitId);
            parentBusiness = MaterialSnapshot(parent.State.Business);
            parentCursor = parent.Cursor;
            inheritedId = parent.State.Expedition!.CandidatePath("a")!.Id;
        }
        PlaygroundBranchForkResult fork = PlaygroundDurableHistory.Fork(store.StorePath, source.BranchId,
            source.StateCommitId, "a", "inherited navigation composition");
        string childBusiness;
        string replacementId;
        using (var child = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(fork.Branch.BranchId))) {
            PlaygroundPath inherited = child.State.Expedition!.CandidatePath("a")!;
            Assert.Equal(inheritedId, inherited.Id);
            Assert.Null(child.State.Expedition.Navigation("a"));
            var kernel = PlaygroundEngine.CreateKernel(child, characterController:
                new NavigationScript("navigate-current", "path camp", "cancel"));
            await UntilOperation(kernel);
            Assert.Equal(PlaygroundEventKind.MoveStarted, kernel.LastCompletion!.Event.Facts.Single().Kind);
            Assert.Equal(PlaygroundCommand.Navigate(inheritedId), child.State.Business.InteractionStates["a"].PendingAdvance!.Command);
            Assert.Same(inherited, child.State.Expedition!.Navigation("a")!.Path);
            await Until(kernel, fact => fact.Kind == PlaygroundEventKind.MoveArrived);
            Assert.Equal("stream", PlaygroundActions.CurrentPlace(child.State, "a"));
            Assert.Equal(1, child.State.Expedition!.Navigation("a")!.CompletedSteps);
            PlaygroundNavigation stopped = child.State.Expedition.Navigation("a")!;
            await UntilOperation(kernel);
            replacementId = child.State.Expedition!.CandidatePath("a")!.Id;
            Assert.NotEqual(inheritedId, replacementId);
            Assert.Equal("camp", child.State.Expedition.CandidatePath("a")!.TargetPlaceId);
            Assert.Same(stopped, child.State.Expedition.Navigation("a"));
            Assert.Same(inherited, child.State.Expedition.Navigation("a")!.Path);
            Assert.Equal("ravine", child.State.Expedition.Navigation("a")!.Path.TargetPlaceId);
            await UntilOperation(kernel);
            Assert.Equal(PlaygroundEventKind.NavigationCancelled, kernel.LastCompletion!.Event.Facts.Single().Kind);
            Assert.Null(child.State.Expedition!.Navigation("a"));
            Assert.Equal("stream", PlaygroundActions.CurrentPlace(child.State, "a"));
            Assert.Equal(Hour(9), child.State.Time);
            childBusiness = MaterialSnapshot(child.State.Business);
        }
        using (var child = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(fork.Branch.BranchId))) {
            Assert.Equal(childBusiness, MaterialSnapshot(child.State.Business));
            Assert.Equal(replacementId, child.State.Expedition!.CandidatePath("a")!.Id);
            Assert.Null(child.State.Expedition.Navigation("a"));
            Assert.Null(child.PendingEvent);
        }
        using var reopenedParent = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(source.BranchId));
        Assert.Equal(parentHead, PlaygroundDurableHistory.FormatCommitId(reopenedParent.HeadCommitId));
        Assert.Equal(parentBusiness, MaterialSnapshot(reopenedParent.State.Business));
        Assert.Equal(parentCursor, reopenedParent.Cursor);
        Assert.Equal(inheritedId, reopenedParent.State.Expedition!.CandidatePath("a")!.Id);
        Assert.Null(reopenedParent.State.Expedition.Navigation("a"));
        Assert.Equal("camp", PlaygroundActions.CurrentPlace(reopenedParent.State, "a"));
    }

    [Fact]
    public async Task Say_supply_map_and_replanning_at_one_stop_do_not_depart_until_the_leader_continues() {
        PlaygroundWorld initial = World(count: 2, needs: true);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: initial.Characters.Select(actor =>
            actor.WithSurvival(PlaygroundSurvival.Create(initial.Time, food: 80, water: 80, warmth: 80)))), initial.Time);
        var history = Memory(initial);
        var script = new ActorScript(
            ("a", "path ravine"), ("a", "navigate-current"), ("a", "say b 我们先在这里补给再继续。"),
            ("b", "continue"), ("a", "use item.water"), ("a", "map"), ("a", "path camp"), ("a", "continue"));
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: script);
        await Until(kernel, fact => fact.Kind == PlaygroundEventKind.MoveStarted);
        await UntilAllArrived(kernel);
        PlaygroundNavigation stopped = kernel.World.Expedition!.Navigation("a")!;
        Assert.Equal(1, stopped.CompletedSteps);
        Assert.Equal(new[] { "camp", "stream" }, new[] { stopped.Path.OriginPlaceId, stopped.Path.Steps[0].ToPlaceId });
        Assert.Equal("ravine", stopped.Path.TargetPlaceId);
        Assert.Equal(2, script.Calls);
        ModelTime stopTime = kernel.World.Time;
        GraphSpatialState stopPositions = kernel.World.Expedition.Spatial;
        void AssertStillStopped() {
            Assert.Equal(stopTime, kernel.World.Time);
            Assert.Same(stopPositions, kernel.World.Expedition!.Spatial);
            Assert.All(kernel.World.Expedition.Spatial.Entities, entity => Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId.Value));
            Assert.Same(stopped, kernel.World.Expedition.Navigation("a"));
            Assert.Equal(1, kernel.World.Expedition.Navigation("a")!.CompletedSteps);
            Assert.Equal(1, history.CompletedEvents.SelectMany(e => e.Facts).Count(f => f.Kind == PlaygroundEventKind.MoveStarted));
        }
        await UntilOperation(kernel);
        PlaygroundFact said = kernel.LastCompletion!.Event.Facts.Single();
        Assert.Equal(PlaygroundEventKind.SpeechSent, said.Kind);
        PlaygroundObservedEvent heard = Assert.Single(said.ObservedEvents, entry => entry.ObserverId == "b" && entry.Kind == PlaygroundObservedEventKind.SpeechSent);
        Assert.Equal("a", heard.Communication!.SenderId);
        Assert.Equal("b", heard.Communication.RecipientId);
        Assert.Equal("我们先在这里补给再继续。", heard.Communication.SpeechText);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        AssertStillStopped();
        await UntilOperation(kernel); // The follower's acknowledgement is not the leader's next-leg confirmation.
        Assert.Equal("b", kernel.LastCompletion!.Event.Facts.Single().ActorId);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        AssertStillStopped();
        int waterBefore = kernel.World.GetCharacter("a").Backpack.Count("item.water");
        long bodyWaterBefore = kernel.World.GetCharacter("a").Survival!.Project(stopTime).WaterUnits;
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.ItemUsed, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Equal(waterBefore - 1, kernel.World.GetCharacter("a").Backpack.Count("item.water"));
        Assert.True(kernel.World.GetCharacter("a").Survival!.Project(stopTime).WaterUnits > bodyWaterBefore);
        AssertStillStopped();
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.MapQueried, kernel.LastCompletion!.Event.Facts.Single().Kind);
        AssertStillStopped();
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.PathQueried, kernel.LastCompletion!.Event.Facts.Single().Kind);
        PlaygroundPath replacement = kernel.World.Expedition!.CandidatePath("a")!;
        Assert.NotEqual(stopped.Path.Id, replacement.Id);
        Assert.Equal("camp", replacement.TargetPlaceId);
        AssertStillStopped();
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.MoveStarted, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Equal(PlaygroundCommand.Continue(), kernel.World.Business.InteractionStates["a"].PendingAdvance!.Command);
        Assert.All(kernel.World.Expedition!.Spatial.Entities, entity => Assert.Equal("ravine", Assert.IsType<TraversingLocation>(entity.Location).TargetPlaceId.Value));
        await UntilAllArrived(kernel);
        Assert.All(kernel.World.Expedition!.Spatial.Entities, entity => Assert.Equal("ravine", Assert.IsType<AtPlaceLocation>(entity.Location).PlaceId.Value));
        Assert.Null(kernel.World.Expedition.Navigation("a"));
        Assert.Equal(replacement.Id, kernel.World.Expedition.CandidatePath("a")!.Id);
        Assert.Null(kernel.World.Expedition.Navigation("b"));
        Assert.Null(kernel.World.Expedition.CandidatePath("b"));
        Assert.Equal(8, script.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigation_exclusivity_blocks_gathering_and_production_that_are_legal_after_cancelling(bool production) {
        PlaygroundWorld initial;
        if (production) {
            initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false, enableWeather: false).World;
            PlaygroundVisit[] visits = new[] { "home", "workshop", "market" }.Select(place => new PlaygroundVisit("a", place, initial.Time,
                initial.Resources.Where(resource => initial.Definition.GetResourcePlace(resource.Id) == place))).ToArray();
            var expedition = new PlaygroundExpedition(initial.Expedition!.ScenarioId, initial.Expedition.Spatial, visits,
                confirmedPassages: [new("a", "home-workshop"), new("a", "market-workshop")]);
            initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: expedition), initial.Time);
        } else { initial = World(); }
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            new NavigationScript(production ? "path market" : "path ravine", "navigate-current", "cancel"));
        await Until(kernel, fact => fact.Kind == PlaygroundEventKind.MoveArrived);
        Assert.Equal(production ? "workshop" : "stream", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        PlaygroundCommand action = production ? PlaygroundCommand.Produce("ration") : PlaygroundCommand.Gather("berries");
        PlaygroundNavigation active = kernel.World.Expedition!.Navigation("a")!;
        string? rejection = PlaygroundActions.Check(kernel.World, "a", action);
        Assert.NotNull(rejection);
        Assert.Contains("导航", rejection, StringComparison.Ordinal);
        Assert.Contains("cancel", rejection, StringComparison.Ordinal);
        Assert.Same(active, kernel.World.Expedition.Navigation("a"));
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.NavigationCancelled, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Null(PlaygroundActions.Check(kernel.World, "a", action));
    }

    private sealed class ActorScript(params (string ActorId, string Input)[] commands) : IPlaygroundCharacterController {
        private readonly Queue<(string ActorId, string Input)> _commands = new(commands);
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            var next = _commands.Dequeue();
            Assert.Equal(next.ActorId, context.Observation.Actor.Id);
            string input = next.Input == "navigate-current" ? "navigate " + context.Observation.CandidatePath!.Id : next.Input;
            string id = "navigation-composition/" + ++Calls;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, input, id));
        }
    }
}
