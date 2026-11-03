using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.PathQueryTests;

namespace DramaBoard.Playground.Tests;

public sealed class NavigationTests {
    [Fact]
    public async Task Navigation_stops_at_each_place_and_candidate_replacement_does_not_change_the_selected_route() {
        var script = new NavigationScript("path ravine", "navigate-current", "map", "path camp", "wait 1", "go e-h5", "follow b", "continue");
        var kernel = PlaygroundEngine.CreateKernel(Memory(World()), characterController: script);
        await UntilOperation(kernel); await UntilOperation(kernel);
        PlaygroundPath selected = kernel.World.Expedition!.Navigation("a")!.Path;
        Assert.Equal(PlaygroundCommandKind.Navigate, kernel.World.Business.InteractionStates["a"].PendingAdvance!.Command.Kind);
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        Assert.Equal("stream", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.Equal(1, kernel.World.Expedition!.Navigation("a")!.CompletedSteps);
        Assert.True(PlaygroundActions.NeedsDecision(kernel.World, "a"));
        Assert.Equal(2, script.Calls);
        await UntilOperation(kernel); // map opens and observes the intermediate stop, without departing.
        Assert.Equal(PlaygroundOutcomeActivityState.Ongoing, kernel.World.Business.InteractionStates["a"].CurrentContext!.Opening.ActivityState);
        await UntilOperation(kernel); // new candidate
        Assert.NotEqual(selected.Id, kernel.World.Expedition!.CandidatePath("a")!.Id);
        Assert.Same(selected, kernel.World.Expedition.Navigation("a")!.Path);
        for (int refusal = 0; refusal < 3; refusal++) {
            await UntilOperation(kernel);
            Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
            Assert.Equal("stream", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        }
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.MoveStarted, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Equal(PlaygroundCommandKind.Continue, kernel.World.Business.InteractionStates["a"].PendingAdvance!.Command.Kind);
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        Assert.Equal("ravine", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.Null(kernel.World.Expedition!.Navigation("a"));
    }

    [Fact]
    public async Task Travel_cancel_preserves_reminder_and_segment_then_continue_releases_only_the_current_leg() {
        PlaygroundWorld initial = World(needs: true);
        PlaygroundCharacter actor = initial.GetCharacter("a");
        var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 29 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0));
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            new NavigationScript("path ravine", "navigate-current", "cancel", "use item.water", "continue"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.SurvivalWarning);
        SpatialLocation segment = PlaygroundActions.Location(kernel.World, "a")!;
        string warning = MaterialSnapshot(kernel.World.GetCharacter("a").Survival);
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.NavigationCancelled, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Null(kernel.World.Expedition!.Navigation("a"));
        Assert.Equal(segment, PlaygroundActions.Location(kernel.World, "a"));
        Assert.Equal(warning, MaterialSnapshot(kernel.World.GetCharacter("a").Survival));
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        await UntilOperation(kernel);
        Assert.True(kernel.World.GetCharacter("a").Survival!.PendingMask > 0);
        await UntilOperation(kernel);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        Assert.Equal("stream", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.Null(kernel.World.Expedition!.Navigation("a"));
        Assert.True(kernel.World.Expedition.KnowsPassage("a", "trail-1"));
    }

    [Fact]
    public async Task Zero_step_navigation_is_instant_but_an_explicit_different_origin_cannot_be_started() {
        var kernel = PlaygroundEngine.CreateKernel(Memory(World()), characterController:
            new NavigationScript("path stream from stream", "navigate-current", "path camp", "navigate-current"));
        await UntilOperation(kernel); await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        await UntilOperation(kernel); await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.NavigationCompleted, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Equal(Hour(8), kernel.World.Time);
        Assert.Null(kernel.World.Expedition!.Navigation("a"));
        Assert.Null(kernel.World.Business.InteractionStates["a"].PendingAdvance);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
    }

    [Fact]
    public async Task Followers_travel_without_inheriting_the_plan_and_learn_only_on_their_own_arrival() {
        var script = new NavigationScript("path ravine", "navigate-current", "continue");
        var kernel = PlaygroundEngine.CreateKernel(Memory(World(count: 3)), characterController: script);
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveStarted);
        foreach (string follower in new[] { "b", "c" }) {
            PlaygroundObservation view = PlaygroundDecisionContext.Observe(kernel.World, follower);
            Assert.Null(view.Navigation); Assert.Null(view.CandidatePath);
            Assert.Null(view.Journey!.KnownDestinationName);
        }
        for (int arrival = 0; arrival < 3; arrival++) {
            await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
            foreach (SpatialEntity entity in kernel.World.Expedition!.Spatial.Entities.Where(e => e.Id.Value != "a")) {
                Assert.Equal(entity.Location is AtPlaceLocation, kernel.World.Expedition.KnowsPassage(entity.Id.Value, "trail-1"));
            }
            Assert.Equal(2, script.Calls);
        }
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveStarted);
        Assert.Null(PlaygroundDecisionContext.Observe(kernel.World, "b").Journey!.KnownDestinationName);
        Assert.Null(PlaygroundDecisionContext.Observe(kernel.World, "b").Navigation);
    }

    [Fact]
    public async Task All_three_departure_commands_share_the_same_single_segment_danger_preview() {
        PlaygroundWorld initial = World(needs: true, place: "cabin", confirmed: ["trail-7", "trail-3"]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new NavigationScript("path ravine"));
        await UntilOperation(kernel);
        PlaygroundWorld ready = kernel.World;
        PlaygroundPath path = ready.Expedition!.CandidatePath("a")!;
        string? go = PlaygroundSurvivalRules.ActionWarning(ready, "a", PlaygroundCommand.Go("e-o7"));
        Assert.NotNull(go);
        Assert.Equal(go, PlaygroundSurvivalRules.ActionWarning(ready, "a", PlaygroundCommand.Navigate(path.Id)));
        var navigation = new PlaygroundNavigation(path, 0);
        PlaygroundExpedition old = ready.Expedition;
        var active = new PlaygroundExpedition(old.ScenarioId, old.Spatial, old.Visits, old.Environment, old.ConfirmedPassages, old.CandidatePaths, [navigation]);
        PlaygroundWorld waiting = PlaygroundEngine.Rebuild(ready.Business.Replace(ready.Time, expedition: active), ready.Time);
        Assert.Equal(go, PlaygroundSurvivalRules.ActionWarning(waiting, "a", PlaygroundCommand.Continue()));
    }

    [Fact]
    public async Task A_follower_who_cannot_start_refuses_navigation_for_everyone_without_creating_a_plan() {
        PlaygroundWorld initial = ExhaustFollowerMovement(World(count: 3));
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new NavigationScript("path ravine", "navigate-current"));
        await UntilOperation(kernel); await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Contains("所有人保持原状", kernel.LastCompletion.Event.Facts.Single().Result);
        Assert.Empty(kernel.World.Expedition!.Navigations);
        Assert.All(kernel.World.Expedition.Spatial.Entities, e => Assert.IsType<AtPlaceLocation>(e.Location));
    }

    [Fact]
    public async Task Failed_continue_keeps_the_route_all_positions_and_the_leaders_unacknowledged_message() {
        var travel = PlaygroundEngine.CreateKernel(Memory(World(count: 3)), characterController: new NavigationScript("path ravine", "navigate-current"));
        await Until(travel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        await UntilAllArrived(travel);
        PlaygroundWorld waiting = ExhaustFollowerMovement(travel.World);
        var driver = new NavigationScript("say b 请回复", "say a 我还在这里", "continue");
        var kernel = PlaygroundEngine.CreateKernel(Memory(waiting), characterController: driver);
        await UntilOperation(kernel); await UntilOperation(kernel);
        PlaygroundNavigation active = kernel.World.Expedition!.Navigation("a")!;
        GraphSpatialState spatial = kernel.World.Expedition.Spatial;
        PlaygroundCommunicationState pending = kernel.World.GetCharacter("a").Communication;
        Assert.True(pending.AttentionPending);
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Same(active, kernel.World.Expedition!.Navigation("a"));
        Assert.Same(spatial, kernel.World.Expedition.Spatial);
        Assert.Equal(pending, kernel.World.GetCharacter("a").Communication);
    }

    [Fact]
    public async Task Leader_death_clears_candidate_and_navigation_without_advancing_the_survivors_along_the_rest_of_the_route() {
        PlaygroundWorld initial = World(count: 3, needs: true);
        PlaygroundCharacter actor = initial.GetCharacter("a");
        var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0), warnedMask: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new NavigationScript("path ravine", "navigate-current"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.SurvivalFailed);
        Assert.Null(kernel.World.Expedition!.Navigation("a"));
        Assert.Null(kernel.World.Expedition.CandidatePath("a"));
        Assert.False(kernel.World.GetCharacter("a").IsActive);
        foreach (string survivor in new[] { "b", "c" }) {
            Assert.Null(kernel.World.GetCharacter(survivor).FollowingLeaderId);
            Assert.Null(kernel.World.Expedition.Navigation(survivor));
        }
        await UntilAllArrived(kernel);
        Assert.All(kernel.World.Expedition.Spatial.Entities, e => Assert.Equal("stream", Assert.IsType<AtPlaceLocation>(e.Location).PlaceId.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_and_other_actors_path_ids_cannot_start_navigation(bool otherActor) {
        PlaygroundWorld initial = World(count: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [initial.GetCharacter("b").WithFollowing(null)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: Commands(
            PlaygroundCommand.Path("stream"), PlaygroundCommand.Path("camp"), PlaygroundCommand.Path("ravine"), PlaygroundCommand.Wait(1),
            PlaygroundCommand.Navigate(otherActor ? "path:b:1:1" : "path:a:1:1")));
        for (int operation = 0; operation < 4; operation++) { await UntilOperation(kernel); }
        PlaygroundPath candidate = kernel.World.Expedition!.CandidatePath("a")!;
        Assert.Equal("path:a:1:2", candidate.Id);
        Assert.Equal("path:b:1:1", kernel.World.Expedition.CandidatePath("b")!.Id);
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Same(candidate, kernel.World.Expedition.CandidatePath("a"));
        Assert.Null(kernel.World.Expedition.Navigation("a"));
        Assert.Equal("camp", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.Equal(Hour(8), kernel.World.Time);
    }

    [Fact]
    public async Task A_followers_death_preserves_the_leaders_route_and_only_the_leaders_arrival_advances_it() {
        PlaygroundWorld initial = World(count: 3, needs: true);
        PlaygroundCharacter actor = initial.GetCharacter("b");
        var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0), warnedMask: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new NavigationScript("path ravine", "navigate-current"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveStarted);
        PlaygroundNavigation active = kernel.World.Expedition!.Navigation("a")!;
        await Until(kernel, f => f.Kind == PlaygroundEventKind.SurvivalFailed);
        Assert.False(kernel.World.GetCharacter("b").IsActive);
        Assert.Same(active, kernel.World.Expedition!.Navigation("a"));
        Assert.Null(kernel.World.Expedition.Navigation("b"));
        Assert.Null(kernel.World.Expedition.CandidatePath("b"));
        await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived && f.ActorId == "a");
        Assert.Equal(1, kernel.World.Expedition.Navigation("a")!.CompletedSteps);
        Assert.Equal(active.Path.Id, kernel.World.Expedition.Navigation("a")!.Path.Id);
    }

    private static PlaygroundWorld ExhaustFollowerMovement(PlaygroundWorld world) {
        PlaygroundExpedition old = world.Expedition!;
        GraphSpatialState spatial = GraphSpatialState.Restore(world.Definition.Runtime.Graph,
            old.Spatial.Entities.Select(e => e.Id.Value == "b" ? new SpatialEntity(e.Id, long.MaxValue, e.Location) : e), [], [], old.Spatial.ConsumedContacts);
        var expedition = new PlaygroundExpedition(old.ScenarioId, spatial, old.Visits, old.Environment, old.ConfirmedPassages, old.CandidatePaths, old.Navigations);
        return PlaygroundEngine.Rebuild(world.Business.Replace(world.Time, expedition: expedition), world.Time);
    }

    internal static async Task UntilAllArrived(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        while (kernel.World.Expedition!.Spatial.Entities.Any(e => e.Location is TraversingLocation)) {
            await Until(kernel, f => f.Kind == PlaygroundEventKind.MoveArrived);
        }
    }

    internal static PlaygroundWorld World(int count = 1, bool needs = false, string place = "camp", string[]? confirmed = null) {
        PlaygroundWorld basis = KnownWorld(count, needs);
        PlaygroundVisit[] visits = basis.Expedition!.Visits.Where(v => v.ActorId == "a" || v.PlaceId == place).ToArray();
        PlaygroundCharacter[] followers = basis.Characters.Where(c => c.Id != "a").Select(c => c.WithFollowing("a")).ToArray();
        var spatial = GraphSpatialState.Create(basis.Definition.Runtime.Graph, basis.Characters.Select(c => new EntityPlacement(new(c.Id), new(place))));
        var expedition = new PlaygroundExpedition(basis.Expedition.ScenarioId, spatial, visits,
            confirmedPassages: (confirmed ?? ["trail-1", "trail-3"]).Select(p => new PlaygroundConfirmedPassage("a", p)));
        PlaygroundWorld world = PlaygroundEngine.Rebuild(basis.Business.Replace(basis.Time, changedCharacters: followers, expedition: expedition), basis.Time);
        if (place == "cabin" && needs) {
            PlaygroundCharacter actor = world.GetCharacter("a");
            world = PlaygroundEngine.Rebuild(world.Business.Replace(world.Time, changedCharacters: [actor.WithSurvival(
                PlaygroundSurvival.Create(world.Time, warmth: 30, warmthRate: PlaygroundSurvivalRules.WarmthRate(true, true)))]), world.Time);
        }
        return world;
    }

    internal sealed class NavigationScript(params string[] commands) : IPlaygroundCharacterController {
        private readonly Queue<string> _commands = new(commands);
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string command = _commands.Dequeue();
            if (command == "navigate-current") { command = "navigate " + context.Observation.CandidatePath!.Id; }
            string id = "navigation-fixture/" + ++Calls;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, command, id));
        }
    }
}
