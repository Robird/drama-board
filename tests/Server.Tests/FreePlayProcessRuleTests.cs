using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using DramaBoard.Server.FreePlay.Runtime;
using DramaBoard.Spatial;

namespace DramaBoard.Server.Tests;

public sealed class FreePlayProcessRuleTests {
    private const string RunId = "process-rule-tests";

    [Fact]
    public async Task TwoMovesReachDWithoutMidProgrammer() {
        KernelHarness harness = NewHarness(
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("cd"))),
            Respond());

        var modelTimes = new List<ModelTime>();
        for (int transition = 0; transition < 7; transition++) {
            Assert.Equal(StepStatus.Committed, await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
            modelTimes.Add(harness.Kernel.CurrentModelTime);
        }

        ModelTime[] expectedTimes =
            [new(0), new(0), new(2000), new(2000), new(3000), new(3000), new(3000)];
        Assert.Equal(expectedTimes, modelTimes);
        Assert.Equal(2, harness.Programmer.Requests.Count);
        Assert.All(harness.Programmer.Requests, request =>
            Assert.Equal(ProgrammingReason.MissingInstruction, request.Reason));
        Assert.Equal(7, harness.Kernel.World.Process.ControlRevision);
        Assert.IsType<RunningStayProcess>(harness.Kernel.World.Process.Mode);
        Assert.True(harness.Kernel.World.Process.Pending.IsEmpty);
        Assert.Equal(new PlaceId("D"), LocationOf(harness.Kernel.World));
        Assert.Equal(StepStatus.Exhausted, await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task AcceptanceAndStartAreSeparateSameTickTransitions() {
        KernelHarness harness = NewHarness(
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("cd"))),
            Respond());

        await StepCommitted(harness.Kernel, 2);

        OccurrenceEvent<FreePlayFact> acceptance = harness.History.CompletedEvents[0];
        Assert.Equal(new LogicalInstant(new ModelTime(0), 0), acceptance.TargetInstant);
        ProgramAcceptedFact accepted =
            Assert.IsType<ProgramAcceptedFact>(Assert.IsType<ProcessFact>(Assert.Single(acceptance.Facts)).Fact);
        Assert.Equal(2, accepted.Program.Count);

        OccurrenceEvent<FreePlayFact> start = harness.History.CompletedEvents[1];
        Assert.Equal(new LogicalInstant(new ModelTime(0), 1), start.TargetInstant);
        Assert.Equal(2, start.Facts.Count);
        TraversalStartedFact started =
            Assert.IsType<TraversalStartedFact>(Assert.IsType<SpatialFact>(start.Facts[0]).Fact);
        Assert.Equal(new PassageId("ac"), started.PassageId);
        MoveStartedFact moveStarted =
            Assert.IsType<MoveStartedFact>(Assert.IsType<ProcessFact>(start.Facts[1]).Fact);
        Assert.Equal(1, moveStarted.MovementGeneration);
    }

    [Fact]
    public async Task ThinkConsumesTimeKeepsNoteAndMidEventStillFires() {
        KernelHarness harness = NewHarness(
            GenesisWithScheduledAbClosure,
            Respond(new MoveInstruction(new PassageHandle("ac")), new ThinkInstruction("note")),
            Respond());

        await StepCommitted(harness.Kernel, 4);
        Assert.Equal("[2000] note", harness.Kernel.World.Process.Workspace.Text);
        Assert.IsType<RunningThinkProcess>(harness.Kernel.World.Process.Mode);

        await StepCommitted(harness.Kernel, 1);
        // The scheduled entry change commits between the think start (2000,1) and the think completion (3000,0).
        Assert.Equal(new LogicalInstant(new ModelTime(2500), 0), harness.Kernel.LastCommittedInstant);
        Assert.Equal(new LogicalInstant(new ModelTime(2000), 1), harness.History.CompletedEvents[3].TargetInstant);
        Assert.Equal(new LogicalInstant(new ModelTime(2500), 0), harness.History.CompletedEvents[4].TargetInstant);
        ScheduledPassageEntryChangeAppliedFact applied = Assert.IsType<ScheduledPassageEntryChangeAppliedFact>(
            Assert.IsType<SpatialFact>(Assert.Single(harness.History.CompletedEvents[4].Facts)).Fact);
        Assert.Equal(new PassageId("ab"), applied.PassageId);

        await StepCommitted(harness.Kernel, 3);
        Assert.Equal(new LogicalInstant(new ModelTime(3000), 0),
            harness.History.CompletedEvents[5].TargetInstant);
        Assert.Equal(2, harness.Programmer.Requests.Count);
        ProgrammingRequest request = harness.Programmer.Requests[1];
        Assert.Equal(ProgrammingReason.ThinkCompleted, request.Reason);
        Assert.Null(request.ReasonDetail);
        Assert.Equal("[2000] note", request.Workspace.Text);
        KnownPassage ab = Assert.Single(request.KnownPassages, passage => passage.Handle.Value == "ab");
        // The direction bits stay at the static initial values; the dynamic closure is not disclosed.
        Assert.True(ab.EnterableFromA);
        Assert.True(ab.EnterableFromB);
        var queries = new SpatialQueries(harness.Scene.Definition);
        PassageEntryAccess access =
            queries.GetPassageEntryAccess(harness.Kernel.World.Spatial, new PassageId("ab"));
        Assert.False(access.EnterableFromA);
        Assert.True(access.EnterableFromB);

        Assert.IsType<RunningStayProcess>(harness.Kernel.World.Process.Mode);
        Assert.Equal(StepStatus.Exhausted, await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task ThinkCompletionRewritesNonEmptyTailAndWorkspace() {
        // Variant A: the think-complete maintenance rewrites both the tail and the workspace; the
        // completed A->C move and its committed model time are not rewritten.
        KernelHarness rewrite = NewHarness(
            Respond(
                new MoveInstruction(new PassageHandle("ac")),
                new ThinkInstruction("note"),
                new MoveInstruction(new PassageHandle("cd"))),
            RespondWith("revised", new MoveInstruction(new PassageHandle("ac"))),
            Respond());

        await StepCommitted(rewrite.Kernel, 5);
        AwaitingProgrammerProcess awaiting =
            Assert.IsType<AwaitingProgrammerProcess>(rewrite.Kernel.World.Process.Mode);
        Assert.Equal(ProgrammingReason.ThinkCompleted, awaiting.Reason);
        Assert.True(rewrite.Kernel.World.Process.Pending ==
            InstructionBuffer.Of([new MoveInstruction(new PassageHandle("cd"))]));

        await StepCommitted(rewrite.Kernel, 1);
        ProgrammingRequest request = rewrite.Programmer.Requests[1];
        Assert.Equal(ProgrammingReason.ThinkCompleted, request.Reason);
        Assert.Equal(new Instruction[] { new MoveInstruction(new PassageHandle("cd")) }, request.RemainingProgram);
        Assert.Equal("[2000] note", request.Workspace.Text);

        await StepCommitted(rewrite.Kernel, 4);
        Assert.Equal(new ModelTime(5000), rewrite.Kernel.CurrentModelTime);
        Assert.Equal(new PlaceId("A"), LocationOf(rewrite.Kernel.World));
        Assert.True(rewrite.Kernel.World.Process.Pending.IsEmpty);
        Assert.Equal("revised", rewrite.Kernel.World.Process.Workspace.Text);
        OccurrenceEvent<FreePlayFact> firstArrival = rewrite.History.CompletedEvents[2];
        Assert.Equal(new LogicalInstant(new ModelTime(2000), 0), firstArrival.TargetInstant);
        Assert.IsType<TraversalArrivedFact>(Assert.IsType<SpatialFact>(firstArrival.Facts[0]).Fact);
        Assert.IsType<RunningStayProcess>(rewrite.Kernel.World.Process.Mode);
        Assert.Equal(StepStatus.Exhausted, await rewrite.Kernel.StepAsync(new ModelTime(long.MaxValue)));

        // Variant B: resubmitting the existing tail authorizes it; the actor continues to D at 4000 ms.
        KernelHarness resubmit = NewHarness(
            Respond(
                new MoveInstruction(new PassageHandle("ac")),
                new ThinkInstruction("note"),
                new MoveInstruction(new PassageHandle("cd"))),
            Respond(new MoveInstruction(new PassageHandle("cd"))),
            Respond());

        await StepCommitted(resubmit.Kernel, 8);
        Assert.Equal(new ModelTime(4000), resubmit.Kernel.CurrentModelTime);
        Assert.Equal(new PlaceId("D"), LocationOf(resubmit.Kernel.World));
        await StepCommitted(resubmit.Kernel, 2);
        Assert.IsType<RunningStayProcess>(resubmit.Kernel.World.Process.Mode);
        Assert.Equal(StepStatus.Exhausted, await resubmit.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task BlockedMovePreservesHeadAndEntersMaintenance() {
        KernelHarness harness = NewHarness(
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("bd"))),
            Respond(new MoveInstruction(new PassageHandle("bd"))),
            Respond(new MoveInstruction(new PassageHandle("cd"))),
            Respond());

        await StepCommitted(harness.Kernel, 4);
        AwaitingProgrammerProcess blocked =
            Assert.IsType<AwaitingProgrammerProcess>(harness.Kernel.World.Process.Mode);
        Assert.Equal(ProgrammingReason.MoveBlocked, blocked.Reason);
        Assert.True(harness.Kernel.World.Process.Pending ==
            InstructionBuffer.Of([new MoveInstruction(new PassageHandle("bd"))]));

        await StepCommitted(harness.Kernel, 1);
        ProgrammingRequest request = harness.Programmer.Requests[1];
        Assert.Equal(ProgrammingReason.MoveBlocked, request.Reason);
        Assert.False(string.IsNullOrWhiteSpace(request.ReasonDetail));

        // Resubmitting the identical blocked program commits another formal block without a kernel fault.
        await StepCommitted(harness.Kernel, 1);
        Assert.Equal(ProgrammingReason.MoveBlocked,
            Assert.IsType<AwaitingProgrammerProcess>(harness.Kernel.World.Process.Mode).Reason);
        Assert.False(harness.Kernel.IsFaulted);
        Assert.Equal(2, CountProcessFacts<MoveBlockedFact>(harness.History.CompletedEvents));

        await StepCommitted(harness.Kernel, 5);
        Assert.Equal(new PlaceId("D"), LocationOf(harness.Kernel.World));
        Assert.IsType<RunningStayProcess>(harness.Kernel.World.Process.Mode);
        Assert.Equal(StepStatus.Exhausted, await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task StayKeepsTailAndExhaustsNormally() {
        // Variant A: the stay keeps the move tail pending and unexecuted; the injected event still fires.
        KernelHarness stay = NewHarness(
            GenesisWithScheduledAbClosure,
            Respond(new StayInstruction(), new MoveInstruction(new PassageHandle("cd"))));

        await StepCommitted(stay.Kernel, 2);
        Assert.IsType<RunningStayProcess>(stay.Kernel.World.Process.Mode);
        Assert.True(stay.Kernel.World.Process.Pending ==
            InstructionBuffer.Of([new MoveInstruction(new PassageHandle("cd"))]));
        await StepCommitted(stay.Kernel, 1);
        Assert.Equal(new LogicalInstant(new ModelTime(2500), 0), stay.Kernel.LastCommittedInstant);
        Assert.Equal(StepStatus.Exhausted, await stay.Kernel.StepAsync(new ModelTime(long.MaxValue)));

        // Variant B: the think tail stays visible and unexecuted; no think facts ever appear.
        KernelHarness think = NewHarness(
            GenesisWithScheduledAbClosure,
            Respond(new StayInstruction(), new ThinkInstruction("note")));

        await StepCommitted(think.Kernel, 2);
        Assert.IsType<RunningStayProcess>(think.Kernel.World.Process.Mode);
        Assert.True(think.Kernel.World.Process.Pending ==
            InstructionBuffer.Of([new ThinkInstruction("note")]));
        Assert.Equal(string.Empty, think.Kernel.World.Process.Workspace.Text);
        await StepCommitted(think.Kernel, 1);
        Assert.Equal(0, CountProcessFacts<ThinkStartedFact>(think.History.CompletedEvents));
        Assert.Equal(StepStatus.Exhausted, await think.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task LegalEmptyProgramNormalizesToStayAtAcceptance() {
        KernelHarness harness = NewHarness(Respond());

        await StepCommitted(harness.Kernel, 1);
        OccurrenceEvent<FreePlayFact> acceptance = harness.History.CompletedEvents[0];
        ProgramAcceptedFact accepted =
            Assert.IsType<ProgramAcceptedFact>(Assert.IsType<ProcessFact>(Assert.Single(acceptance.Facts)).Fact);
        Assert.IsType<StayInstruction>(Assert.Single(accepted.Program));

        await StepCommitted(harness.Kernel, 1);
        Assert.IsType<RunningStayProcess>(harness.Kernel.World.Process.Mode);
        Assert.Equal(StepStatus.Exhausted, await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
    }

    [Fact]
    public async Task InvalidResponseFailsStepWithZeroCommitAndIsReasked() {
        KernelHarness harness = NewHarness(
            RespondWithUnknownHandle(),
            Respond(new MoveInstruction(new PassageHandle("ac"))));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)));
        Assert.Equal(0, harness.Kernel.Version.TransitionCount);
        Assert.Empty(harness.History.CompletedEvents);
        Assert.False(harness.Kernel.IsFaulted);
        Assert.Same(harness.InitialWorld, harness.Kernel.World);

        await StepCommitted(harness.Kernel, 3);
        Assert.Equal(2, harness.Programmer.Requests.Count);
        ProgrammingRequest first = harness.Programmer.Requests[0];
        ProgrammingRequest reasked = harness.Programmer.Requests[1];
        Assert.Equal(first.ActivationId.Value, reasked.ActivationId.Value);
        Assert.Equal(first.ModelTimeMs, reasked.ModelTimeMs);
        Assert.Equal(first.Reason, reasked.Reason);
        Assert.Equal(new PlaceId("C"), LocationOf(harness.Kernel.World));
    }

    [Fact]
    public async Task RequestMaterialsAreFrozenAndComplete() {
        KernelHarness harness = NewHarness(
            Respond(new MoveInstruction(new PassageHandle("ac"))),
            Respond());

        await StepCommitted(harness.Kernel, 1);
        ProgrammingRequest request = Assert.Single(harness.Programmer.Requests);
        Assert.Equal($"{RunId}:0", request.ActivationId.Value);
        Assert.Equal("human", request.ActorId);
        Assert.Equal(0, request.ModelTimeMs);
        Assert.Equal(ProgrammingReason.MissingInstruction, request.Reason);
        Assert.Null(request.ReasonDetail);
        Assert.Equal("A", request.PlaceId);
        Assert.Equal(string.Empty, request.Workspace.Text);
        Assert.Empty(request.RemainingProgram);
        KnownPassage[] expectedPassages =
        [
            new(new PassageHandle("ab"), "A", "B", 1000, true, true),
            new(new PassageHandle("ac"), "A", "C", 2000, true, true),
            new(new PassageHandle("bd"), "B", "D", 3000, true, true),
            new(new PassageHandle("cd"), "C", "D", 1000, true, true),
        ];
        Assert.Equal(expectedPassages, request.KnownPassages);

        // Advancing the world must not mutate the earlier captured request object.
        await StepCommitted(harness.Kernel, 4);
        Assert.Equal(new PlaceId("C"), LocationOf(harness.Kernel.World));
        Assert.Equal($"{RunId}:0", request.ActivationId.Value);
        Assert.Equal("A", request.PlaceId);
        Assert.Equal(0, request.ModelTimeMs);
        Assert.Equal(string.Empty, request.Workspace.Text);
        Assert.Empty(request.RemainingProgram);
        Assert.Equal(expectedPassages, request.KnownPassages);
    }

    [Fact]
    public async Task SameTickBudgetExhaustionIsDeterministicFault() {
        var factories = new List<Func<ProgrammingRequest, ProgrammingResponse>>
        {
            Respond(new MoveInstruction(new PassageHandle("ac")), new MoveInstruction(new PassageHandle("bd"))),
        };
        for (int index = 0; index < 200; index++) {
            factories.Add(Respond(new MoveInstruction(new PassageHandle("bd"))));
        }

        KernelHarness harness = NewHarness([.. factories]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            while (await harness.Kernel.StepAsync(new ModelTime(long.MaxValue)) == StepStatus.Committed) {
            }
        });

        // Two transitions at tick 0 plus exactly the 100-transition budget at tick 2000; the 101st
        // proposal at that tick throws deterministically before any further commit.
        Assert.Equal(102, harness.Kernel.Version.TransitionCount);
        Assert.Equal(new LogicalInstant(new ModelTime(2000), 99), harness.Kernel.LastCommittedInstant);
        Assert.False(harness.Kernel.IsFaulted);
        Assert.Equal(ProgrammingReason.MoveBlocked,
            Assert.IsType<AwaitingProgrammerProcess>(harness.Kernel.World.Process.Mode).Reason);
        Assert.True(harness.Kernel.World.Process.Pending ==
            InstructionBuffer.Of([new MoveInstruction(new PassageHandle("bd"))]));
        Assert.Equal(50, harness.Programmer.Requests.Count);
    }

    private static FreePlayScene NewScene() => new(
        RunId,
        (_, _, _) => throw new InvalidOperationException("Process-rule tests never use the legacy decision path."));

    private static KernelHarness NewHarness(params Func<ProgrammingRequest, ProgrammingResponse>[] factories) =>
        NewHarness(Genesis, factories);

    private static KernelHarness NewHarness(
        Func<FreePlayScene, FreePlayWorld> worldFactory,
        params Func<ProgrammingRequest, ProgrammingResponse>[] factories) {
        FreePlayScene scene = NewScene();
        var programmer = new RecordingProgrammer(new ScriptedDynamicProgrammer(factories));
        FreePlayWorld world = worldFactory(scene);
        var history = new InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact>(
            world, new KernelCursor(new WorldVersion(1, 0), ModelTime.Zero, null, null));
        var kernel = new SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact>(
            history,
            new SimulationRules(1, 100),
            [
                new FreePlaySpatialRule(scene.Definition),
                new FreePlayProcessRule(
                    RunId,
                    programmer,
                    new SpatialPlanner(scene.Definition),
                    new SpatialQueries(scene.Definition),
                    scene.Definition),
            ],
            scene.Fold,
            scene.Validate);
        return new(scene, programmer, history, kernel, world);
    }

    private static FreePlayWorld Genesis(FreePlayScene scene) => scene.Genesis();

    private static FreePlayWorld GenesisWithScheduledAbClosure(FreePlayScene scene) {
        FreePlayWorld world = scene.Genesis();
        return scene.Fold(
            world,
            new LogicalInstant(ModelTime.Zero, 0),
            new SpatialFact(new PassageEntryChangeScheduledFact(
                new PassageId("ab"), new ModelTime(2500), new PassageEntryPatch(false, null))));
    }

    private static async Task StepCommitted(
        SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> kernel,
        int times) {
        for (int transition = 0; transition < times; transition++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
        }
    }

    private static PlaceId LocationOf(FreePlayWorld world) {
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == FreePlayScene.Actor);
        return Assert.IsType<AtPlaceLocation>(actor.Location).PlaceId;
    }

    private static int CountProcessFacts<TProcessFact>(IReadOnlyList<OccurrenceEvent<FreePlayFact>> events)
        where TProcessFact : FreePlayProcessFact =>
        events.SelectMany(@event => @event.Facts)
            .Count(fact => fact is ProcessFact process && process.Fact is TProcessFact);

    private static Func<ProgrammingRequest, ProgrammingResponse> Respond(params Instruction[] program) =>
        request => new ProgrammingResponse(request.ActivationId, program, request.Workspace);

    private static Func<ProgrammingRequest, ProgrammingResponse> RespondWith(
        string workspaceText, params Instruction[] program) =>
        request => new ProgrammingResponse(request.ActivationId, program, new SubjectiveWorkspace(workspaceText));

    private static Func<ProgrammingRequest, ProgrammingResponse> RespondWithUnknownHandle() =>
        request => new ProgrammingResponse(
            request.ActivationId, [new MoveInstruction(new PassageHandle("zz"))], request.Workspace);

    /// <summary>Counting decorator that records every frozen request passed to the inner programmer.</summary>
    private sealed class RecordingProgrammer : IDynamicProgrammer {
        private readonly IDynamicProgrammer _inner;

        public RecordingProgrammer(IDynamicProgrammer inner) {
            ArgumentNullException.ThrowIfNull(inner);
            _inner = inner;
        }

        public List<ProgrammingRequest> Requests { get; } = [];

        public async ValueTask<ProgrammingResponse> ProgramAsync(
            ProgrammingRequest request,
            CancellationToken cancellationToken) {
            Requests.Add(request);
            return await _inner.ProgramAsync(request, cancellationToken);
        }
    }

    private sealed record KernelHarness(
        FreePlayScene Scene,
        RecordingProgrammer Programmer,
        InMemoryOccurrenceHistory<FreePlayWorld, FreePlayFact> History,
        SimulationKernel<FreePlayWorld, FreePlayCandidate, FreePlayFact> Kernel,
        FreePlayWorld InitialWorld);
}
