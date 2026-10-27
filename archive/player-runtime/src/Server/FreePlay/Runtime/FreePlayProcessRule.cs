using System.Buffers;
using System.Globalization;
using System.Text.Json;
using DramaBoard.Decision.Validation;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Player.Agency.Spatial;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using DramaBoard.Spatial;

namespace DramaBoard.Server.FreePlay.Runtime;

/// <summary>
/// Forecasts and plans the player process occurrences of the FreePlay scene: programmer responses,
/// head instruction starts and think completions. Movement truth stays in the Spatial domain; this
/// rule only maintains the process mode, the pending buffer and the subjective workspace.
/// </summary>
internal sealed class FreePlayProcessRule : IOccurrenceRule<FreePlayWorld, FreePlayCandidate, FreePlayFact> {
    private const long MoveSpeedUnitsPerMs = 1;
    private const long ThinkDurationMs = 1000;

    private readonly string _runId;
    private readonly IDynamicProgrammer _programmer;
    private readonly SpatialPlanner _planner;
    private readonly SpatialQueries _queries;
    private readonly GraphDefinition _definition;

    /// <summary>Initializes the process rule for one run lineage.</summary>
    public FreePlayProcessRule(
        string runId,
        IDynamicProgrammer programmer,
        SpatialPlanner planner,
        SpatialQueries queries,
        GraphDefinition definition) {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(programmer);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(definition);
        _runId = runId;
        _programmer = programmer;
        _planner = planner;
        _queries = queries;
        _definition = definition;
    }

    /// <inheritdoc />
    public IReadOnlyList<OccurrenceCandidate<FreePlayCandidate>> Forecast(FreePlayWorld world, SimulationRules rules) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rules);
        return world.Process.Mode switch {
            AwaitingProgrammerProcess => SingleCandidate(
                FreePlayProcessCandidateKind.Respond,
                CreateRespondKey(world.Process.ControlRevision),
                new CandidateDue(world.EffectiveTime)),
            ReadyProcess => SingleCandidate(
                FreePlayProcessCandidateKind.Start,
                CreateStartKey(world.Process.ControlRevision),
                new CandidateDue(world.EffectiveTime)),
            RunningThinkProcess think => SingleCandidate(
                FreePlayProcessCandidateKind.ThinkComplete,
                CreateThinkCompleteKey(think.CompletesAtMs),
                new CandidateDue(new ModelTime(think.CompletesAtMs))),
            RunningMoveProcess => [],
            RunningStayProcess => [],
            _ => throw new InvalidOperationException(
                $"The process rule does not know process mode '{DescribeMode(world.Process.Mode)}'."),
        };
    }

    /// <inheritdoc />
    public async ValueTask<TransitionDraft<FreePlayFact>> PlanSelectedAsync(
        FreePlayWorld world,
        OccurrenceCandidate<FreePlayCandidate> winner,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(winner);
        cancellationToken.ThrowIfCancellationRequested();
        if (winner.Data.Process is not { } process) {
            throw new InvalidOperationException("The FreePlay process rule only plans FreePlay process candidates.");
        }

        return process.Kind switch {
            FreePlayProcessCandidateKind.Respond =>
                await PlanRespondAsync(world, winner, cancellationToken),
            FreePlayProcessCandidateKind.Start => PlanStart(world, winner),
            FreePlayProcessCandidateKind.ThinkComplete => PlanThinkComplete(world, winner),
            _ => throw new InvalidOperationException(
                $"Unsupported FreePlay process candidate kind '{process.Kind}'."),
        };
    }

    private async ValueTask<TransitionDraft<FreePlayFact>> PlanRespondAsync(
        FreePlayWorld world,
        OccurrenceCandidate<FreePlayCandidate> winner,
        CancellationToken cancellationToken) {
        if (world.Process.Mode is not AwaitingProgrammerProcess awaiting) {
            throw new InvalidOperationException(
                "Planning a respond occurrence requires an awaiting-programmer process, "
                + $"but the current mode is '{DescribeMode(world.Process.Mode)}'.");
        }

        if (winner.Due.ModelTime != world.EffectiveTime ||
            winner.Key != CreateRespondKey(world.Process.ControlRevision)) {
            throw new InvalidOperationException("The selected respond occurrence does not match current state.");
        }

        ProgrammingRequest request = CreateRequest(world, awaiting.Reason);
        ProgrammingResponse response = await _programmer.ProgramAsync(request, cancellationToken)
            ?? throw new InvalidOperationException("The dynamic programmer returned a null response.");
        ProgrammingResponseValidationResult validation = ProgrammingResponseValidator.Validate(response, request);
        if (!validation.IsValid) {
            throw new InvalidOperationException(
                $"The dynamic programmer response for activation '{request.ActivationId.Value}' was rejected: "
                + $"{validation.Message ?? validation.Error.ToString()} The step fails without committing.");
        }

        // A legal empty program normalizes to a stay at acceptance time only; the fact constructor refuses empty programs.
        IReadOnlyList<Instruction> program = response.Program.Count == 0
            ? [new StayInstruction()]
            : response.Program;
        return new TransitionDraft<FreePlayFact>(
            [new ProcessFact(new ProgramAcceptedFact(program, response.Workspace))]);
    }

    private TransitionDraft<FreePlayFact> PlanStart(FreePlayWorld world, OccurrenceCandidate<FreePlayCandidate> winner) {
        if (world.Process.Mode is not ReadyProcess) {
            throw new InvalidOperationException(
                "Planning a start occurrence requires a ready process, "
                + $"but the current mode is '{DescribeMode(world.Process.Mode)}'.");
        }

        if (winner.Due.ModelTime != world.EffectiveTime ||
            winner.Key != CreateStartKey(world.Process.ControlRevision)) {
            throw new InvalidOperationException("The selected start occurrence does not match current state.");
        }

        Instruction? head = world.Process.Pending.Head;
        return head switch {
            MoveInstruction move => PlanMoveStart(world, move),
            ThinkInstruction think => new TransitionDraft<FreePlayFact>(
                [new ProcessFact(new ThinkStartedFact(think.Note, ThinkDurationMs))]),
            StayInstruction => new TransitionDraft<FreePlayFact>([new ProcessFact(new StayStartedFact())]),
            null => throw new InvalidOperationException(
                "Starting a ready process requires a pending head instruction."),
            _ => throw new InvalidOperationException(
                $"Unsupported pending head instruction '{head!.GetType().Name}'."),
        };
    }

    private TransitionDraft<FreePlayFact> PlanMoveStart(FreePlayWorld world, MoveInstruction move) {
        var passageId = new PassageId(move.Passage.Value);
        SpatialPlanResult plan = _planner.TryStartTraversal(
            world.Spatial, FreePlayScene.Actor, passageId, MoveSpeedUnitsPerMs, world.EffectiveTime);
        if (plan is SpatialPlanRejected) {
            // A blocked move is a formal in-world outcome: the head and tail stay pending and the
            // process enters maintenance with the MoveBlocked reason instead of consuming the head.
            return new TransitionDraft<FreePlayFact>([new ProcessFact(new MoveBlockedFact(passageId.Value))]);
        }

        if (plan is not SpatialPlanAccepted accepted ||
            accepted.Facts.Count != 1 ||
            accepted.Facts[0] is not TraversalStartedFact started) {
            throw new InvalidOperationException("Starting a move must plan exactly one TraversalStartedFact.");
        }

        // The generation is read from the pre-plan world actor, matching the Spatial reducer's +1 rule.
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == FreePlayScene.Actor);
        return new TransitionDraft<FreePlayFact>(
            [new SpatialFact(started), new ProcessFact(new MoveStartedFact(actor.MovementGeneration + 1))]);
    }

    private TransitionDraft<FreePlayFact> PlanThinkComplete(
        FreePlayWorld world,
        OccurrenceCandidate<FreePlayCandidate> winner) {
        if (world.Process.Mode is not RunningThinkProcess running) {
            throw new InvalidOperationException(
                "Planning a think-complete occurrence requires a running-think process, "
                + $"but the current mode is '{DescribeMode(world.Process.Mode)}'.");
        }

        if (winner.Due.ModelTime.Ticks != running.CompletesAtMs ||
            winner.Key != CreateThinkCompleteKey(running.CompletesAtMs)) {
            throw new InvalidOperationException("The selected think-complete occurrence does not match current state.");
        }

        return new TransitionDraft<FreePlayFact>([new ProcessFact(new ThinkCompletedFact())]);
    }

    private ProgrammingRequest CreateRequest(FreePlayWorld world, ProgrammingReason reason) {
        string placeId = _queries.GetLocation(world.Spatial, FreePlayScene.Actor, world.EffectiveTime) switch {
            AtPlaceView place => place.PlaceId.Value,
            TraversingView => throw new InvalidOperationException(
                "A respond occurrence requires the actor to be at a place, not traversing a passage."),
            _ => throw new InvalidOperationException("The actor has an unknown location view."),
        };

        // The known map is the full objective definition. Direction bits come from the known graph's static
        // InitialEntryAccess and never from the world's current overrides: remote dynamic entry state is not
        // legitimate disclosure, and dynamic closures surface as planner rejections with MoveBlocked reasons.
        // ExpectedDurationMs equals the passage length because moves run at 1 unit per millisecond.
        GraphDefinition knownGraph = FullMapPlayerSpatialKnowledgeGetter<FreePlayWorld>.Instance
            .GetKnownGraph(world, FreePlayScene.Actor.Value, _definition).KnownGraph;
        KnownPassage[] knownPassages = [.. knownGraph.Passages
            .OrderBy(passage => passage.Id)
            .Select(passage => new KnownPassage(
                new PassageHandle(passage.Id.Value),
                passage.EndpointA.Value,
                passage.EndpointB.Value,
                passage.Length,
                passage.InitialEntryAccess.EnterableFromA,
                passage.InitialEntryAccess.EnterableFromB))];

        return new ProgrammingRequest(
            new ActivationId(string.Create(CultureInfo.InvariantCulture, $"{_runId}:{world.Process.ControlRevision}")),
            FreePlayScene.Actor.Value,
            world.EffectiveTime.Ticks,
            reason,
            reason == ProgrammingReason.MoveBlocked ? DescribeBlockedMove(world) : null,
            placeId,
            knownPassages,
            world.Process.Workspace,
            world.Process.Pending.Items);
    }

    private string DescribeBlockedMove(FreePlayWorld world) {
        if (world.Process.Pending.Head is not MoveInstruction move) {
            throw new InvalidOperationException(
                "A move-blocked respond occurrence requires a pending move instruction as the buffer head.");
        }

        var passageId = new PassageId(move.Passage.Value);
        PassageDefinition passage = _definition.GetPassage(passageId);
        SpatialEntity actor = world.Spatial.Entities.Single(entity => entity.Id == FreePlayScene.Actor);
        if (actor.Location is not AtPlaceLocation atPlace) {
            throw new InvalidOperationException("A move-blocked respond occurrence requires the actor to be at a place.");
        }

        if (atPlace.PlaceId != passage.EndpointA && atPlace.PlaceId != passage.EndpointB) {
            return string.Create(CultureInfo.InvariantCulture,
                $"The pending move through passage '{passageId.Value}' from place '{atPlace.PlaceId.Value}' is blocked: "
                + $"'{atPlace.PlaceId.Value}' is not an endpoint of the passage.");
        }

        PassageEntryAccess access = _queries.GetPassageEntryAccess(world.Spatial, passageId);
        bool entryAllowed = atPlace.PlaceId == passage.EndpointA ? access.EnterableFromA : access.EnterableFromB;
        return string.Create(CultureInfo.InvariantCulture,
            $"The pending move through passage '{passageId.Value}' from place '{atPlace.PlaceId.Value}' is blocked: "
            + $"entry in that direction is currently {(entryAllowed ? "open" : "closed")}.");
    }

    private static OccurrenceCandidate<FreePlayCandidate>[] SingleCandidate(
        FreePlayProcessCandidateKind kind,
        CandidateKey key,
        CandidateDue due) =>
        [new(key, due, new FreePlayCandidate(null, new FreePlayProcessOccurrenceData(kind)))];

    private static CandidateKey CreateRespondKey(long controlRevision) =>
        CreateProcessKey("free-play/process/respond", controlRevision);

    private static CandidateKey CreateStartKey(long controlRevision) =>
        CreateProcessKey("free-play/process/start", controlRevision);

    private static CandidateKey CreateThinkCompleteKey(long completesAtMs) =>
        CreateProcessKey("free-play/process/think-complete", completesAtMs);

    private static CandidateKey CreateProcessKey(string kind, long discriminator) {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue(kind);
        writer.WriteNumberValue(discriminator);
        writer.WriteEndArray();
        writer.Flush();
        return CandidateKey.FromBytes(buffer.WrittenSpan);
    }

    private static string DescribeMode(ProcessMode mode) => mode.GetType().Name;
}
