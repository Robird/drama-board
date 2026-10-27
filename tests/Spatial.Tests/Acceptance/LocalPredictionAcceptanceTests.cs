using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class LocalPredictionAcceptanceTests {
    private static readonly PassageId Other = new("other");
    private static readonly SimulationRules Rules = new(0, 100);

    [Fact]
    public void ReverseDiscoversPreviouslyAbsentPairAndLeavesOtherPassageUntouched() {
        var definition = Definition();
        var calls = new List<PassageId>();
        var simulation = new SpatialSimulation(definition, calls.Add);
        var planner = new SpatialPlanner(definition);
        var root = simulation.Rebuild(GraphTestWorld.State(definition,
            ("a", GraphTestWorld.B), ("b", GraphTestWorld.A), ("c", GraphTestWorld.A),
            ("x", GraphTestWorld.C), ("y", GraphTestWorld.D)), ModelTime.Zero);
        root = Apply(simulation, root, 0,
            Start(planner, root.Business, "a", GraphTestWorld.Bridge, 1, 0),
            Start(planner, root.Business, "b", GraphTestWorld.Bridge, 3, 0),
            Start(planner, root.Business, "c", GraphTestWorld.Bridge, 1, 0),
            Start(planner, root.Business, "x", Other, 1, 0),
            Start(planner, root.Business, "y", Other, 1, 0));
        ContactOracle.AssertMatches(definition, root);
        Assert.Contains(root.Contacts.Entries, p => Pair(p, "a", "b"));
        Assert.DoesNotContain(root.Contacts.Entries, p => Pair(p, "b", "c"));
        var old = root;
        var unrelated = root.Contacts.Entries.Where(p => p.ContactKey.PassageId == Other).ToArray();
        calls.Clear();
        root = Apply(simulation, root, 2,
            GraphTestWorld.Accepted(planner.TryReverseTraversal(root.Business, new EntityId("b"), GraphTestWorld.Time(2))).Facts.Single());
        Assert.Equal([GraphTestWorld.Bridge], calls);
        Assert.DoesNotContain(root.Contacts.Entries, p => Pair(p, "a", "b"));
        Assert.Contains(root.Contacts.Entries, p => Pair(p, "b", "c") && p.Due == GraphTestWorld.Time(3));
        Assert.Equal(unrelated, root.Contacts.Entries.Where(p => p.ContactKey.PassageId == Other));
        Assert.Contains(old.Contacts.Entries, p => Pair(p, "a", "b")); // Published input remains immutable.
        ContactOracle.AssertMatches(definition, root);
    }

    [Fact]
    public void EmptyPassageDiscoversBatchNewcomersThenConsumptionAndRemovalRetireOutputs() {
        var definition = Definition();
        var calls = new List<PassageId>();
        var simulation = new SpatialSimulation(definition, calls.Add);
        var planner = new SpatialPlanner(definition);
        var root = simulation.Rebuild(GraphTestWorld.State(definition, ("a", GraphTestWorld.A), ("b", GraphTestWorld.B)), ModelTime.Zero);
        Assert.Empty(root.Contacts.Entries);
        calls.Clear();
        root = Apply(simulation, root, 0,
            Start(planner, root.Business, "a", GraphTestWorld.Bridge, 3, 0),
            Start(planner, root.Business, "b", GraphTestWorld.Bridge, 2, 0));
        Assert.Equal([GraphTestWorld.Bridge], calls); // Two business facts, one final discovery.
        ContactOracle.AssertMatches(definition, root);
        var contact = Assert.Single(root.Contacts.Entries);
        root = Apply(simulation, root, contact.Due.Ticks, new PassageContactOccurredFact(contact.ContactKey, contact.Kind));
        Assert.Empty(root.Contacts.Entries);
        Assert.Single(root.Business.ConsumedContacts);
        ContactOracle.AssertMatches(definition, root);
        root = Apply(simulation, root, contact.Due.Ticks, new EntityRemovedFact(new EntityId("a")));
        Assert.Empty(root.Contacts.Entries);
        Assert.Empty(root.Business.ConsumedContacts);
        ContactOracle.AssertMatches(definition, root);
    }

    [Fact]
    public void NonAdjacentSameTickWinnerIsRetainedAndMissingOutputIsCaughtByOracle() {
        var definition = Definition();
        var simulation = new SpatialSimulation(definition);
        var planner = new SpatialPlanner(definition);
        var root = simulation.Rebuild(GraphTestWorld.State(definition,
            ("a", GraphTestWorld.A), ("b", GraphTestWorld.A), ("c", GraphTestWorld.A)), ModelTime.Zero);
        root = Apply(simulation, root, 0,
            Start(planner, root.Business, "b", GraphTestWorld.Bridge, 2, 0),
            Start(planner, root.Business, "c", GraphTestWorld.Bridge, 3, 0));
        root = Apply(simulation, root, 1, Start(planner, root.Business, "a", GraphTestWorld.Bridge, 10, 1));
        ContactOracle.AssertMatches(definition, root);
        var candidates = new SpatialContactOccurrenceRule(definition).Forecast(root, Rules);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(GraphTestWorld.Time(1), c.Due.ModelTime));
        // Verify the seeded scheduler's winner against independently encoded candidates.
        var expected = ContactOracle.Enumerate(definition, root.Business).Select(p => {
            var k = p.ContactKey;
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object[] {
                "graph-spatial/contact", k.PassageId.Value, k.EntityA.Value, k.MovementGenerationA, k.EntityB.Value, k.MovementGenerationB });
            return new OccurrenceCandidate<SpatialOccurrenceData>(CandidateKey.FromBytes(bytes), new CandidateDue(p.Due), new PassageContactOccurrenceData(k, p.Kind));
        }).ToArray();
        ulong seed = Enumerable.Range(0, 1000).Select(i => (ulong)i).First(value =>
            ((PassageContactOccurrenceData)OccurrenceScheduler.SelectWinner(expected, value).Data).ContactKey.EntityB == new EntityId("c"));
        var winner = OccurrenceScheduler.SelectWinner(candidates, seed);
        Assert.Equal(OccurrenceScheduler.SelectWinner(expected, seed).Key, winner.Key);
        Assert.Equal(new EntityId("c"), ((PassageContactOccurrenceData)winner.Data).ContactKey.EntityB);
        var incomplete = new SpatialSimulationState(root.Business, new SpatialContactPredictions([]));
        simulation.Validate(incomplete, GraphTestWorld.Time(1)); // Structure alone cannot prove coverage.
        Assert.ThrowsAny<Exception>(() => ContactOracle.AssertMatches(definition, incomplete));
        var selected = (PassageContactOccurrenceData)winner.Data;
        root = Apply(simulation, root, 1, new PassageContactOccurredFact(selected.ContactKey, selected.Kind));
        var rebuilt = simulation.Rebuild(root.Business, GraphTestWorld.Time(1));
        Assert.Equal(root, rebuilt);
        var remaining = Assert.Single(rebuilt.Contacts.Entries);
        Assert.NotEqual(selected.ContactKey, remaining.ContactKey);
        Assert.Equal(GraphTestWorld.Time(1), remaining.Due); // Unconsumed due-now output survives rebuilding.
        root = Apply(simulation, root, 1, new PassageContactOccurredFact(remaining.ContactKey, remaining.Kind));
        Assert.Empty(simulation.Rebuild(root.Business, GraphTestWorld.Time(1)).Contacts.Entries);
        Assert.Equal(2, root.Business.ConsumedContacts.Count);
    }

    [Fact]
    public async Task SeededLifecycleTraceMatchesFullOracleAtEveryCommittedBoundary() {
        var definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 12),
             GraphTestWorld.Passage(Other, GraphTestWorld.C, GraphTestWorld.D, length: 12)]);
        var simulation = new SpatialSimulation(definition);
        var planner = new SpatialPlanner(definition);
        var random = new Random(1771);
        var genesis = simulation.Rebuild(GraphTestWorld.State(definition,
            ("a", GraphTestWorld.A), ("b", GraphTestWorld.B), ("c", GraphTestWorld.A),
            ("d", GraphTestWorld.C), ("e", GraphTestWorld.D), ("f", GraphTestWorld.C)), ModelTime.Zero);
        var history = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(genesis,
            new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        long largestGeneration = 0;
        async Task Drain(SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact> kernel, long time) {
            while (await kernel.StepAsync(GraphTestWorld.Time(time)) == StepStatus.Committed) {
                ContactOracle.AssertMatches(definition, kernel.World);
                Assert.Equal(simulation.Rebuild(kernel.World.Business, kernel.CurrentModelTime), kernel.World);
                largestGeneration = Math.Max(largestGeneration, kernel.World.Business.Entities.Max(e => e.MovementGeneration));
            }
        }
        // Each scripted change is itself an occurrence; drain earlier autonomous events first.
        for (int tick = 0; tick < 36; tick++) {
            await Drain(simulation.CreateKernel(Rules, history), tick);
            var before = history.State;
            var entity = before.Business.Entities[tick % 6];
            var facts = new List<GraphSpatialFact>();
            if (tick > 0 && tick % 5 == 0) {
                facts.Add(new EntityRemovedFact(entity.Id));
                facts.Add(new EntityPlacedFact(entity.Id, tick % 2 == 0 ? GraphTestWorld.A : GraphTestWorld.C));
            } else if (entity.Location is AtPlaceLocation room) {
                var passage = room.PlaceId == GraphTestWorld.A || room.PlaceId == GraphTestWorld.B ? GraphTestWorld.Bridge : Other;
                facts.AddRange(GraphTestWorld.Accepted(planner.TryStartTraversal(before.Business, entity.Id, passage, random.Next(1, 4), GraphTestWorld.Time(tick))).Facts);
            } else {
                var proposal = planner.TryReverseTraversal(before.Business, entity.Id, GraphTestWorld.Time(tick));
                if (proposal is SpatialPlanAccepted accepted) { facts.AddRange(accepted.Facts); }
                else { continue; }
            }
            var action = new ScriptedChange(before, tick, facts);
            await Drain(simulation.CreateKernel(Rules, history, [action]), tick);
            Assert.NotSame(before, history.State);
        }
        await Drain(simulation.CreateKernel(Rules, history), 100);
        var businessFacts = history.CompletedEvents.SelectMany(e => e.Facts).ToArray();
        Assert.True(businessFacts.OfType<TraversalArrivedFact>().Count() >= 2);
        Assert.True(businessFacts.OfType<TraversalReversedFact>().Count() >= 2);
        Assert.True(businessFacts.OfType<EntityRemovedFact>().Count() >= 2);
        Assert.True(largestGeneration >= 3);
    }

    [Fact]
    public void CandidateReadsNeverRecomputePassages() {
        var definition = Definition();
        var calls = new List<PassageId>();
        var simulation = new SpatialSimulation(definition, calls.Add);
        var planner = new SpatialPlanner(definition);
        var root = simulation.Rebuild(GraphTestWorld.State(definition, ("a", GraphTestWorld.A), ("b", GraphTestWorld.B)), ModelTime.Zero);
        root = Apply(simulation, root, 0,
            Start(planner, root.Business, "a", GraphTestWorld.Bridge, 1, 0),
            Start(planner, root.Business, "b", GraphTestWorld.Bridge, 1, 0));
        calls.Clear();
        var contactRule = new SpatialContactOccurrenceRule(definition);
        var first = contactRule.Forecast(root, Rules);
        var second = contactRule.Forecast(root, Rules);
        Assert.Single(first);
        Assert.Equal(first.Select(c => c.Key), second.Select(c => c.Key));
        Assert.Equal(2, new SpatialOccurrenceRule(definition).Forecast(root, Rules).Count);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationFailureLeavesPublishedWorldAndCacheUntouched(bool failValidation) {
        var definition = Definition();
        bool failPrediction = false;
        var simulation = new SpatialSimulation(definition, _ => {
            if (failPrediction) { throw new InvalidOperationException("prediction probe"); }
        });
        var root = simulation.Rebuild(GraphTestWorld.State(definition, ("a", GraphTestWorld.A), ("b", GraphTestWorld.B)), ModelTime.Zero);
        var planner = new SpatialPlanner(definition);
        GraphSpatialFact[] facts = [
            Start(planner, root.Business, "a", GraphTestWorld.Bridge, 1, 0),
            Start(planner, root.Business, "b", GraphTestWorld.Bridge, 1, 0)];
        var history = new InMemoryOccurrenceHistory<SpatialSimulationState, GraphSpatialFact>(root,
            new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        var kernel = new SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact>(
            history, Rules, [new ScriptedChange(root, 0, facts)], simulation.Fold, simulation.Complete,
            (world, time) => {
                simulation.Validate(world, time);
                if (failValidation && !ReferenceEquals(world, root)) { throw new InvalidOperationException("validation probe"); }
            });
        failPrediction = !failValidation;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await kernel.StepAsync(ModelTime.Zero));
        Assert.Same(root, kernel.World);
        Assert.Same(root, history.State);
        Assert.Empty(root.Contacts.Entries);
        Assert.All(root.Business.Entities, e => Assert.IsType<AtPlaceLocation>(e.Location));
        Assert.Empty(history.CompletedEvents);
        Assert.Null(history.PendingEvent);
    }

    private sealed class ScriptedChange(SpatialSimulationState before, long tick, IReadOnlyList<GraphSpatialFact> facts)
        : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, GraphSpatialFact> {
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world, SimulationRules rules) =>
            ReferenceEquals(before, world)
                ? [new(new CandidateKey($"test/script/{tick}"), new CandidateDue(GraphTestWorld.Time(tick)), new ScriptData())]
                : [];
        public ValueTask<TransitionDraft<GraphSpatialFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken token) =>
            ValueTask.FromResult(new TransitionDraft<GraphSpatialFact>(facts));
    }
    private sealed record ScriptData : SpatialOccurrenceData;

    private static bool Pair(ContactPrediction p, string a, string b) => p.ContactKey.EntityA == new EntityId(a) && p.ContactKey.EntityB == new EntityId(b);
    private static GraphDefinition Definition() => GraphDefinition.Create(
        [GraphTestWorld.A, GraphTestWorld.B, GraphTestWorld.C, GraphTestWorld.D],
        [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 100),
         GraphTestWorld.Passage(Other, GraphTestWorld.C, GraphTestWorld.D, length: 100)]);
    private static GraphSpatialFact Start(SpatialPlanner planner, GraphSpatialState world, string entity, PassageId passage, long speed, long time) =>
        GraphTestWorld.Accepted(planner.TryStartTraversal(world, new EntityId(entity), passage, speed, GraphTestWorld.Time(time))).Facts.Single();
    private static SpatialSimulationState Apply(SpatialSimulation simulation, SpatialSimulationState root, long time, params GraphSpatialFact[] facts) {
        var instant = GraphTestWorld.Instant(time);
        var before = root;
        foreach (var fact in facts) { root = simulation.Fold(root, instant, fact); }
        var foldedBusiness = root.Business;
        root = simulation.Complete(before, instant, facts, root);
        Assert.Same(foldedBusiness, root.Business);
        simulation.Validate(root, instant.ModelTime);
        return root;
    }
}
