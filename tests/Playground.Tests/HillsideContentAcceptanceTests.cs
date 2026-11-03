using System.Security.Cryptography;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

/// <summary>Content identity is exercised through real world execution and durable E/S boundaries.</summary>
public sealed class HillsideContentAcceptanceTests {
    [Fact]
    public async Task Interleaved_default_and_variant_journeys_use_their_own_graph_resources_recipes_prices_and_physiology() {
        PlaygroundWorldDefinition normal = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1).World.Definition;
        PlaygroundWorldDefinition variant = Variant(normal);
        Assert.NotEqual(normal.Fingerprint, variant.Fingerprint);
        var first = new Journey(normal, "default");
        var second = new Journey(variant, "variant");
        for (int step = 0; step < 600 && (!first.Done || !second.Done); step++) {
            if (!first.Done) { await first.Step(); }
            if (!second.Done) { await second.Step(); }
        }
        Assert.True(first.Done && second.Done);
        first.AssertEffectiveDefinition();
        second.AssertEffectiveDefinition();
        Assert.NotEqual(first.Kernel.World.Time, second.Kernel.World.Time);
        Assert.Equal(14, first.Kernel.World.GetCharacter("a").Money);
        Assert.Equal(33, second.Kernel.World.GetCharacter("a").Money);
        Assert.Equal(normal.Fingerprint, first.Kernel.World.Definition.Fingerprint);
        Assert.Equal(variant.Fingerprint, second.Kernel.World.Definition.Fingerprint);
        Assert.NotSame(first.Kernel.World.Definition.Runtime, second.Kernel.World.Definition.Runtime);
    }

    [Fact]
    public async Task Variant_production_E_recovers_without_replanning_and_a_cold_fork_acts_without_changing_its_parent() {
        using var store = new TemporaryStore();
        PlaygroundWorldDefinition variant = Variant(PlaygroundGameFactory.CreateLivingWorld(characterCount: 1).World.Definition);
        variant = variant.Change(initialCharacters: variant.InitialCharacters.Select(actor => actor.Change(placeId: "workshop")));
        var initial = PlaygroundGameFactory.Create(variant);
        var baseline = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor),
            characterController: new QueueController("baseline", PlaygroundCommand.Produce("ration")));
        await Until(baseline, world => world.Business.Economy!.Totals.Produced.GetValueOrDefault("item.ration") > 0);
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var interrupted = PlaygroundEngine.CreateKernel(new StopAfterProduction(history),
                characterController: new QueueController("baseline", PlaygroundCommand.Produce("ration")));
            for (int step = 0; step < 80 && !interrupted.IsFaulted; step++) {
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.NotNull(history.PendingEvent);
            Assert.Empty(history.State.Business.Economy!.Totals.Produced);
            Assert.Equal(variant.Fingerprint, history.State.Definition.Fingerprint);
        }
        PlaygroundDecisionBoundary source;
        string parentHead, parentState;
        using (var history = PlaygroundDurableHistory.Open(store.StorePath, variant)) {
            var recovery = RecoveryWithoutPlanning(history);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(MaterialSnapshot(baseline.World.Business), MaterialSnapshot(recovery.World.Business));
            Assert.Equal(3, history.State.Business.Economy!.Totals.Produced["item.ration"]);
            Assert.Equal(3, history.State.Business.Economy.Totals.RecipeInputs["item.food"]);
            var opening = PlaygroundEngine.CreateKernel(history, characterController: new ForbiddenController());
            await Until(opening, world => world.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
            source = history.CaptureDecisionBoundary("a");
            var parent = PlaygroundEngine.CreateKernel(history, characterController: new QueueController("parent", PlaygroundCommand.Handoff("parent-only continuation")));
            await Advance(parent);
            parentHead = PlaygroundDurableHistory.FormatCommitId(history.HeadCommitId);
            parentState = MaterialSnapshot(history.State.Business);
        }

        // Creating an unrelated world does not select a process-global graph for the reopened branch.
        _ = PlaygroundGameFactory.CreateLivingWorld(characterCount: 3);
        PlaygroundBranchForkResult fork = PlaygroundDurableHistory.Fork(store.StorePath, source.BranchId, source.StateCommitId, "a", "content alternate");
        string childState;
        using (var child = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(fork.Branch.BranchId), variant)) {
            Assert.Equal(variant.Fingerprint, child.State.Definition.Fingerprint);
            Assert.Null(child.State.Business.InteractionStates["a"].DraftHandoff);
            var kernel = PlaygroundEngine.CreateKernel(child, characterController: new QueueController("child", PlaygroundCommand.Go("e-j3")));
            await Until(kernel, world => PlaygroundActions.CurrentPlace(world, "a") == "home");
            Assert.Equal(3, kernel.World.Business.Economy!.Totals.Produced["item.ration"]);
            childState = MaterialSnapshot(kernel.World.Business);
        }
        using (var child = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(fork.Branch.BranchId), variant)) {
            Assert.Equal(childState, MaterialSnapshot(child.State.Business));
            Assert.False(PlaygroundEngine.CreateKernel(child, characterController: new ForbiddenController()).RecoverPending());
        }
        using var parentAgain = PlaygroundDurableHistory.Open(store.StorePath, PlaygroundDurableHistory.ParseBranchId(source.BranchId), variant);
        Assert.Equal(parentHead, PlaygroundDurableHistory.FormatCommitId(parentAgain.HeadCommitId));
        Assert.Equal(parentState, MaterialSnapshot(parentAgain.State.Business));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_content_mismatch_rejects_S_and_pending_E_without_touching_the_save(bool pending) {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        PlaygroundWorldDefinition expected = initial.World.Definition;
        PlaygroundWorldDefinition different = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false).World.Definition;
        Assert.NotEqual(expected.Fingerprint, different.Fingerprint);
        string before;
        using (var history = PlaygroundDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            before = MaterialSnapshot(history.State.Business);
            if (pending) {
                var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(history.State, history.Cursor);
                var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new ForbiddenController());
                await Advance(kernel); // A real, independently published Context opening.
                history.CommitEvent(kernel.LastCompletion!.Event);
                Assert.NotNull(history.PendingEvent);
            }
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath, different));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
        using (var history = PlaygroundDurableHistory.Open(store.StorePath, expected)) {
            Assert.Equal(before, MaterialSnapshot(history.State.Business));
            Assert.Equal(expected.Fingerprint, history.State.Definition.Fingerprint);
            Assert.Equal(pending, history.PendingEvent is not null);
            var recovery = RecoveryWithoutPlanning(history);
            Assert.Equal(pending, recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(expected.Fingerprint, recovery.World.Definition.Fingerprint);
        }
    }

    [Fact]
    public async Task An_E_wrapped_with_another_valid_definition_is_rejected_before_any_recovery_or_publication() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        PlaygroundWorldDefinition other = PlaygroundGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false).World.Definition;
        var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new ForbiddenController());
        await Advance(kernel);
        OccurrenceEvent<PlaygroundFact> occurrence = kernel.LastCompletion!.Event;
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            BranchId branch = repository.CreateRootBranchFromState("mismatched-content", new PlaygroundDurableRoot(initial.World.Business, initial.Cursor)).Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            // Both definitions are valid. Only their association with this actual planning S is false.
            work.CommitEvent(new PlaygroundDurableEvent(occurrence, other));
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath, initial.World.Definition));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
    }

    [Fact]
    public void A_saved_definition_with_changed_material_and_stale_fingerprint_cannot_be_opened() {
        using var store = new TemporaryStore();
        var initial = PlaygroundGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        var root = new PlaygroundDurableRoot(initial.World.Business, initial.Cursor);
        // Write corruption below the application API to prove that cold hydration recomputes identity.
        typeof(PlaygroundWorldDefinition).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(initial.World.Definition, "tampered saved material");
        var models = new StateModelRegistry();
        PlaygroundDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("stale-fingerprint", root);
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => PlaygroundDurableHistory.Open(store.StorePath));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
    }

    private static PlaygroundWorldDefinition Variant(PlaygroundWorldDefinition basis) => basis.Change(
        contentId: "acceptance-variant", name: "Acceptance orchard",
        passages: basis.Passages.Select(passage => passage.Id == "market-home" ? passage with { DurationTicks = PlaygroundTime.Hour / 2 } : passage),
        resources: basis.Resources.Select(resource => resource.Id == "berries"
            ? resource with { HarvestTicks = PlaygroundTime.Hour / 4, RegenerationTicks = PlaygroundTime.Hour / 4 } : resource),
        initialCharacters: basis.InitialCharacters.Select(actor => actor.Change(money: 30,
            backpack: new Dictionary<string, int> { ["item.food"] = 4, ["item.water"] = 4 })),
        recipes: basis.Recipes.Select(recipe => recipe.Id == "ration"
            ? recipe with { InputCount = 3, OutputCount = 3, DurationTicks = PlaygroundTime.Hour / 2 } : recipe),
        buyPrices: new Dictionary<string, int>(basis.BuyPrices) { ["item.food"] = 4 },
        sellPrices: new Dictionary<string, int>(basis.SellPrices) { ["item.ration"] = 7 },
        foodSupplies: new Dictionary<string, int>(basis.FoodSupplies) { ["item.ration"] = 25 },
        body: basis.Body with { FoodPerHour = -3, WaterPerHour = -4 },
        weather: basis.Weather with { IntervalTicks = PlaygroundTime.Hour / 2, ExposureLoss = 1 });

    private static async Task Until(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel, Func<PlaygroundWorld, bool> reached) {
        for (int step = 0; step < 300; step++) {
            if (reached(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The content scenario did not reach its finite boundary.");
    }

    private static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
        kernel.World.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Equal(kernel.World.Definition.Fingerprint, rebuilt.Definition.Fingerprint);
    }

    private static SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> RecoveryWithoutPlanning(PlaygroundDurableHistory history) =>
        new(history, new SimulationRules(0, 1024), [new ForbiddenPlanning()], PlaygroundEngine.Fold,
            (before, at, facts, folded) => PlaygroundEngine.Complete(before, at, facts, folded, PredictionMode.Incremental), PlaygroundEngine.Validate);

    private static string[] DiskFingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

    private sealed class ForbiddenController : IPlaygroundCharacterController {
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This boundary must not request another character operation.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> {
        public IReadOnlyList<OccurrenceCandidate<PlaygroundPrediction>> Forecast(PlaygroundWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending recovery must use the saved definition without forecasting.");
        public ValueTask<TransitionDraft<PlaygroundFact>> PlanSelectedAsync(PlaygroundWorld world, OccurrenceCandidate<PlaygroundPrediction> winner,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Pending recovery must not re-plan with any definition.");
    }

    private sealed class QueueController(string session, params PlaygroundCommand[] commands) : IPlaygroundCharacterController {
        private readonly Queue<PlaygroundCommand> _commands = new(commands);
        private int _calls;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            PlaygroundCommand command = _commands.Dequeue();
            Assert.Null(context.Check(command));
            string id = $"{session}/{++_calls}";
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, PlaygroundCommandParser.Format(command), id));
        }
    }

    private sealed class Journey : IPlaygroundCharacterController {
        private readonly PlaygroundWorldDefinition _definition;
        private readonly string _session;
        private readonly Queue<PlaygroundCommand> _commands;
        private readonly List<(PlaygroundWorld Before, PlaygroundWorld After, PlaygroundFact Fact)> _trace = [];
        private int _calls;
        public SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> Kernel { get; }
        public bool Done => _commands.Count == 0 && PlaygroundActions.CurrentPlace(Kernel.World, "a") == "home";
        public Journey(PlaygroundWorldDefinition definition, string session) {
            _definition = definition; _session = session;
            PlaygroundCommand Go(string from, string to) => PlaygroundCommand.Go(definition.GetEntrances(from).Single(passage => passage.To == to).EntranceId);
            _commands = new([
                Go("home", "market"), Go("market", "road"), Go("road", "camp"), Go("camp", "stream"),
                PlaygroundCommand.Gather("berries"), PlaygroundCommand.Gather("water"), Go("stream", "camp"),
                Go("camp", "road"), Go("road", "market"), Go("market", "workshop"),
                PlaygroundCommand.Produce("ration"), Go("workshop", "market"), PlaygroundCommand.Sell("item.ration"),
                PlaygroundCommand.Buy("item.food"), PlaygroundCommand.Use("item.ration"), Go("market", "home")
            ]);
            var initial = PlaygroundGameFactory.Create(definition);
            Assert.Equal(definition.InitialCharacters[0].Money, initial.World.GetCharacter("a").Money);
            Assert.Equal(definition.InitialCharacters[0].Backpack["item.food"], initial.World.GetCharacter("a").Backpack.Count("item.food"));
            Kernel = PlaygroundEngine.CreateKernel(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor), characterController: this);
        }
        public async Task Step() {
            PlaygroundWorld before = Kernel.World;
            await Advance(Kernel);
            _trace.AddRange(Kernel.LastCompletion!.Event.Facts.Select(fact => (before, Kernel.World, fact)));
        }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal(_definition.Body.FoodPerHour, context.Observation.Survival!.FoodPerHour);
            Assert.Equal(_definition.Body.WaterPerHour, context.Observation.Survival.WaterPerHour);
            PlaygroundCommand command = context.Observation.Survival.Water <= 40 && context.Observation.Actor.Backpack.Count("item.water") > 0
                ? PlaygroundCommand.Use("item.water")
                : context.Observation.Actor.Activity is not null || context.Observation.ArrivalDue is not null
                    ? PlaygroundCommand.Continue() : _commands.Dequeue();
            Assert.Null(context.Check(command));
            string id = $"{_session}/{++_calls}";
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, PlaygroundCommandParser.Format(command), id));
        }
        public void AssertEffectiveDefinition() {
            Assert.True(Kernel.World.GetCharacter("a").IsActive);
            var firstGo = _trace.First(entry => entry.Fact.RawInput == "go e-m2");
            TraversingLocation moving = Assert.IsType<TraversingLocation>(PlaygroundActions.Location(firstGo.After, "a"));
            Assert.Equal(_definition.Passages.Single(passage => passage.Id == "market-home").DurationTicks,
                moving.ArrivalDue.Ticks - firstGo.Fact.At.Ticks);
            var gathered = Assert.Single(_trace, entry => entry.Fact.Kind == PlaygroundEventKind.GatherYielded && entry.Fact.ResourceId == "berries");
            Assert.Equal(_definition.Resources.Single(resource => resource.Id == "berries").HarvestTicks,
                gathered.Before.GetCharacter("a").Activity!.DurationTicks);
            var regeneration = _trace.First(entry => entry.Fact.Kind == PlaygroundEventKind.ResourceRegenerated && entry.Fact.ResourceId == "berries");
            Assert.Equal(_definition.Resources.Single(resource => resource.Id == "berries").RegenerationTicks,
                regeneration.Fact.At.Ticks - gathered.Fact.At.Ticks);
            PlaygroundRecipe recipe = _definition.Recipe("ration")!;
            var started = Assert.Single(_trace, entry => entry.Fact.Kind == PlaygroundEventKind.ProductionStarted);
            Assert.Equal(recipe.DurationTicks, started.After.GetCharacter("a").Activity!.DurationTicks);
            PlaygroundEconomicTotals totals = Kernel.World.Business.Economy!.Totals;
            Assert.Equal(recipe.InputCount, totals.RecipeInputs[recipe.InputItem]);
            Assert.Equal(recipe.OutputCount, totals.Produced[recipe.OutputItem]);
            Assert.Equal(_definition.SellPrice("item.ration"), totals.ExportIncome);
            Assert.Equal(_definition.BuyPrice("item.food"), totals.ImportExpense);
            Assert.Equal(1, totals.Harvested["item.food"]);
            Assert.Equal(1, totals.Harvested["item.water"]);
            var consumed = Assert.Single(_trace, entry => entry.Fact.RawInput == "use item.ration");
            long foodBefore = consumed.Before.GetCharacter("a").Survival!.Project(consumed.Fact.At).FoodUnits;
            Assert.Equal(Math.Min(PlaygroundSurvival.MaximumUnits, foodBefore + _definition.SupplyFood("item.ration") * PlaygroundSurvival.Scale),
                consumed.After.GetCharacter("a").Survival!.FoodUnits);
            var weather = _trace.Where(entry => entry.Fact.Kind == PlaygroundEventKind.WeatherResolved).ToArray();
            Assert.NotEmpty(weather);
            Assert.Equal(_definition.StartTime.Ticks + _definition.Weather.IntervalTicks, weather[0].Fact.At.Ticks);
            Assert.All(weather, entry => Assert.Equal(_definition.Weather.IntervalTicks,
                entry.After.Expedition!.Environment!.NextDue!.Value.Ticks - entry.Fact.At.Ticks));
            totals.Verify(Kernel.World.Business);
        }
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterProduction(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner) : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == PlaygroundEventKind.ProductionCompleted)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
