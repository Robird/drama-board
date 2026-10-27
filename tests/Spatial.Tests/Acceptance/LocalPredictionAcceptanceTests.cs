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
        var root = simulation.CreateGenesis(GraphTestWorld.State(definition,
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
        var root = simulation.CreateGenesis(GraphTestWorld.State(definition, ("a", GraphTestWorld.A), ("b", GraphTestWorld.B)), ModelTime.Zero);
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
        var root = simulation.CreateGenesis(GraphTestWorld.State(definition,
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
        var genesis = simulation.CreateGenesis(GraphTestWorld.State(definition,
            ("a", GraphTestWorld.A), ("b", GraphTestWorld.B), ("c", GraphTestWorld.A),
            ("d", GraphTestWorld.C), ("e", GraphTestWorld.D), ("f", GraphTestWorld.C)), ModelTime.Zero);
        var history = new InMemoryOccurrenceHistory<SpatialSimulationState, SpatialSimulationFact>(genesis,
            new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        long largestGeneration = 0;
        async Task Drain(SimulationKernel<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> kernel, long time) {
            while (await kernel.StepAsync(GraphTestWorld.Time(time)) == StepStatus.Committed) {
                ContactOracle.AssertMatches(definition, kernel.World);
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
        var businessFacts = history.CompletedEvents.SelectMany(e => e.Facts).OfType<SpatialBusinessFact>().Select(f => f.Fact).ToArray();
        Assert.True(businessFacts.OfType<TraversalArrivedFact>().Count() >= 2);
        Assert.True(businessFacts.OfType<TraversalReversedFact>().Count() >= 2);
        Assert.True(businessFacts.OfType<EntityRemovedFact>().Count() >= 2);
        Assert.True(largestGeneration >= 3);
    }

    [Fact]
    public void CompleteRejectsMaintenanceFactsAndAlreadyCompletedDrafts() {
        var definition = Definition();
        var simulation = new SpatialSimulation(definition);
        var root = simulation.CreateGenesis(GraphTestWorld.State(definition, ("a", GraphTestWorld.A), ("b", GraphTestWorld.B)), ModelTime.Zero);
        var instant = GraphTestWorld.Instant(0);
        var injected = new TransitionDraft<SpatialSimulationFact>([new ContactPredictionsReplaced([GraphTestWorld.Bridge], [])]);
        Assert.Throws<InvalidOperationException>(() => simulation.Complete(root, instant, injected));
        var planner = new SpatialPlanner(definition);
        var business = new TransitionDraft<SpatialSimulationFact>([
            new SpatialBusinessFact(Start(planner, root.Business, "a", GraphTestWorld.Bridge, 1, 0)),
            new SpatialBusinessFact(Start(planner, root.Business, "b", GraphTestWorld.Bridge, 1, 0))]);
        var completed = simulation.Complete(root, instant, business);
        Assert.Single(completed.Facts.OfType<ContactPredictionsReplaced>());
        Assert.Throws<InvalidOperationException>(() => simulation.Complete(root, instant, completed));
    }

    private sealed class ScriptedChange(SpatialSimulationState before, long tick, IReadOnlyList<GraphSpatialFact> facts)
        : IOccurrenceRule<SpatialSimulationState, SpatialOccurrenceData, SpatialSimulationFact> {
        public IReadOnlyList<OccurrenceCandidate<SpatialOccurrenceData>> Forecast(SpatialSimulationState world, SimulationRules rules) =>
            ReferenceEquals(before, world)
                ? [new(new CandidateKey($"test/script/{tick}"), new CandidateDue(GraphTestWorld.Time(tick)), new ScriptData())]
                : [];
        public ValueTask<TransitionDraft<SpatialSimulationFact>> PlanSelectedAsync(SpatialSimulationState world,
            OccurrenceCandidate<SpatialOccurrenceData> winner, CancellationToken token) =>
            ValueTask.FromResult(new TransitionDraft<SpatialSimulationFact>(facts.Select(f => (SpatialSimulationFact)new SpatialBusinessFact(f)).ToArray()));
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
        var draft = simulation.Complete(root, instant, new TransitionDraft<SpatialSimulationFact>(facts.Select(f => (SpatialSimulationFact)new SpatialBusinessFact(f)).ToArray()));
        foreach (var fact in draft.Facts) { root = simulation.Fold(root, instant, fact); }
        simulation.Validate(root, instant.ModelTime);
        return root;
    }
}
