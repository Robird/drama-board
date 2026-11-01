using System.Security.Cryptography;
using System.Reflection;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

/// <summary>Content identity is exercised through real world execution and durable E/S boundaries.</summary>
public sealed class HillsideContentAcceptanceTests {
    [Fact]
    public async Task Interleaved_default_and_variant_journeys_use_their_own_graph_resources_recipes_prices_and_physiology() {
        CycleWorldDefinition normal = CycleGameFactory.CreateLivingWorld(characterCount: 1).World.Definition;
        CycleWorldDefinition variant = Variant(normal);
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
        CycleWorldDefinition variant = Variant(CycleGameFactory.CreateLivingWorld(characterCount: 1).World.Definition);
        variant = variant.Change(initialCharacters: variant.InitialCharacters.Select(actor => actor.Change(placeId: "workshop")));
        var initial = CycleGameFactory.Create(variant);
        var baseline = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor),
            characterController: new QueueController("baseline", CycleCommand.Produce("ration")));
        await Until(baseline, world => world.Business.Economy!.Totals.Produced.GetValueOrDefault("item.ration") > 0);
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            var interrupted = CycleEngine.CreateKernel(new StopAfterProduction(history),
                characterController: new QueueController("baseline", CycleCommand.Produce("ration")));
            for (int step = 0; step < 80 && !interrupted.IsFaulted; step++) {
                try { await Advance(interrupted); }
                catch (InvalidOperationException error) when (error.InnerException is InjectedCrash) { }
            }
            Assert.True(interrupted.IsFaulted);
            Assert.NotNull(history.PendingEvent);
            Assert.Empty(history.State.Business.Economy!.Totals.Produced);
            Assert.Equal(variant.Fingerprint, history.State.Definition.Fingerprint);
        }
        CycleDecisionBoundary source;
        string parentHead, parentState;
        using (var history = CycleDurableHistory.Open(store.StorePath, variant)) {
            var recovery = RecoveryWithoutPlanning(history);
            Assert.True(recovery.RecoverPending());
            Assert.False(recovery.RecoverPending());
            Assert.Equal(MaterialSnapshot(baseline.World.Business), MaterialSnapshot(recovery.World.Business));
            Assert.Equal(3, history.State.Business.Economy!.Totals.Produced["item.ration"]);
            Assert.Equal(3, history.State.Business.Economy.Totals.RecipeInputs["item.food"]);
            var opening = CycleEngine.CreateKernel(history, characterController: new ForbiddenController());
            await Until(opening, world => world.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open);
            source = history.CaptureDecisionBoundary("a");
            var parent = CycleEngine.CreateKernel(history, characterController: new QueueController("parent", CycleCommand.Handoff("parent-only continuation")));
            await Advance(parent);
            parentHead = CycleDurableHistory.FormatCommitId(history.HeadCommitId);
            parentState = MaterialSnapshot(history.State.Business);
        }

        // Creating an unrelated world does not select a process-global graph for the reopened branch.
        _ = CycleGameFactory.CreateLivingWorld(characterCount: 3);
        CycleBranchForkResult fork = CycleDurableHistory.Fork(store.StorePath, source.BranchId, source.StateCommitId, "a", "content alternate");
        string childState;
        using (var child = CycleDurableHistory.Open(store.StorePath, CycleDurableHistory.ParseBranchId(fork.Branch.BranchId), variant)) {
            Assert.Equal(variant.Fingerprint, child.State.Definition.Fingerprint);
            Assert.Null(child.State.Business.InteractionStates["a"].DraftHandoff);
            var kernel = CycleEngine.CreateKernel(child, characterController: new QueueController("child", CycleCommand.Go("home-workshop")));
            await Until(kernel, world => CycleActions.CurrentPlace(world, "a") == "home");
            Assert.Equal(3, kernel.World.Business.Economy!.Totals.Produced["item.ration"]);
            childState = MaterialSnapshot(kernel.World.Business);
        }
        using (var child = CycleDurableHistory.Open(store.StorePath, CycleDurableHistory.ParseBranchId(fork.Branch.BranchId), variant)) {
            Assert.Equal(childState, MaterialSnapshot(child.State.Business));
            Assert.False(CycleEngine.CreateKernel(child, characterController: new ForbiddenController()).RecoverPending());
        }
        using var parentAgain = CycleDurableHistory.Open(store.StorePath, CycleDurableHistory.ParseBranchId(source.BranchId), variant);
        Assert.Equal(parentHead, CycleDurableHistory.FormatCommitId(parentAgain.HeadCommitId));
        Assert.Equal(parentState, MaterialSnapshot(parentAgain.State.Business));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_content_mismatch_rejects_S_and_pending_E_without_touching_the_save(bool pending) {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        CycleWorldDefinition expected = initial.World.Definition;
        CycleWorldDefinition different = CycleGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false).World.Definition;
        Assert.NotEqual(expected.Fingerprint, different.Fingerprint);
        string before;
        using (var history = CycleDurableHistory.Create(store.StorePath, initial.World, initial.Cursor)) {
            before = MaterialSnapshot(history.State.Business);
            if (pending) {
                var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(history.State, history.Cursor);
                var kernel = CycleEngine.CreateKernel(memory, characterController: new ForbiddenController());
                await Advance(kernel); // A real, independently published Context opening.
                history.CommitEvent(kernel.LastCompletion!.Event);
                Assert.NotNull(history.PendingEvent);
            }
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath, different));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
        using (var history = CycleDurableHistory.Open(store.StorePath, expected)) {
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
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        CycleWorldDefinition other = CycleGameFactory.CreateLivingWorld(characterCount: 2, enableNeeds: false).World.Definition;
        var memory = new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor);
        var kernel = CycleEngine.CreateKernel(memory, characterController: new ForbiddenController());
        await Advance(kernel);
        OccurrenceEvent<CycleFact> occurrence = kernel.LastCompletion!.Event;
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            BranchId branch = repository.CreateRootBranchFromState("mismatched-content", new CycleDurableRoot(initial.World.Business, initial.Cursor)).Id;
            using WorkingCopy work = repository.OpenWorkingCopy(branch);
            // Both definitions are valid. Only their association with this actual planning S is false.
            work.CommitEvent(new CycleDurableEvent(occurrence, other));
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath, initial.World.Definition));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
    }

    [Fact]
    public void A_saved_definition_with_changed_material_and_stale_fingerprint_cannot_be_opened() {
        using var store = new TemporaryStore();
        var initial = CycleGameFactory.CreateLivingWorld(characterCount: 1, enableNeeds: false);
        var root = new CycleDurableRoot(initial.World.Business, initial.Cursor);
        // Write corruption below the application API to prove that cold hydration recomputes identity.
        typeof(CycleWorldDefinition).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(initial.World.Definition, "tampered saved material");
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            repository.CreateRootBranchFromState("stale-fingerprint", root);
        }
        string[] bytes = DiskFingerprint(store.StorePath);
        Assert.Throws<InvalidDataException>(() => CycleDurableHistory.Open(store.StorePath));
        Assert.Equal(bytes, DiskFingerprint(store.StorePath));
    }

    private static CycleWorldDefinition Variant(CycleWorldDefinition basis) => basis.Change(
        contentId: "acceptance-variant", name: "Acceptance orchard",
        passages: basis.Passages.Select(passage => passage.Id == "market-home" ? passage with { DurationTicks = CycleTime.Hour / 2 } : passage),
        resources: basis.Resources.Select(resource => resource.Id == "berries"
            ? resource with { HarvestTicks = CycleTime.Hour / 4, RegenerationTicks = CycleTime.Hour / 4 } : resource),
        initialCharacters: basis.InitialCharacters.Select(actor => actor.Change(money: 30,
            backpack: new Dictionary<string, int> { ["item.food"] = 4, ["item.water"] = 4 })),
        recipes: basis.Recipes.Select(recipe => recipe.Id == "ration"
            ? recipe with { InputCount = 3, OutputCount = 3, DurationTicks = CycleTime.Hour / 2 } : recipe),
        buyPrices: new Dictionary<string, int>(basis.BuyPrices) { ["item.food"] = 4 },
        sellPrices: new Dictionary<string, int>(basis.SellPrices) { ["item.ration"] = 7 },
        foodSupplies: new Dictionary<string, int>(basis.FoodSupplies) { ["item.ration"] = 25 },
        body: basis.Body with { FoodPerHour = -3, WaterPerHour = -4 },
        weather: basis.Weather with { IntervalTicks = CycleTime.Hour / 2, ExposureLoss = 1 });

    private static async Task Until(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel, Func<CycleWorld, bool> reached) {
        for (int step = 0; step < 300; step++) {
            if (reached(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The content scenario did not reach its finite boundary.");
    }

    private static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(72)));
        kernel.World.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(kernel.World.Business, kernel.World.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), kernel.World.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, kernel.World.Spatial!.Contacts);
        Assert.Equal(kernel.World.Definition.Fingerprint, rebuilt.Definition.Fingerprint);
    }

    private static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> RecoveryWithoutPlanning(CycleDurableHistory history) =>
        new(history, new SimulationRules(0, 1024), [new ForbiddenPlanning()], CycleEngine.Fold,
            (before, at, facts, folded) => CycleEngine.Complete(before, at, facts, folded, PredictionMode.Incremental), CycleEngine.Validate);

    private static string[] DiskFingerprint(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();

    private sealed class ForbiddenController : ICycleCharacterController {
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This boundary must not request another character operation.");
    }

    private sealed class ForbiddenPlanning : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) =>
            throw new InvalidOperationException("Pending recovery must use the saved definition without forecasting.");
        public ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world, OccurrenceCandidate<CyclePrediction> winner,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Pending recovery must not re-plan with any definition.");
    }

    private sealed class QueueController(string session, params CycleCommand[] commands) : ICycleCharacterController {
        private readonly Queue<CycleCommand> _commands = new(commands);
        private int _calls;
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            CycleCommand command = _commands.Dequeue();
            Assert.Null(context.Check(command));
            string id = $"{session}/{++_calls}";
            return ValueTask.FromResult(new CycleControllerOperation(id, CycleCommandParser.Format(command), id));
        }
    }

    private sealed class Journey : ICycleCharacterController {
        private readonly CycleWorldDefinition _definition;
        private readonly string _session;
        private readonly Queue<CycleCommand> _commands;
        private readonly List<(CycleWorld Before, CycleWorld After, CycleFact Fact)> _trace = [];
        private int _calls;
        public SimulationKernel<CycleWorld, CyclePrediction, CycleFact> Kernel { get; }
        public bool Done => _commands.Count == 0 && CycleActions.CurrentPlace(Kernel.World, "a") == "home";
        public Journey(CycleWorldDefinition definition, string session) {
            _definition = definition; _session = session;
            CycleCommand Go(string from, string to) => CycleCommand.Go(definition.GetPassages(from).Single(passage => passage.To == to).Id);
            _commands = new([
                Go("home", "market"), Go("market", "road"), Go("road", "camp"), Go("camp", "stream"),
                CycleCommand.Gather("berries"), CycleCommand.Gather("water"), Go("stream", "camp"),
                Go("camp", "road"), Go("road", "market"), Go("market", "workshop"),
                CycleCommand.Produce("ration"), Go("workshop", "market"), CycleCommand.Sell("item.ration"),
                CycleCommand.Buy("item.food"), CycleCommand.Use("item.ration"), Go("market", "home")
            ]);
            var initial = CycleGameFactory.Create(definition);
            Assert.Equal(definition.InitialCharacters[0].Money, initial.World.GetCharacter("a").Money);
            Assert.Equal(definition.InitialCharacters[0].Backpack["item.food"], initial.World.GetCharacter("a").Backpack.Count("item.food"));
            Kernel = CycleEngine.CreateKernel(new InMemoryOccurrenceHistory<CycleWorld, CycleFact>(initial.World, initial.Cursor), characterController: this);
        }
        public async Task Step() {
            CycleWorld before = Kernel.World;
            await Advance(Kernel);
            _trace.AddRange(Kernel.LastCompletion!.Event.Facts.Select(fact => (before, Kernel.World, fact)));
        }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Assert.Equal(_definition.Body.FoodPerHour, context.Observation.Survival!.FoodPerHour);
            Assert.Equal(_definition.Body.WaterPerHour, context.Observation.Survival.WaterPerHour);
            CycleCommand command = context.Observation.Survival.Water <= 40 && context.Observation.Actor.Backpack.Count("item.water") > 0
                ? CycleCommand.Use("item.water")
                : context.Observation.Actor.Activity is not null || context.Observation.ArrivalDue is not null
                    ? CycleCommand.Continue() : _commands.Dequeue();
            Assert.Null(context.Check(command));
            string id = $"{_session}/{++_calls}";
            return ValueTask.FromResult(new CycleControllerOperation(id, CycleCommandParser.Format(command), id));
        }
        public void AssertEffectiveDefinition() {
            Assert.True(Kernel.World.GetCharacter("a").IsActive);
            var firstGo = _trace.First(entry => entry.Fact.RawInput == "go market-home");
            TraversingLocation moving = Assert.IsType<TraversingLocation>(CycleActions.Location(firstGo.After, "a"));
            Assert.Equal(_definition.Passages.Single(passage => passage.Id == "market-home").DurationTicks,
                moving.ArrivalDue.Ticks - firstGo.Fact.At.Ticks);
            var gathered = Assert.Single(_trace, entry => entry.Fact.Kind == CycleEventKind.GatherYielded && entry.Fact.ResourceId == "berries");
            Assert.Equal(_definition.Resources.Single(resource => resource.Id == "berries").HarvestTicks,
                gathered.Before.GetCharacter("a").Activity!.DurationTicks);
            var regeneration = _trace.First(entry => entry.Fact.Kind == CycleEventKind.ResourceRegenerated && entry.Fact.ResourceId == "berries");
            Assert.Equal(_definition.Resources.Single(resource => resource.Id == "berries").RegenerationTicks,
                regeneration.Fact.At.Ticks - gathered.Fact.At.Ticks);
            CycleRecipe recipe = _definition.Recipe("ration")!;
            var started = Assert.Single(_trace, entry => entry.Fact.Kind == CycleEventKind.ProductionStarted);
            Assert.Equal(recipe.DurationTicks, started.After.GetCharacter("a").Activity!.DurationTicks);
            CycleEconomicTotals totals = Kernel.World.Business.Economy!.Totals;
            Assert.Equal(recipe.InputCount, totals.RecipeInputs[recipe.InputItem]);
            Assert.Equal(recipe.OutputCount, totals.Produced[recipe.OutputItem]);
            Assert.Equal(_definition.SellPrice("item.ration"), totals.ExportIncome);
            Assert.Equal(_definition.BuyPrice("item.food"), totals.ImportExpense);
            Assert.Equal(1, totals.Harvested["item.food"]);
            Assert.Equal(1, totals.Harvested["item.water"]);
            var consumed = Assert.Single(_trace, entry => entry.Fact.RawInput == "use item.ration");
            long foodBefore = consumed.Before.GetCharacter("a").Survival!.Project(consumed.Fact.At).FoodUnits;
            Assert.Equal(Math.Min(CycleSurvival.MaximumUnits, foodBefore + _definition.SupplyFood("item.ration") * CycleSurvival.Scale),
                consumed.After.GetCharacter("a").Survival!.FoodUnits);
            var weather = _trace.Where(entry => entry.Fact.Kind == CycleEventKind.WeatherResolved).ToArray();
            Assert.NotEmpty(weather);
            Assert.Equal(_definition.StartTime.Ticks + _definition.Weather.IntervalTicks, weather[0].Fact.At.Ticks);
            Assert.All(weather, entry => Assert.Equal(_definition.Weather.IntervalTicks,
                entry.After.Expedition!.Environment!.NextDue!.Value.Ticks - entry.Fact.At.Ticks));
            totals.Verify(Kernel.World.Business);
        }
    }

    private sealed class InjectedCrash : IOException { }
    private sealed class StopAfterProduction(IOccurrenceHistory<CycleWorld, CycleFact> inner) : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(fact => fact.Kind == CycleEventKind.ProductionCompleted)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
