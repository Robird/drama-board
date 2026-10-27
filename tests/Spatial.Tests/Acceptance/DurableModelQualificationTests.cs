using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial.Tests.TestSupport;

namespace DramaBoard.Spatial.Tests.Acceptance;

public sealed class DurableModelQualificationTests {
    [Fact]
    public async Task GeneratedModels_SaveAndColdReadSpatialStateFactsAndCursorValues() {
        GraphDefinition definition = GraphDefinition.Create(
            [GraphTestWorld.A, GraphTestWorld.B],
            [GraphTestWorld.Passage(GraphTestWorld.Bridge, GraphTestWorld.A, GraphTestWorld.B, length: 11)]);
        GraphSpatialState world = GraphTestWorld.State(definition,
            ("alice", GraphTestWorld.A), ("bob", GraphTestWorld.B), ("waiting", GraphTestWorld.A));
        var planner = new SpatialPlanner(definition);
        var reducer = new GraphSpatialReducer(definition);
        world = GraphTestWorld.Fold(reducer, world, GraphTestWorld.Instant(0),
            planner.TryStartTraversal(world, new EntityId("alice"), GraphTestWorld.Bridge, 3, ModelTime.Zero));
        world = GraphTestWorld.Fold(reducer, world, GraphTestWorld.Instant(0, 1),
            planner.TryStartTraversal(world, new EntityId("bob"), GraphTestWorld.Bridge, 2, ModelTime.Zero));
        var contactRule = new BusinessContactRule(definition);
        var candidate = Assert.Single(contactRule.Forecast(world, new SimulationRules(71, 100)));
        TransitionDraft<GraphSpatialFact> contact = await contactRule.PlanSelectedAsync(world, candidate, default);
        LogicalInstant instant = GraphTestWorld.Instant(2);
        GraphSpatialFact[] facts = [
            .. contact.Facts,
            .. GraphTestWorld.Accepted(planner.TrySetPassageEntryAccess(world, GraphTestWorld.Bridge,
                new PassageEntryPatch(false, null))).Facts,
            .. GraphTestWorld.Accepted(planner.TrySchedulePassageEntryChange(world, GraphTestWorld.Bridge,
                GraphTestWorld.Time(20), new PassageEntryPatch(null, false), instant.ModelTime)).Facts,
        ];
        var occurrence = new OccurrenceEvent<GraphSpatialFact>(candidate.Key, instant, facts);
        world = facts.Aggregate(world, (current, fact) => reducer.Apply(current, instant, fact));
        GraphSpatialStateValidator.ValidateComplete(definition, world);
        Assert.Single(world.ConsumedContacts);
        Assert.Single(world.PassageEntryAccessOverrides);
        Assert.Single(world.ScheduledPassageEntryChanges);

        // Cursor is an inline value, so exercise its generated fields inside an existing generic root.
        // These independent samples qualify model generation; they are not an IOccurrenceHistory adapter.
        KernelCursor genesisCursor = new(new WorldVersion(17, 0), ModelTime.Zero, null, null);
        KernelCursor completedCursor = genesisCursor.Advance(occurrence.CauseKey, instant);
        var cursorValues = new OccurrenceEvent<KernelCursor>(candidate.Key, instant,
            [genesisCursor, completedCursor]);
        string path = Path.Combine(Path.GetTempPath(), "dramaboard-model-qualification-" + Guid.NewGuid().ToString("N"));
        try {
            CommitId stateId, eventId, cursorId;
            using (Repository repository = Repository.CreateNew(path, RegisterModels())) {
                BranchId stateBranch = repository.CreateRootBranchFromState("spatial-state", world).Id;
                BranchId eventBranch = repository.CreateRootBranchFromEvent("spatial-facts", occurrence).Id;
                BranchId cursorBranch = repository.CreateRootBranchFromEvent("cursor-values", cursorValues).Id;
                stateId = repository.ReadCurrentCommit(stateBranch).Commit.Id;
                eventId = repository.ReadCurrentCommit(eventBranch).Commit.Id;
                cursorId = repository.ReadCurrentCommit(cursorBranch).Commit.Id;
            }

            // Fresh registrations and a reopened repository rule out retained CLR objects and warm closures.
            using Repository reopened = Repository.OpenReadOnlyExisting(path, RegisterModels());
            GraphSpatialState restored = Assert.IsType<GraphSpatialState>(reopened.LoadState(stateId));
            Assert.NotSame(world, restored);
            Assert.Equal(world, restored);
            GraphSpatialStateValidator.ValidateComplete(definition, restored);
            Assert.Equal(2, restored.Entities.Count(entity => entity.Location is TraversingLocation));
            Assert.Single(restored.Entities, entity => entity.Location is AtPlaceLocation);
            for (int index = 0; index < world.Entities.Count; index++) {
                Assert.NotSame(world.Entities[index], restored.Entities[index]);
                Assert.NotSame(world.Entities[index].Location, restored.Entities[index].Location);
            }

            var restoredOccurrence = Assert.IsType<OccurrenceEvent<GraphSpatialFact>>(reopened.LoadEvent(eventId));
            restoredOccurrence.Validate();
            Assert.NotSame(occurrence, restoredOccurrence);
            Assert.NotSame(occurrence.CauseKey, restoredOccurrence.CauseKey);
            Assert.Equal(occurrence.CauseKey, restoredOccurrence.CauseKey);
            Assert.Equal(occurrence.TargetInstant, restoredOccurrence.TargetInstant);
            Assert.Equal(occurrence.Facts, restoredOccurrence.Facts);
            Assert.Collection(restoredOccurrence.Facts,
                fact => Assert.IsType<PassageContactOccurredFact>(fact),
                fact => Assert.IsType<PassageEntryAccessChangedFact>(fact),
                fact => Assert.IsType<PassageEntryChangeScheduledFact>(fact));

            var restoredCursors = Assert.IsType<OccurrenceEvent<KernelCursor>>(reopened.LoadEvent(cursorId));
            restoredCursors.Validate();
            Assert.Equal(cursorValues.Facts, restoredCursors.Facts);
            Assert.All(restoredCursors.Facts, cursor => cursor.Validate());
            Assert.Null(restoredCursors.Facts[0].LastInstant);
            Assert.Same(restoredCursors.CauseKey, restoredCursors.Facts[1].LastCauseKey);
        } finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static StateModelRegistry RegisterModels() {
        var models = new StateModelRegistry();
        KernelDurableModels.Register(models);
        SpatialDurableModels.Register(models);
        return models;
    }
}
