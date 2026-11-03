using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class PathQueryTests {
    [Fact]
    public async Task Queries_replace_clear_or_preserve_the_candidate_without_refreshing_observations_or_confirming_attention() {
        PlaygroundWorld initial = KnownWorld(needs: true);
        PlaygroundCharacter resident = initial.GetCharacter("a");
        var pending = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 20 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), Rate(0), Rate(0), warnedMask: 2, pendingMask: 2, pendingSince: initial.Time);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters:
            [resident.Change(null, survival: pending, communication: resident.Communication.Receive())]), initial.Time);
        var script = new RawScript("path stream", "map", "path stream", "path missing", "path signal", "path stream from stream");
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: script);
        string visits = MaterialSnapshot(initial.Expedition!.Visits);
        string body = MaterialSnapshot(initial.GetCharacter("a").Survival);
        string communication = MaterialSnapshot(initial.GetCharacter("a").Communication);
        await UntilOperation(kernel);
        PlaygroundPath first = kernel.World.Expedition!.CandidatePath("a")!;
        Assert.Equal("path:a:1:1", first.Id);
        Assert.Equal("e-c7", Assert.Single(first.Steps).EntranceId);
        await UntilOperation(kernel);
        Assert.Same(first, kernel.World.Expedition.CandidatePath("a"));
        Assert.Contains("本人已知地图", kernel.LastCompletion!.Event.Facts.Single().Result);
        await UntilOperation(kernel);
        PlaygroundPath second = kernel.World.Expedition.CandidatePath("a")!;
        Assert.Equal("path:a:1:3", second.Id);
        Assert.NotEqual(first.Id, second.Id);
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Same(second, kernel.World.Expedition.CandidatePath("a"));
        await UntilOperation(kernel);
        Assert.Null(kernel.World.Expedition.CandidatePath("a"));
        Assert.Contains("已知地图中未找到", kernel.LastCompletion!.Event.Facts.Single().Result);
        await UntilOperation(kernel);
        PlaygroundPath zero = kernel.World.Expedition.CandidatePath("a")!;
        Assert.Empty(zero.Steps);
        Assert.Equal("stream", zero.OriginPlaceId);
        Assert.Equal("camp", PlaygroundActions.CurrentPlace(kernel.World, "a"));
        Assert.Contains("规划起终点相同", kernel.LastCompletion!.Event.Facts.Single().Result);
        Assert.DoesNotContain("已在目标", kernel.LastCompletion!.Event.Facts.Single().Result!);
        Assert.Equal(initial.Time, kernel.World.Time);
        Assert.Equal(visits, MaterialSnapshot(kernel.World.Expedition.Visits));
        Assert.Equal(body, MaterialSnapshot(kernel.World.GetCharacter("a").Survival));
        Assert.Equal(communication, MaterialSnapshot(kernel.World.GetCharacter("a").Communication));
        Assert.Single(kernel.World.Expedition.ConfirmedPassages);
        Assert.Equal(CharacterContextStatus.Open, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
        Assert.Equal(6, script.Calls);
    }

    [Fact]
    public async Task Travel_warning_allows_explicit_origin_queries_but_neither_default_origin_nor_queries_change_the_segment_or_reminder() {
        PlaygroundWorld initial = KnownWorld(needs: true);
        PlaygroundCharacter actor = initial.GetCharacter("a");
        var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 29 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0));
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            new RawScript("go e-c7", "path stream", "map", "path stream from camp", "continue"));
        await UntilOperation(kernel);
        await Until(kernel, fact => fact.Kind == PlaygroundEventKind.SurvivalWarning);
        PlaygroundWorld warned = kernel.World;
        TraversingLocation segment = (TraversingLocation)PlaygroundActions.Location(warned, "a")!;
        string visits = MaterialSnapshot(warned.Expedition!.Visits);
        string reminder = MaterialSnapshot(warned.GetCharacter("a").Survival);
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Null(kernel.World.Expedition!.CandidatePath("a"));
        await UntilOperation(kernel);
        await UntilOperation(kernel);
        Assert.NotNull(kernel.World.Expedition!.CandidatePath("a"));
        Assert.Equal(segment, PlaygroundActions.Location(kernel.World, "a"));
        Assert.Equal(warned.Time, kernel.World.Time);
        Assert.Equal(visits, MaterialSnapshot(kernel.World.Expedition.Visits));
        Assert.Equal(reminder, MaterialSnapshot(kernel.World.GetCharacter("a").Survival));
        await UntilOperation(kernel);
        Assert.Equal(CharacterContextStatus.Closed, kernel.World.Business.InteractionStates["a"].CurrentContext!.Status);
    }

    [Fact]
    public async Task Each_actor_owns_a_different_candidate_even_in_the_same_context_and_operation_number() {
        PlaygroundWorld initial = KnownWorld(count: 2);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController:
            new FollowingTestSupport.Script([PlaygroundCommand.Path("stream")], [PlaygroundCommand.Path("stream")]));
        await UntilOperation(kernel); await UntilOperation(kernel);
        PlaygroundPath a = kernel.World.Expedition!.CandidatePath("a")!, b = kernel.World.Expedition.CandidatePath("b")!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, PlaygroundDecisionContext.Observe(kernel.World, "a").CandidatePath!.Id);
        Assert.Equal(b.Id, PlaygroundDecisionContext.Observe(kernel.World, "b").CandidatePath!.Id);
    }

    [Fact]
    public async Task Death_clears_the_candidate_without_recreating_it_from_the_previous_frozen_context() {
        PlaygroundWorld initial = KnownWorld(needs: true);
        PlaygroundCharacter actor = initial.GetCharacter("a");
        var body = new PlaygroundSurvival(initial.Time, 100 * PlaygroundSurvival.Scale, 8 * PlaygroundSurvival.Scale,
            100 * PlaygroundSurvival.Scale, Rate(0), PlaygroundSurvivalRules.WaterRate, Rate(0), warnedMask: 2);
        initial = PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, changedCharacters: [actor.WithSurvival(body)]), initial.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new RawScript("path stream", "go e-c7"));
        await UntilOperation(kernel); Assert.NotNull(kernel.World.Expedition!.CandidatePath("a"));
        await Until(kernel, fact => fact.Kind == PlaygroundEventKind.SurvivalFailed);
        Assert.Null(kernel.World.Expedition!.CandidatePath("a"));
        Assert.Null(PlaygroundDecisionContext.Observe(kernel.World, "a").CandidatePath);
    }

    [Fact]
    public async Task An_unrepresentable_known_route_is_a_recorded_rejection_and_keeps_the_previous_candidate() {
        PlaygroundWorld basis = KnownWorld();
        PlaygroundWorldDefinition definition = basis.Definition.Change(passages: basis.Definition.Passages.Select(p =>
            p.Id == "trail-1" ? p with { DurationTicks = long.MaxValue } : p));
        var expedition = new PlaygroundExpedition(basis.Expedition!.ScenarioId, basis.Expedition.Spatial, basis.Expedition.Visits,
            confirmedPassages: [new("a", "trail-1"), new("a", "trail-3")]);
        var business = new PlaygroundBusinessState(basis.Time, basis.Light, basis.Resources, basis.Script, basis.Characters,
            expedition: expedition, definition: definition);
        PlaygroundWorld initial = PlaygroundEngine.Rebuild(business, basis.Time);
        var kernel = PlaygroundEngine.CreateKernel(Memory(initial), characterController: new RawScript("path camp", "path ravine"));
        await UntilOperation(kernel);
        PlaygroundPath original = kernel.World.Expedition!.CandidatePath("a")!;
        await UntilOperation(kernel);
        Assert.Equal(PlaygroundEventKind.OperationRejected, kernel.LastCompletion!.Event.Facts.Single().Kind);
        Assert.Contains("超过支持范围", kernel.LastCompletion.Event.Facts.Single().Result);
        Assert.Same(original, kernel.World.Expedition!.CandidatePath("a"));
    }

    [Fact]
    public void Path_value_freezes_its_step_collection_and_record_copy_replacements() {
        List<PlaygroundPathStep> steps = [new("camp", "e-c7", "stream", PlaygroundTime.Hour)];
        var path = new PlaygroundPath("path:a:1:1", "a", 1, 1, "camp", "stream", steps, PlaygroundTime.Hour);
        PlaygroundPath copy = path with { Steps = steps };
        steps.Clear();
        Assert.Single(path.Steps);
        Assert.Single(copy.Steps);
        Assert.Throws<NotSupportedException>(() => ((IList<PlaygroundPathStep>)path.Steps).Clear());
    }

    [Theory]
    [InlineData("extra place")]
    [InlineData("extra\tplace")]
    [InlineData("extra\nplace")]
    public void Place_command_identity_rejects_whitespace_without_restricting_human_readable_material(string id) {
        PlaygroundWorldDefinition definition = PlaygroundWorldDefinitions.LivingWorld();
        Assert.Throws<InvalidDataException>(() => definition.Change(places: definition.Places.Append(new(id, "有 空格 的地点", "有 空格 的描述"))));
        Assert.NotNull(definition.Change(places: definition.Places.Append(new("extra-place", "有 空格 的地点", "有 空格 的描述"))));
    }

    [Theory]
    [InlineData("map")]
    [InlineData("path camp")]
    [InlineData("path stream from camp")]
    public void Commands_round_trip_and_invalid_syntax_does_not_become_a_partial_query(string input) {
        PlaygroundCommand command = PlaygroundCommandParser.Parse(input)!;
        Assert.Equal(input, PlaygroundCommandParser.Format(command));
        Assert.Null(PlaygroundCommandParser.Parse(input + " unexpected"));
    }

    internal static PlaygroundWorld KnownWorld(int count = 1, bool needs = false) {
        PlaygroundWorld initial = PlaygroundGameFactory.CreateSurvival(Hour(8), enableNeeds: needs, enableWeather: false, characterCount: count).World;
        PlaygroundVisit[] visits = initial.Characters.SelectMany(actor => initial.Definition.Places.Select(place => new PlaygroundVisit(
            actor.Id, place.Id, Hour(7), initial.Resources.Where(resource => initial.Definition.GetResourcePlace(resource.Id) == place.Id)))).ToArray();
        var expedition = new PlaygroundExpedition(initial.Expedition!.ScenarioId, initial.Expedition.Spatial, visits,
            confirmedPassages: initial.Characters.Select(actor => new PlaygroundConfirmedPassage(actor.Id, "trail-1")));
        return PlaygroundEngine.Rebuild(initial.Business.Replace(initial.Time, expedition: expedition), initial.Time);
    }

    internal static async Task UntilOperation(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) =>
        await Until(kernel, fact => fact.OperationRecordId is not null);
    internal static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundFact, bool> match) {
        for (int step = 0; step < 60; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
            if (match(kernel.LastCompletion!.Event.Facts.Single())) { return; }
        }
        Assert.Fail("Expected path-query boundary was not reached.");
    }
    internal sealed class RawScript(params string[] commands) : IPlaygroundCharacterController {
        private readonly Queue<string> _commands = new(commands);
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string id = "path-fixture/" + ++Calls;
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, _commands.Dequeue(), id));
        }
    }
}
