using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Normalizes and plans one simultaneous batch of immediate spatial intents.</summary>
public sealed class SpatialCommandHandler
{
    private readonly SpatialDefinition _definition;

    /// <summary>Creates a command boundary pinned to one immutable spatial definition.</summary>
    public SpatialCommandHandler(SpatialDefinition definition)
    {
        SpatialRules.EnsureSupported(definition);
        _definition = definition;
    }

    /// <summary>
    /// Plans accepted primary events and one final relationship diff without mutating authoritative state.
    /// </summary>
    public SpatialCommandBatchResult HandleBatch(
        SpatialState preState,
        IReadOnlyList<SpatialCommand> simultaneousCommands,
        ModelTime now)
    {
        ArgumentNullException.ThrowIfNull(preState);
        ArgumentNullException.ThrowIfNull(simultaneousCommands);
        SpatialStateValidator.ValidateComplete(_definition, preState);

        SpatialCommand[] commands = [.. simultaneousCommands];
        if (commands.Any(command => command is null))
        {
            throw new ArgumentException("A spatial command batch cannot contain null commands.", nameof(simultaneousCommands));
        }

        foreach (SpatialCommand command in commands)
        {
            SpatialCommandArguments.Validate(command.CommandId);
        }

        commands = [.. commands.OrderBy(command => command.CommandId)];
        for (int index = 1; index < commands.Length; index++)
        {
            if (commands[index - 1].CommandId == commands[index].CommandId)
            {
                throw new ArgumentException(
                    $"Spatial CommandId '{commands[index].CommandId}' is duplicated.",
                    nameof(simultaneousCommands));
            }
        }

        EnsureKnownCommandTypes(commands, nameof(simultaneousCommands));
        var context = new HandlingContext(_definition, preState, now);
        Dictionary<SpatialCommandId, SpatialCommandId> aliases = Preflight(
            _definition,
            commands,
            context.Results);

        ProcessImmediateTopology(commands, aliases, context);
        ProcessSchedules(commands, aliases, context);
        ProcessPlacementsAndRemovals(commands, aliases, context);
        ProcessObservation(commands, aliases, context);
        ProcessMovementChanges(commands, aliases, context);
        ProcessAssignments(commands, aliases, context);
        CompleteAliasResults(aliases, context.Results);

        if (context.Results.Count != commands.Length)
        {
            throw new InvalidOperationException("Every spatial command must produce exactly one result.");
        }

        SpatialTransitionResult transition = SpatialTransition.Complete(
            _definition,
            preState,
            now,
            context.PrimaryEvents);
        if (!transition.ResultingState.Equals(context.WorkingState))
        {
            throw new InvalidOperationException(
                "Spatial command scratch projection diverged from the formal transition projection.");
        }

        SpatialCommandResult[] orderedResults =
        [
            .. commands.Select(command => context.Results[command.CommandId]),
        ];
        return new SpatialCommandBatchResult(transition.Events, orderedResults);
    }

    private static Dictionary<SpatialCommandId, SpatialCommandId> Preflight(
        SpatialDefinition definition,
        IReadOnlyList<SpatialCommand> commands,
        IDictionary<SpatialCommandId, SpatialCommandResult> results)
    {
        var aliases = new Dictionary<SpatialCommandId, SpatialCommandId>();

        foreach (IGrouping<EntityId, SpatialCommand> group in commands
                     .Where(IsEntityCommand)
                     .GroupBy(GetEntityId))
        {
            SpatialCommand[] values = [.. group];
            if (values.Any(command => command is RemoveEntityCommand) && values.Length > 1)
            {
                RejectConflict(values, results);
                continue;
            }

            SpatialCommand[] placements = [.. values.Where(command => command is PlaceEntityCommand)];
            if (placements.Length > 1)
            {
                RejectConflict(placements, results);
            }

            SpatialCommand[] movement = [.. values.Where(IsMovementCommand)];
            if (movement.Length > 1)
            {
                RejectConflict(movement, results);
            }
        }

        AliasOrConflict(
            commands.OfType<SetPortalStateCommand>(),
            command => command.PortalId,
            (left, right) => left.IsEnabled == right.IsEnabled,
            results,
            aliases);
        AliasOrConflict(
            commands.OfType<SetCellOverrideCommand>(),
            command => command.Cell,
            (left, right) => CanonicalCellOverrideEquals(
                definition,
                left.Cell,
                left.Value,
                right.Value),
            results,
            aliases);
        AliasOrConflict(
            commands.OfType<SetObservationEnabledCommand>(),
            command => command.EntityId,
            (left, right) => left.ObservationEnabled == right.ObservationEnabled,
            results,
            aliases);
        AliasOrConflict(
            commands.OfType<ScheduleSpatialMutationCommand>(),
            GetScheduleTarget,
            (left, right) => CanonicalMutationValueEquals(
                definition,
                left.Mutation,
                right.Mutation),
            results,
            aliases);

        return aliases;
    }

    private static void AliasOrConflict<TCommand, TKey>(
        IEnumerable<TCommand> commands,
        Func<TCommand, TKey> selectKey,
        Func<TCommand, TCommand, bool> valuesEqual,
        IDictionary<SpatialCommandId, SpatialCommandResult> results,
        IDictionary<SpatialCommandId, SpatialCommandId> aliases)
        where TCommand : SpatialCommand
        where TKey : notnull
    {
        foreach (IGrouping<TKey, TCommand> group in commands
                     .Where(command => !results.ContainsKey(command.CommandId))
                     .GroupBy(selectKey))
        {
            TCommand[] values = [.. group.OrderBy(command => command.CommandId)];
            if (values.Length < 2)
            {
                continue;
            }

            TCommand canonical = values[0];
            if (values.Skip(1).Any(command => !valuesEqual(canonical, command)))
            {
                RejectConflict(values, results);
                continue;
            }

            foreach (TCommand alias in values.Skip(1))
            {
                aliases.Add(alias.CommandId, canonical.CommandId);
            }
        }
    }

    private static void ProcessImmediateTopology(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        foreach (SpatialCommand command in Available(commands, aliases, context.Results)
                     .Where(command => command is SetPortalStateCommand or SetCellOverrideCommand))
        {
            switch (command)
            {
                case SetPortalStateCommand portal:
                    ProcessPortal(portal, context);
                    break;
                case SetCellOverrideCommand cell:
                    ProcessCell(cell, context);
                    break;
            }
        }
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

    private static void ProcessSchedules(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        var newSchedules = new List<SchedulePlan>();
        foreach (ScheduleSpatialMutationCommand command in Available(commands, aliases, context.Results)
                     .OfType<ScheduleSpatialMutationCommand>())
        {
            if (command.Due <= context.Now)
            {
                context.Reject(command, SpatialCommandRejectionCode.ScheduledMutationDueNotFuture);
                continue;
            }

            if (!TryCanonicalizeMutation(
                    context.Definition,
                    command.Mutation,
                    out ScheduledSpatialMutation? mutation,
                    out SpatialCommandRejectionCode rejectionCode))
            {
                context.Reject(command, rejectionCode);
                continue;
            }

            ScheduledSpatialMutationState? existing = context.WorkingState.ScheduledMutations
                .SingleOrDefault(value =>
                    value.Due == command.Due && SameMutationTarget(value.Mutation, mutation!));
            if (existing is not null)
            {
                if (existing.Mutation == mutation)
                {
                    context.AcceptExistingScheduleAlias(command, existing.Id);
                }
                else
                {
                    context.Reject(command, SpatialCommandRejectionCode.ScheduledMutationConflict);
                }

                continue;
            }

            newSchedules.Add(new SchedulePlan(command, mutation!));
        }

        if (!CanAllocate(context.WorkingState.NextMutationOrdinal, newSchedules.Count))
        {
            foreach (SchedulePlan plan in newSchedules)
            {
                context.Reject(plan.Command, SpatialCommandRejectionCode.ScheduledMutationAllocatorExhausted);
            }

            return;
        }

        foreach (SchedulePlan plan in newSchedules)
        {
            var id = new ScheduledMutationId(context.WorkingState.NextMutationOrdinal);
            var state = new ScheduledSpatialMutationState(id, plan.Command.Due, plan.Mutation);
            context.Apply(new MutationScheduledEvent(state));
            context.Accept(plan.Command, scheduledMutationId: id);
        }
    }

    private static void ProcessPlacementsAndRemovals(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        foreach (SpatialCommand command in Available(commands, aliases, context.Results)
                     .Where(command => command is PlaceEntityCommand or RemoveEntityCommand))
        {
            switch (command)
            {
                case PlaceEntityCommand place:
                    ProcessPlacement(place, context);
                    break;
                case RemoveEntityCommand remove:
                    ProcessRemoval(remove, context);
                    break;
            }
        }
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

    private static void ProcessObservation(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        foreach (SetObservationEnabledCommand command in Available(commands, aliases, context.Results)
                     .OfType<SetObservationEnabledCommand>())
        {
            if (!context.WorkingState.TryGetEntity(command.EntityId, out SpatialEntityState? entity))
            {
                context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
                continue;
            }

            if (entity!.ObservationEnabled == command.ObservationEnabled)
            {
                context.AcceptNoChange(command);
                continue;
            }

            context.Apply(new ObservationStateChangedEvent(
                entity.Id,
                entity.ObservationEnabled,
                command.ObservationEnabled));
            context.Accept(command);
        }
    }

    private static void ProcessMovementChanges(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        foreach (SpatialCommand command in Available(commands, aliases, context.Results)
                     .Where(command => command is RetargetMoveGoalCommand or
                         CancelMoveGoalCommand or InterruptMovementCommand))
        {
            switch (command)
            {
                case RetargetMoveGoalCommand retarget:
                    ProcessRetarget(retarget, context);
                    break;
                case CancelMoveGoalCommand cancel:
                    ProcessCancellation(cancel, context);
                    break;
                case InterruptMovementCommand interrupt:
                    ProcessInterruption(interrupt, context);
                    break;
            }
        }
    }

    private static void ProcessCancellation(CancelMoveGoalCommand command, HandlingContext context)
    {
        if (!TryRequireActiveJourney(command, context, out SpatialEntityState? entity, out JourneyState? journey))
        {
            return;
        }

        if (!TryAdvanceGeneration(command, entity!, context, out long nextGeneration))
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
        if (!TryRequireActiveJourney(command, context, out SpatialEntityState? entity, out JourneyState? journey))
        {
            return;
        }

        if (!TryAdvanceGeneration(command, entity!, context, out long nextGeneration))
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

                var result = new JourneyState(
                    journey!.Id,
                    entity.Id,
                    command.Goal,
                    nextGeneration,
                    leg!);
                context.Apply(new JourneyRetargetedEvent(entity.MovementGeneration, result));
                context.Accept(command, journeyId: journey.Id);
                return;
        }
    }

    private static void ProcessAssignments(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        HandlingContext context)
    {
        var plans = new List<AssignmentPlan>();
        foreach (AssignMoveGoalCommand command in Available(commands, aliases, context.Results)
                     .OfType<AssignMoveGoalCommand>())
        {
            if (!context.WorkingState.TryGetEntity(command.EntityId, out SpatialEntityState? entity))
            {
                context.Reject(command, SpatialCommandRejectionCode.EntityNotFound);
                continue;
            }

            if (context.WorkingState.TryGetJourney(command.EntityId, out _))
            {
                context.Reject(command, SpatialCommandRejectionCode.EntityHasActiveJourney);
                continue;
            }

            SpatialCommandRejectionCode? invalidGoal = ValidateGoal(context.Definition, command.Goal);
            if (invalidGoal is { } goalCode)
            {
                context.Reject(command, goalCode);
                continue;
            }

            if (entity!.MovementGeneration == long.MaxValue)
            {
                context.Reject(command, SpatialCommandRejectionCode.MovementGenerationOverflow);
                continue;
            }

            long generation = entity.MovementGeneration + 1;
            PathSearchResult search = SpatialNavigator.FindNextStep(
                context.Definition,
                context.WorkingState,
                entity.Cell,
                command.Goal);
            switch (search)
            {
                case PathSearchResult.Unreachable:
                    context.Reject(command, SpatialCommandRejectionCode.JourneyUnreachable);
                    break;
                case PathSearchResult.CostOverflow:
                    context.Reject(command, SpatialCommandRejectionCode.NavigationCostOverflow);
                    break;
                case PathSearchResult.AlreadySatisfied:
                    plans.Add(new AssignmentPlan(command, entity, generation, Leg: null));
                    break;
                case PathSearchResult.NextStep next:
                    if (!TryCreateLeg(next.Edge, context.Now, generation, out CurrentLeg? leg))
                    {
                        context.Reject(command, SpatialCommandRejectionCode.ModelTimeOverflow);
                        break;
                    }

                    plans.Add(new AssignmentPlan(command, entity, generation, leg));
                    break;
            }
        }

        if (!CanAllocate(context.WorkingState.NextJourneyOrdinal, plans.Count))
        {
            foreach (AssignmentPlan plan in plans)
            {
                context.Reject(plan.Command, SpatialCommandRejectionCode.JourneyAllocatorExhausted);
            }

            return;
        }

        foreach (AssignmentPlan plan in plans)
        {
            var journeyId = new JourneyId(context.WorkingState.NextJourneyOrdinal);
            if (plan.Leg is null)
            {
                context.Apply(new JourneyCompletedEvent(
                    plan.Entity.Id,
                    journeyId,
                    plan.Command.Goal,
                    plan.Entity.MovementGeneration,
                    plan.Generation,
                    JourneyCompletionReason.AssignedAlreadySatisfied));
            }
            else
            {
                context.Apply(new JourneyStartedEvent(new JourneyState(
                    journeyId,
                    plan.Entity.Id,
                    plan.Command.Goal,
                    plan.Generation,
                    plan.Leg)));
            }

            context.Accept(plan.Command, journeyId: journeyId);
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
            ModelTime due = now + edge.Duration;
            leg = new CurrentLeg(
                edge.From,
                edge.To,
                edge.EdgeKind,
                edge.PortalId,
                now,
                due,
                generation);
            return true;
        }
        catch (OverflowException)
        {
            leg = null;
            return false;
        }
    }

    private static void CompleteAliasResults(
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        IDictionary<SpatialCommandId, SpatialCommandResult> results)
    {
        foreach ((SpatialCommandId aliasId, SpatialCommandId canonicalId) in aliases.OrderBy(pair => pair.Key))
        {
            SpatialCommandResult canonical = results[canonicalId];
            results.Add(
                aliasId,
                canonical.Disposition == SpatialCommandDisposition.Rejected
                    ? new SpatialCommandResult(aliasId, SpatialCommandDisposition.Rejected, canonical.RejectionCode)
                    : new SpatialCommandResult(
                        aliasId,
                        SpatialCommandDisposition.AcceptedAlias,
                        aliasOfCommandId: canonicalId,
                        scheduledMutationId: canonical.ScheduledMutationId));
        }
    }

    private static IEnumerable<SpatialCommand> Available(
        IEnumerable<SpatialCommand> commands,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandId> aliases,
        IReadOnlyDictionary<SpatialCommandId, SpatialCommandResult> results) =>
        commands.Where(command =>
            !aliases.ContainsKey(command.CommandId) && !results.ContainsKey(command.CommandId));

    private static bool IsEntityCommand(SpatialCommand command) => command is
        PlaceEntityCommand or RemoveEntityCommand or SetObservationEnabledCommand or
        AssignMoveGoalCommand or RetargetMoveGoalCommand or CancelMoveGoalCommand or
        InterruptMovementCommand;

    private static bool IsMovementCommand(SpatialCommand command) => command is
        AssignMoveGoalCommand or RetargetMoveGoalCommand or CancelMoveGoalCommand or
        InterruptMovementCommand;

    private static EntityId GetEntityId(SpatialCommand command) => command switch
    {
        PlaceEntityCommand value => value.EntityId,
        RemoveEntityCommand value => value.EntityId,
        SetObservationEnabledCommand value => value.EntityId,
        AssignMoveGoalCommand value => value.EntityId,
        RetargetMoveGoalCommand value => value.EntityId,
        CancelMoveGoalCommand value => value.EntityId,
        InterruptMovementCommand value => value.EntityId,
        _ => throw new ArgumentException($"Command '{command.GetType().Name}' is not entity-scoped.", nameof(command)),
    };

    private static void RejectConflict(
        IEnumerable<SpatialCommand> commands,
        IDictionary<SpatialCommandId, SpatialCommandResult> results)
    {
        foreach (SpatialCommand command in commands)
        {
            results[command.CommandId] = new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.Rejected,
                SpatialCommandRejectionCode.ConflictingCommands);
        }
    }

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

    private static bool CanAllocate(long nextOrdinal, int count) =>
        (UInt128)(ulong)nextOrdinal + (uint)count <= (UInt128)long.MaxValue;

    private static ScheduleTarget GetScheduleTarget(ScheduleSpatialMutationCommand command) =>
        command.Mutation switch
        {
            SetPortalStateMutation portal => new ScheduleTarget(
                command.Due,
                Kind: 0,
                portal.PortalId,
                Cell: default),
            SetCellOverrideMutation cell => new ScheduleTarget(
                command.Due,
                Kind: 1,
                Portal: default,
                cell.Cell),
            _ => throw new ArgumentException(
                $"Unsupported scheduled mutation '{command.Mutation.GetType().Name}'.",
                nameof(command)),
        };

    private static bool CanonicalMutationValueEquals(
        SpatialDefinition definition,
        ScheduledSpatialMutation left,
        ScheduledSpatialMutation right) => (left, right) switch
        {
            (SetPortalStateMutation first, SetPortalStateMutation second) =>
                first.IsEnabled == second.IsEnabled,
            (SetCellOverrideMutation first, SetCellOverrideMutation second) =>
                CanonicalCellOverrideEquals(definition, first.Cell, first.Value, second.Value),
            _ => false,
        };

    private static bool SameMutationTarget(
        ScheduledSpatialMutation left,
        ScheduledSpatialMutation right) => (left, right) switch
        {
            (SetPortalStateMutation first, SetPortalStateMutation second) =>
                first.PortalId == second.PortalId,
            (SetCellOverrideMutation first, SetCellOverrideMutation second) => first.Cell == second.Cell,
            _ => false,
        };

    private static bool CellOverrideEquals(CellOverride? left, CellOverride? right) => left == right;

    private static bool CanonicalCellOverrideEquals(
        SpatialDefinition definition,
        CellRef cell,
        CellOverride? left,
        CellOverride? right)
    {
        if (ValidateCell(definition, cell) is not null ||
            !TryCanonicalizeCellOverride(definition, cell, left, out CellOverride? canonicalLeft) ||
            !TryCanonicalizeCellOverride(definition, cell, right, out CellOverride? canonicalRight))
        {
            return CellOverrideEquals(left, right);
        }

        return CellOverrideEquals(canonicalLeft, canonicalRight);
    }

    private static void EnsureKnownCommandTypes(
        IEnumerable<SpatialCommand> commands,
        string parameterName)
    {
        SpatialCommand? unknown = commands.FirstOrDefault(command => command is not (
            PlaceEntityCommand or RemoveEntityCommand or SetObservationEnabledCommand or
            AssignMoveGoalCommand or RetargetMoveGoalCommand or CancelMoveGoalCommand or
            SetPortalStateCommand or SetCellOverrideCommand or ScheduleSpatialMutationCommand or
            InterruptMovementCommand));
        if (unknown is not null)
        {
            throw new ArgumentException(
                $"Unsupported Spatial command '{unknown.GetType().Name}'.",
                parameterName);
        }
    }

    private readonly record struct ScheduleTarget(
        ModelTime Due,
        int Kind,
        PortalId Portal,
        CellRef Cell);

    private sealed record SchedulePlan(
        ScheduleSpatialMutationCommand Command,
        ScheduledSpatialMutation Mutation);

    private sealed record AssignmentPlan(
        AssignMoveGoalCommand Command,
        SpatialEntityState Entity,
        long Generation,
        CurrentLeg? Leg);

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

        public List<SpatialEvent> PrimaryEvents { get; } = [];

        public Dictionary<SpatialCommandId, SpatialCommandResult> Results { get; } = [];

        public void Apply(SpatialEvent payload)
        {
            WorkingState = SpatialProjector.Apply(
                Definition,
                WorkingState,
                SpatialEventKinds.For(payload),
                payload,
                Now);
            PrimaryEvents.Add(payload);
        }

        public void Accept(
            SpatialCommand command,
            JourneyId? journeyId = null,
            ScheduledMutationId? scheduledMutationId = null) =>
            Results.Add(command.CommandId, new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.Accepted,
                journeyId: journeyId,
                scheduledMutationId: scheduledMutationId));

        public void AcceptNoChange(SpatialCommand command) =>
            Results.Add(command.CommandId, new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.AcceptedNoChange));

        public void AcceptExistingScheduleAlias(
            SpatialCommand command,
            ScheduledMutationId scheduledMutationId) =>
            Results.Add(command.CommandId, new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.AcceptedAlias,
                scheduledMutationId: scheduledMutationId));

        public void Reject(SpatialCommand command, SpatialCommandRejectionCode rejectionCode) =>
            Results.Add(command.CommandId, new SpatialCommandResult(
                command.CommandId,
                SpatialCommandDisposition.Rejected,
                rejectionCode));
    }
}
