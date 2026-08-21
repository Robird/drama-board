using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Plans one external Spatial command without introducing scheduler arbitration.</summary>
public sealed class SpatialCommandHandler
{
    private readonly SpatialDefinition _definition;

    /// <summary>Creates a command boundary pinned to one immutable spatial definition.</summary>
    public SpatialCommandHandler(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <summary>Plans one command and its canonical derived relation facts against one frozen state.</summary>
    public SpatialCommandPlan Handle(SpatialState preState, SpatialCommand command, ModelTime now)
    {
        ArgumentNullException.ThrowIfNull(preState);
        ArgumentNullException.ThrowIfNull(command);
        SpatialStateValidator.ValidateComplete(_definition, preState);
        SpatialCommandArguments.Validate(command.CommandId);

        var context = new HandlingContext(_definition, preState, now);
        switch (command)
        {
            case PlaceEntityCommand place:
                ProcessPlacement(place, context);
                break;
            case RemoveEntityCommand remove:
                ProcessRemoval(remove, context);
                break;
            case SetObservationEnabledCommand observation:
                ProcessObservation(observation, context);
                break;
            case AssignMoveGoalCommand assign:
                ProcessAssignment(assign, context);
                break;
            case RetargetMoveGoalCommand retarget:
                ProcessRetarget(retarget, context);
                break;
            case CancelMoveGoalCommand cancel:
                ProcessCancellation(cancel, context);
                break;
            case InterruptMovementCommand interrupt:
                ProcessInterruption(interrupt, context);
                break;
            case SetPortalStateCommand portal:
                ProcessPortal(portal, context);
                break;
            case SetCellOverrideCommand cell:
                ProcessCell(cell, context);
                break;
            case ScheduleSpatialMutationCommand schedule:
                ProcessSchedule(schedule, context);
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported Spatial command '{command.GetType().Name}'.",
                    nameof(command));
        }

        SpatialCommandResult result = context.Result
            ?? throw new InvalidOperationException("A Spatial command must produce exactly one result.");
        SpatialTransitionResult transition = SpatialTransition.Complete(
            _definition,
            preState,
            now,
            context.PrimaryFacts);
        if (!transition.ResultingState.Equals(context.WorkingState))
        {
            throw new InvalidOperationException(
                "Spatial command scratch projection diverged from the formal transition projection.");
        }

        return new SpatialCommandPlan(transition.Facts, result);
    }

    private static void ProcessPortal(SetPortalStateCommand command, HandlingContext context)
    {
        PortalDefinition? portal = context.Definition.Portals.SingleOrDefault(value => value.Id == command.PortalId);
        if (portal is null)
        {
            context.Reject(command, SpatialCommandRejectionCode.UnknownPortal);
            return;
        }

        bool? expected = context.WorkingState.PortalOverrides
            .SingleOrDefault(value => value.PortalId == command.PortalId)?.IsEnabled;
        bool? resulting = command.IsEnabled == portal.InitiallyEnabled ? null : command.IsEnabled;
        if (expected == resulting)
        {
            context.AcceptNoChange(command);
            return;
        }

        context.Apply(new PortalStateChangedEvent(command.PortalId, expected, resulting));
        context.Accept(command);
    }

    private static void ProcessCell(SetCellOverrideCommand command, HandlingContext context)
    {
        SpatialCommandRejectionCode? invalidCell = ValidateCell(context.Definition, command.Cell);
        if (invalidCell is { } cellCode)
        {
            context.Reject(command, cellCode);
            return;
        }

        if (!TryCanonicalizeCellOverride(
                context.Definition,
                command.Cell,
                command.Value,
                out CellOverride? resulting))
        {
            context.Reject(command, SpatialCommandRejectionCode.CellTraversalCostOverflow);
            return;
        }

        CellOverride? expected = context.WorkingState.CellOverrides
            .SingleOrDefault(value => value.Cell == command.Cell)?.Value;
        if (expected == resulting)
        {
            context.AcceptNoChange(command);
            return;
        }

        context.Apply(new CellStateChangedEvent(command.Cell, expected, resulting));
        context.Accept(command);
    }

    private static void ProcessSchedule(ScheduleSpatialMutationCommand command, HandlingContext context)
    {
        if (command.Due <= context.Now)
        {
            context.Reject(command, SpatialCommandRejectionCode.ScheduledMutationDueNotFuture);
            return;
        }

        if (!TryCanonicalizeMutation(
                context.Definition,
                command.Mutation,
                out ScheduledSpatialMutation? mutation,
                out SpatialCommandRejectionCode rejectionCode))
        {
            context.Reject(command, rejectionCode);
            return;
        }

        ScheduledSpatialMutationState? existing = context.WorkingState.ScheduledMutations
            .SingleOrDefault(value =>
                value.Due == command.Due && SameMutationTarget(value.Mutation, mutation!));
        if (existing is not null)
        {
            if (existing.Mutation == mutation)
            {
                context.AcceptExistingSchedule(command, existing.Id);
            }
            else
            {
                context.Reject(command, SpatialCommandRejectionCode.ScheduledMutationConflict);
            }

            return;
        }

        if (context.WorkingState.NextMutationOrdinal == long.MaxValue)
        {
            context.Reject(command, SpatialCommandRejectionCode.ScheduledMutationAllocatorExhausted);
            return;
        }

        var id = new ScheduledMutationId(context.WorkingState.NextMutationOrdinal);
        context.Apply(new MutationScheduledEvent(
            new ScheduledSpatialMutationState(id, command.Due, mutation!)));
        context.Accept(command, scheduledMutationId: id);
    }

    private static void ProcessPlacement(PlaceEntityCommand command, HandlingContext context)
    {
        if (context.WorkingState.TryGetEntity(command.EntityId, out _))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityAlreadyExists);
            return;
        }

        SpatialCommandRejectionCode? invalidCell = ValidateCell(context.Definition, command.Cell);
        if (invalidCell is { } cellCode)
        {
            context.Reject(command, cellCode);
            return;
        }

        context.Apply(new EntityPlacedEvent(new SpatialEntityState(
            command.EntityId,
            command.Cell,
            command.ObservationEnabled,
            movementGeneration: 0)));
        context.Accept(command);
    }

    private static void ProcessRemoval(RemoveEntityCommand command, HandlingContext context)
    {
        if (!context.WorkingState.TryGetEntity(command.EntityId, out SpatialEntityState? entity))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
            return;
        }

        context.WorkingState.TryGetJourney(command.EntityId, out JourneyState? journey);
        if (journey is not null && context.Now > journey.CurrentLeg.Due)
        {
            context.Reject(command, SpatialCommandRejectionCode.JourneyLegOverdue);
            return;
        }

        context.Apply(new EntityRemovedEvent(entity!.Id, entity.MovementGeneration, journey?.Id));
        context.Accept(command);
    }

    private static void ProcessObservation(SetObservationEnabledCommand command, HandlingContext context)
    {
        if (!context.WorkingState.TryGetEntity(command.EntityId, out SpatialEntityState? entity))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
            return;
        }

        if (entity!.ObservationEnabled == command.ObservationEnabled)
        {
            context.AcceptNoChange(command);
            return;
        }

        context.Apply(new ObservationStateChangedEvent(
            entity.Id,
            entity.ObservationEnabled,
            command.ObservationEnabled));
        context.Accept(command);
    }

    private static void ProcessCancellation(CancelMoveGoalCommand command, HandlingContext context)
    {
        if (!TryRequireActiveJourney(command, context, out SpatialEntityState? entity, out JourneyState? journey) ||
            !TryAdvanceGeneration(command, entity!, context, out long nextGeneration))
        {
            return;
        }

        context.Apply(new JourneyCancelledEvent(
            entity!.Id,
            journey!.Id,
            entity.MovementGeneration,
            nextGeneration));
        context.Accept(command, journeyId: journey.Id);
    }

    private static void ProcessInterruption(InterruptMovementCommand command, HandlingContext context)
    {
        if (!TryRequireActiveJourney(command, context, out SpatialEntityState? entity, out JourneyState? journey) ||
            !TryAdvanceGeneration(command, entity!, context, out long nextGeneration))
        {
            return;
        }

        context.Apply(new JourneyInterruptedEvent(
            entity!.Id,
            journey!.Id,
            entity.MovementGeneration,
            nextGeneration,
            command.Reason));
        context.Accept(command, journeyId: journey.Id);
    }

    private static void ProcessRetarget(RetargetMoveGoalCommand command, HandlingContext context)
    {
        if (!TryRequireActiveJourney(command, context, out SpatialEntityState? entity, out JourneyState? journey))
        {
            return;
        }

        SpatialCommandRejectionCode? invalidGoal = ValidateGoal(context.Definition, command.Goal);
        if (invalidGoal is { } goalCode)
        {
            context.Reject(command, goalCode);
            return;
        }

        if (!TryAdvanceGeneration(command, entity!, context, out long nextGeneration))
        {
            return;
        }

        PathSearchResult search = SpatialNavigator.FindNextStep(
            context.Definition,
            context.WorkingState,
            entity!.Cell,
            command.Goal);
        switch (search)
        {
            case PathSearchResult.Unreachable:
                context.Reject(command, SpatialCommandRejectionCode.JourneyUnreachable);
                return;
            case PathSearchResult.CostOverflow:
                context.Reject(command, SpatialCommandRejectionCode.NavigationCostOverflow);
                return;
            case PathSearchResult.AlreadySatisfied:
                context.Apply(new JourneyCompletedEvent(
                    entity.Id,
                    journey!.Id,
                    command.Goal,
                    entity.MovementGeneration,
                    nextGeneration,
                    JourneyCompletionReason.RetargetedAlreadySatisfied));
                context.Accept(command, journeyId: journey.Id);
                return;
            case PathSearchResult.NextStep next:
                if (!TryCreateLeg(next.Edge, context.Now, nextGeneration, out CurrentLeg? leg))
                {
                    context.Reject(command, SpatialCommandRejectionCode.ModelTimeOverflow);
                    return;
                }

                context.Apply(new JourneyRetargetedEvent(
                    entity.MovementGeneration,
                    new JourneyState(journey!.Id, entity.Id, command.Goal, nextGeneration, leg!)));
                context.Accept(command, journeyId: journey.Id);
                return;
        }
    }

    private static void ProcessAssignment(AssignMoveGoalCommand command, HandlingContext context)
    {
        if (!context.WorkingState.TryGetEntity(command.EntityId, out SpatialEntityState? entity))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
            return;
        }

        if (context.WorkingState.TryGetJourney(command.EntityId, out _))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityHasActiveJourney);
            return;
        }

        SpatialCommandRejectionCode? invalidGoal = ValidateGoal(context.Definition, command.Goal);
        if (invalidGoal is { } goalCode)
        {
            context.Reject(command, goalCode);
            return;
        }

        if (entity!.MovementGeneration == long.MaxValue)
        {
            context.Reject(command, SpatialCommandRejectionCode.MovementGenerationOverflow);
            return;
        }

        if (context.WorkingState.NextJourneyOrdinal == long.MaxValue)
        {
            context.Reject(command, SpatialCommandRejectionCode.JourneyAllocatorExhausted);
            return;
        }

        long generation = entity.MovementGeneration + 1;
        var journeyId = new JourneyId(context.WorkingState.NextJourneyOrdinal);
        PathSearchResult search = SpatialNavigator.FindNextStep(
            context.Definition,
            context.WorkingState,
            entity.Cell,
            command.Goal);
        switch (search)
        {
            case PathSearchResult.Unreachable:
                context.Reject(command, SpatialCommandRejectionCode.JourneyUnreachable);
                return;
            case PathSearchResult.CostOverflow:
                context.Reject(command, SpatialCommandRejectionCode.NavigationCostOverflow);
                return;
            case PathSearchResult.AlreadySatisfied:
                context.Apply(new JourneyCompletedEvent(
                    entity.Id,
                    journeyId,
                    command.Goal,
                    entity.MovementGeneration,
                    generation,
                    JourneyCompletionReason.AssignedAlreadySatisfied));
                context.Accept(command, journeyId: journeyId);
                return;
            case PathSearchResult.NextStep next:
                if (!TryCreateLeg(next.Edge, context.Now, generation, out CurrentLeg? leg))
                {
                    context.Reject(command, SpatialCommandRejectionCode.ModelTimeOverflow);
                    return;
                }

                context.Apply(new JourneyStartedEvent(new JourneyState(
                    journeyId,
                    entity.Id,
                    command.Goal,
                    generation,
                    leg!)));
                context.Accept(command, journeyId: journeyId);
                return;
        }
    }

    private static bool TryRequireActiveJourney(
        SpatialCommand command,
        HandlingContext context,
        out SpatialEntityState? entity,
        out JourneyState? journey)
    {
        EntityId entityId = GetEntityId(command);
        if (!context.WorkingState.TryGetEntity(entityId, out entity))
        {
            journey = null;
            context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
            return false;
        }

        if (!context.WorkingState.TryGetJourney(entityId, out journey))
        {
            context.Reject(command, SpatialCommandRejectionCode.EntityHasNoActiveJourney);
            return false;
        }

        if (context.Now > journey!.CurrentLeg.Due)
        {
            context.Reject(command, SpatialCommandRejectionCode.JourneyLegOverdue);
            return false;
        }

        return true;
    }

    private static bool TryAdvanceGeneration(
        SpatialCommand command,
        SpatialEntityState entity,
        HandlingContext context,
        out long nextGeneration)
    {
        if (entity.MovementGeneration == long.MaxValue)
        {
            nextGeneration = default;
            context.Reject(command, SpatialCommandRejectionCode.MovementGenerationOverflow);
            return false;
        }

        nextGeneration = entity.MovementGeneration + 1;
        return true;
    }

    private static bool TryCreateLeg(
        NavigationEdge edge,
        ModelTime now,
        long generation,
        out CurrentLeg? leg)
    {
        try
        {
            leg = new CurrentLeg(
                edge.From,
                edge.To,
                edge.EdgeKind,
                edge.PortalId,
                now,
                now + edge.Duration,
                generation);
            return true;
        }
        catch (OverflowException)
        {
            leg = null;
            return false;
        }
    }

    private static EntityId GetEntityId(SpatialCommand command) => command switch
    {
        RetargetMoveGoalCommand value => value.EntityId,
        CancelMoveGoalCommand value => value.EntityId,
        InterruptMovementCommand value => value.EntityId,
        _ => throw new ArgumentException($"Command '{command.GetType().Name}' is not journey-scoped.", nameof(command)),
    };

    private static SpatialCommandRejectionCode? ValidateCell(
        SpatialDefinition definition,
        CellRef cell)
    {
        GridMapDefinition? map = definition.Maps.SingleOrDefault(value => value.Id == cell.MapId);
        if (map is null)
        {
            return SpatialCommandRejectionCode.UnknownMap;
        }

        return cell.X >= map.Width || cell.Y >= map.Height
            ? SpatialCommandRejectionCode.CellOutOfBounds
            : null;
    }

    private static SpatialCommandRejectionCode? ValidateGoal(
        SpatialDefinition definition,
        MoveGoal goal) => goal switch
        {
            CellGoal cell => ValidateCell(definition, cell.Cell),
            AnchorGoal anchor when definition.Anchors.Any(value => value.Id == anchor.AnchorId) => null,
            AnchorGoal => SpatialCommandRejectionCode.UnknownAnchor,
            ZoneGoal zone when definition.Zones.Any(value => value.Id == zone.ZoneId) => null,
            ZoneGoal => SpatialCommandRejectionCode.UnknownZone,
            _ => throw new ArgumentException($"Unsupported MoveGoal '{goal.GetType().Name}'.", nameof(goal)),
        };

    private static bool TryCanonicalizeCellOverride(
        SpatialDefinition definition,
        CellRef cell,
        CellOverride? requested,
        out CellOverride? canonical)
    {
        if (requested is null)
        {
            canonical = null;
            return true;
        }

        CellDefinition cellDefinition = definition.GetCell(cell);
        bool? blocksMovement = requested.BlocksMovement is bool movement &&
            movement != cellDefinition.BlocksMovement
                ? movement
                : null;
        bool? blocksSight = requested.BlocksSight is bool sight && sight != cellDefinition.BlocksSight
            ? sight
            : null;
        int? moveCost = requested.MoveCost is int cost && cost != cellDefinition.MoveCost ? cost : null;
        int effectiveMoveCost = moveCost ?? cellDefinition.MoveCost;
        try
        {
            _ = checked(definition.GetMap(cell.MapId).OrthogonalStepDuration.Ticks * effectiveMoveCost);
        }
        catch (OverflowException)
        {
            canonical = null;
            return false;
        }

        canonical = blocksMovement is null && blocksSight is null && moveCost is null
            ? null
            : new CellOverride(blocksMovement, blocksSight, moveCost);
        return true;
    }

    private static bool TryCanonicalizeMutation(
        SpatialDefinition definition,
        ScheduledSpatialMutation requested,
        out ScheduledSpatialMutation? canonical,
        out SpatialCommandRejectionCode rejectionCode)
    {
        switch (requested)
        {
            case SetPortalStateMutation portal:
                if (!definition.Portals.Any(value => value.Id == portal.PortalId))
                {
                    canonical = null;
                    rejectionCode = SpatialCommandRejectionCode.UnknownPortal;
                    return false;
                }

                canonical = portal;
                rejectionCode = SpatialCommandRejectionCode.None;
                return true;
            case SetCellOverrideMutation cell:
                SpatialCommandRejectionCode? invalidCell = ValidateCell(definition, cell.Cell);
                if (invalidCell is { } cellCode)
                {
                    canonical = null;
                    rejectionCode = cellCode;
                    return false;
                }

                if (!TryCanonicalizeCellOverride(definition, cell.Cell, cell.Value, out CellOverride? value))
                {
                    canonical = null;
                    rejectionCode = SpatialCommandRejectionCode.CellTraversalCostOverflow;
                    return false;
                }

                canonical = new SetCellOverrideMutation(cell.Cell, value);
                rejectionCode = SpatialCommandRejectionCode.None;
                return true;
            default:
                throw new ArgumentException(
                    $"Unsupported scheduled mutation '{requested.GetType().Name}'.",
                    nameof(requested));
        }
    }

    private static bool SameMutationTarget(
        ScheduledSpatialMutation left,
        ScheduledSpatialMutation right) => (left, right) switch
        {
            (SetPortalStateMutation first, SetPortalStateMutation second) =>
                first.PortalId == second.PortalId,
            (SetCellOverrideMutation first, SetCellOverrideMutation second) => first.Cell == second.Cell,
            _ => false,
        };

    private sealed class HandlingContext
    {
        public HandlingContext(SpatialDefinition definition, SpatialState preState, ModelTime now)
        {
            Definition = definition;
            WorkingState = preState;
            Now = now;
        }

        public SpatialDefinition Definition { get; }

        public ModelTime Now { get; }

        public SpatialState WorkingState { get; private set; }

        public List<SpatialEvent> PrimaryFacts { get; } = [];

        public SpatialCommandResult? Result { get; private set; }

        public void Apply(SpatialEvent fact)
        {
            WorkingState = SpatialProjector.Apply(Definition, WorkingState, fact, Now);
            PrimaryFacts.Add(fact);
        }

        public void Accept(
            SpatialCommand command,
            JourneyId? journeyId = null,
            ScheduledMutationId? scheduledMutationId = null) =>
            Complete(new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.Accepted,
                journeyId: journeyId,
                scheduledMutationId: scheduledMutationId));

        public void AcceptNoChange(SpatialCommand command) =>
            Complete(new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.AcceptedNoChange));

        public void AcceptExistingSchedule(
            SpatialCommand command,
            ScheduledMutationId scheduledMutationId) =>
            Complete(new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.AcceptedNoChange,
                scheduledMutationId: scheduledMutationId));

        public void Reject(SpatialCommand command, SpatialCommandRejectionCode rejectionCode) =>
            Complete(new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.Rejected,
                rejectionCode));

        private void Complete(SpatialCommandResult result)
        {
            if (Result is not null)
            {
                throw new InvalidOperationException("A Spatial command cannot produce multiple results.");
            }

            Result = result;
        }
    }
}
