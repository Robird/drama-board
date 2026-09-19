using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using DramaBoard.Server.FreePlay.Runtime;
using DramaBoard.Spatial;

namespace DramaBoard.Server.Tests;

public sealed class FreePlaySpatialRuleTests {
    [Fact]
    public async Task ArrivalCommitsSpatialAndProcessInOneBatch() {
        FreePlayScene scene = NewScene();
        FreePlaySpatialRule rule = new(scene.Definition);
        FreePlayWorld world = scene.Genesis();
        world = Fold(scene, world, ProgramAccepted(
            new MoveInstruction(new PassageHandle("ac")),
            new MoveInstruction(new PassageHandle("cd"))));
        world = Fold(scene, world, TraversalStartedFacts(scene, world));
        world = Fold(scene, world, new ProcessFact(new MoveStartedFact(1)));
        scene.Validate(world);
        SpatialEntity traversing = world.Spatial.Entities.Single(entity => entity.Id == FreePlayScene.Actor);
        Assert.Equal(1, traversing.MovementGeneration);

        InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> history = NewHistory(world);
        SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> kernel = NewKernel(scene, rule, history);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));

        Assert.Equal(new LogicalInstant(new ModelTime(2000), 0), kernel.LastCommittedInstant);
        Assert.Equal(new ModelTime(2000), kernel.CurrentModelTime);
        OccurrenceEvent<FreePlayFact> batch = Assert.Single(history.CompletedEvents);
        Assert.Equal(2, batch.Facts.Count);
        TraversalArrivedFact spatial =
            Assert.IsType<TraversalArrivedFact>(Assert.IsType<SpatialFact>(batch.Facts[0]).Fact);
        Assert.Equal(FreePlayScene.Actor, spatial.EntityId);
        Assert.Equal(1, spatial.ExpectedMovementGeneration);
        MoveArrivedFact process =
            Assert.IsType<MoveArrivedFact>(Assert.IsType<ProcessFact>(batch.Facts[1]).Fact);
        Assert.Equal(1, process.ExpectedMovementGeneration);

        SpatialEntity actor = kernel.World.Spatial.Entities.Single(entity => entity.Id == FreePlayScene.Actor);
        AtPlaceLocation location = Assert.IsType<AtPlaceLocation>(actor.Location);
        Assert.Equal(new PlaceId("C"), location.PlaceId);
        Assert.IsType<ReadyProcess>(kernel.World.Process.Mode);
        Assert.True(kernel.World.Process.Pending == InstructionBuffer.Of(
            [new MoveInstruction(new PassageHandle("cd"))]));

        Assert.Equal(StepStatus.Exhausted, await kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task ArrivalWithEmptyTailRequestsNewProgram() {
        FreePlayScene scene = NewScene();
        FreePlaySpatialRule rule = new(scene.Definition);
        FreePlayWorld world = scene.Genesis();
        world = Fold(scene, world, ProgramAccepted(new MoveInstruction(new PassageHandle("ac"))));
        world = Fold(scene, world, TraversalStartedFacts(scene, world));
        world = Fold(scene, world, new ProcessFact(new MoveStartedFact(1)));

        InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> history = NewHistory(world);
        SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> kernel = NewKernel(scene, rule, history);

        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));

        AwaitingProgrammerProcess awaiting = Assert.IsType<AwaitingProgrammerProcess>(kernel.World.Process.Mode);
        Assert.Equal(ProgrammingReason.MissingInstruction, awaiting.Reason);
        Assert.True(kernel.World.Process.Pending.IsEmpty);
    }

    [Fact]
    public async Task ArrivalWithoutRunningMoveFailsBeforeCommit() {
        FreePlayScene scene = NewScene();
        FreePlaySpatialRule rule = new(scene.Definition);
        FreePlayWorld world = scene.Genesis();
        world = Fold(scene, world, TraversalStartedFacts(scene, world));
        var runningThink = new PlayerProcessState(
            new SubjectiveWorkspace(""), InstructionBuffer.Empty, new RunningThinkProcess(999_999), 0);
        world = world with { Process = runningThink };
        scene.Validate(world);

        InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> history = NewHistory(world);
        SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> kernel = NewKernel(scene, rule, history);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal(0, kernel.Version.TransitionCount);
        Assert.Empty(history.CompletedEvents);
    }

    private static FreePlayScene NewScene() => new(
        "spatial-rule-tests",
        (_, _, _) => throw new InvalidOperationException("Spatial-rule tests never make decisions."));

    private static FreePlayFact ProgramAccepted(params Instruction[] program) =>
        new ProcessFact(new ProgramAcceptedFact(program, new SubjectiveWorkspace("")));

    private static FreePlayFact[] TraversalStartedFacts(FreePlayScene scene, FreePlayWorld world) {
        var planner = new SpatialPlanner(scene.Definition);
        SpatialPlanAccepted plan = Assert.IsType<SpatialPlanAccepted>(planner.TryStartTraversal(
            world.Spatial, FreePlayScene.Actor, new PassageId("ac"), 1, ModelTime.Zero));
        return plan.Facts.Select(fact => new SpatialFact(fact)).ToArray();
    }

    private static FreePlayWorld Fold(FreePlayScene scene, FreePlayWorld world, params FreePlayFact[] facts) {
        var instant = new LogicalInstant(new ModelTime(0), 0);
        foreach (FreePlayFact fact in facts) {
            world = scene.Fold(world, instant, fact);
        }

        return world;
    }

    private static InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> NewHistory(FreePlayWorld world) =>
        new(world, new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));

    private static SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> NewKernel(
        FreePlayScene scene, FreePlaySpatialRule rule, InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> history) =>
        new(history, new SimulationRules(1, 100), [rule], scene.Fold, scene.Validate);
}
