using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ExpeditionRecoveryValidationTests {
    [Theory]
    [InlineData("gather-from-camp")]
    [InlineData("escape-from-camp")]
    [InlineData("escape-while-working")]
    [InlineData("scheduled-passage-change")]
    public void Cold_open_rejects_restorable_but_unrunnable_expedition_boundaries_without_repairing_disk(string invalidBoundary) {
        var initial = CycleGameFactory.CreateSurvival(Hour(8), enableNeeds: false, enableWeather: false);
        CycleBusinessState business = initial.World.Business;
        CycleExpedition expedition = initial.World.Expedition!;
        CycleCharacter actor = initial.World.GetCharacter("a");
        GraphSpatialState spatial = expedition.Spatial;
        CycleCharacterEnding? ending = null;
        switch (invalidBoundary) {
            case "gather-from-camp":
                // Individually valid activity and known resource, but the actor is elsewhere.
                actor = actor.Change(new(CycleActivityKind.Gather, 1, Hour(8), CycleTime.Hour,
                    Hour(9), "berries", 1), nextActivityId: 2);
                break;
            case "escape-from-camp":
                spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, []);
                ending = new(CycleExpeditionOutcome.Escaped, Hour(8), "Forged escape before reaching safety.", new AtPlaceLocation(new("camp")));
                break;
            case "escape-while-working":
                spatial = GraphSpatialState.Create(CycleExpeditionMap.Graph, []);
                actor = actor.Change(new(CycleActivityKind.Wait, 1, Hour(8), CycleTime.Hour, Hour(9)), nextActivityId: 2);
                ending = new(CycleExpeditionOutcome.Escaped, Hour(8), "An exit cannot leave ordinary timed work pending.", new AtPlaceLocation(new("road")));
                break;
            case "scheduled-passage-change":
                string passage = CycleExpeditionMap.GetPassages("camp")[0].Id;
                // Valid in Spatial, but this application has no rule for applying scheduled access changes.
                spatial = GraphSpatialState.Restore(CycleExpeditionMap.Graph, spatial.Entities,
                    spatial.PassageEntryAccessOverrides, [new(new(passage), Hour(9), new(false, null))], spatial.ConsumedContacts);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidBoundary));
        }
        if (ending is not null) {
            // Deliberately bypass End, which normally clears activity and captures a valid terminal boundary.
            actor = new(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack, actor.Activity, actor.Notices,
                actor.DecisionGeneration, actor.LastResult, actor.NextActivityId, actor.Survival, ending);
        }
        var candidate = business.Replace(business.Time, changedCharacters: [actor],
            expedition: new(expedition.ScenarioId, spatial, expedition.Visits));
        if (ending is not null) {
            // Keep the separate ending/context invariant satisfied so rejection still tests
            // the forged escape location or unfinished activity, not an unrelated missing context.
            var raw = new CycleWorld(candidate, initial.World.Forecasts);
            CycleObservation observation = CycleDecisionContext.ObserveOpening(raw, actor.Id, 1);
            var opening = new CharacterContextOpening(CycleBackground.For(observation), observation, null,
                business.Time, CycleOutcomeActivityState.Terminal);
            candidate = candidate.Replace(business.Time, interactionChanges: [
                new(actor.Id, new(1, CharacterContextStatus.Terminal, opening))]);
        }
        Assert.Throws<InvalidOperationException>(() => new CycleDurableRoot(candidate, initial.Cursor));

        using var store = new TemporaryStore();
        var models = new StateModelRegistry();
        CycleDurableModels.Register(models);
        string savedHead;
        using (Repository repository = Repository.CreateNew(store.StorePath, models)) {
            // Model a schema-restorable save: raw DG does not enforce application readiness.
            var root = CycleDurableRoot.CreateRevisionCandidate(candidate, initial.Cursor, CycleGameRevision.Current);
            BranchId branch = repository.CreateRootBranchFromState("unrunnable-expedition", root).Id;
            savedHead = repository.ReadCurrentCommit(branch).Commit.Id.ToString()
                ?? throw new InvalidOperationException("A recorded commit must have a diagnostic identity.");
        }

        Assert.Throws<InvalidOperationException>(() => CycleDurableHistory.Open(store.StorePath));

        using Repository inspected = Repository.OpenReadOnlyExisting(store.StorePath, models);
        CommitInfo head = inspected.ReadCurrentCommit(Assert.Single(inspected.ListBranches()).Id).Commit;
        Assert.Equal(savedHead, head.Id.ToString());
        var restored = Assert.IsType<CycleDurableRoot>(inspected.LoadState(head.Id));
        Assert.Equal(initial.Cursor, restored.Cursor);
        Assert.Equal(spatial, restored.Business.Expedition!.Spatial);
        Assert.Equal(ending, restored.Business.GetCharacter("a").Ending);
        Assert.Equal(actor.Activity?.ToString(), restored.Business.GetCharacter("a").Activity?.ToString());
    }
}
