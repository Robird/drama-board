namespace DramaBoard.Spatial;

/// <summary>Validates invariants required at complete commit, replay, and fork boundaries.</summary>
public static class SpatialStateValidator
{
    /// <summary>Throws when state is not a complete canonical projection for its definition.</summary>
    public static void ValidateComplete(SpatialDefinition definition, SpatialState state)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ValidateStamp(definition, state);
        ValidateCanonicalIds(state.Entities.Select(entity => entity.Id), "entity");
        ValidateCanonicalIds(state.Journeys.Select(journey => journey.EntityId), "journey entity");
        ValidateUniqueIds(state.Journeys.Select(journey => journey.Id), "journey");
        ValidateCanonicalIds(state.PortalOverrides.Select(value => value.PortalId), "portal override");
        ValidateCanonicalIds(state.CellOverrides.Select(value => value.Cell), "cell override");

        foreach (SpatialEntityState entity in state.Entities)
        {
            EnsureEntityId(entity.Id, "Entity state");
            EnsureCellExists(definition, entity.Cell, $"Entity '{entity.Id}'");
        }

        foreach (JourneyState journey in state.Journeys)
        {
            EnsureJourneyId(journey.Id, "Journey state");
            EnsureEntityId(journey.EntityId, "Journey state");
            SpatialEntityState entity = RequireEntity(state, journey.EntityId);
            if (journey.Id.Value >= state.NextJourneyOrdinal)
            {
                throw new InvalidOperationException($"Journey '{journey.Id}' was not consumed by the allocator.");
            }

            if (journey.Generation != entity.MovementGeneration)
            {
                throw new InvalidOperationException($"Journey '{journey.Id}' generation does not match its entity.");
            }

            CurrentLeg leg = journey.CurrentLeg;
            if (leg.JourneyGeneration != journey.Generation || leg.From != entity.Cell)
            {
                throw new InvalidOperationException($"Journey '{journey.Id}' current leg is not anchored at its entity.");
            }

            ValidateGoal(definition, journey.Goal);
            if (IsGoalSatisfied(definition, entity.Cell, journey.Goal))
            {
                throw new InvalidOperationException(
                    $"Journey '{journey.Id}' remains active although its goal is already satisfied.");
            }

            ValidateLeg(definition, leg);
        }

        foreach (PortalOverrideState value in state.PortalOverrides)
        {
            PortalDefinition portal = RequirePortal(definition, value.PortalId);
            if (value.IsEnabled == portal.InitiallyEnabled)
            {
                throw new InvalidOperationException($"Portal override '{value.PortalId}' redundantly stores its default.");
            }
        }

        foreach (CellOverrideState value in state.CellOverrides)
        {
            EnsureCellExists(definition, value.Cell, "Cell override");
            ValidateCellOverride(definition, value.Cell, value.Value);
        }

        ValidateCanonicalMutationOrder(state.ScheduledMutations);
        ValidateUniqueMutationTargets(state.ScheduledMutations);
        var mutationIds = new HashSet<ScheduledMutationId>();
        foreach (ScheduledSpatialMutationState mutation in state.ScheduledMutations)
        {
            EnsureMutationId(mutation.Id, "Scheduled mutation state");
            if (!mutationIds.Add(mutation.Id))
            {
                throw new InvalidOperationException($"Scheduled mutation '{mutation.Id}' is duplicated.");
            }

            if (mutation.Id.Value >= state.NextMutationOrdinal)
            {
                throw new InvalidOperationException($"Scheduled mutation '{mutation.Id}' was not consumed by the allocator.");
            }

            ValidateMutation(definition, mutation.Mutation);
        }
    }

    internal static void ValidateStamp(SpatialDefinition definition, SpatialState state)
    {
        SpatialDefinitionStamp expected = SpatialDefinitionStamp.From(definition);
        if (state.Definition != expected)
        {
            throw new InvalidOperationException(
                $"Spatial state stamp '{state.Definition}' does not match definition stamp '{expected}'.");
        }
    }

    internal static SpatialEntityState RequireEntity(SpatialState state, EntityId entityId) =>
        state.TryGetEntity(entityId, out SpatialEntityState? entity)
            ? entity!
            : throw new InvalidOperationException($"Spatial entity '{entityId}' does not exist.");

    internal static JourneyState RequireJourney(SpatialState state, EntityId entityId, JourneyId journeyId)
    {
        if (!state.TryGetJourney(entityId, out JourneyState? journey) || journey is null || journey.Id != journeyId)
        {
            throw new InvalidOperationException(
                $"Entity '{entityId}' does not have active journey '{journeyId}'.");
        }

        return journey;
    }

    internal static PortalDefinition RequirePortal(SpatialDefinition definition, PortalId portalId) =>
        definition.Portals.SingleOrDefault(portal => portal.Id == portalId)
        ?? throw new InvalidOperationException($"Portal '{portalId}' does not exist.");

    internal static void EnsureCellExists(SpatialDefinition definition, CellRef cell, string description)
    {
        if (!definition.Contains(cell))
        {
            throw new InvalidOperationException($"{description} references undefined cell '{cell}'.");
        }
    }

    internal static void ValidateGoal(SpatialDefinition definition, MoveGoal goal)
    {
        switch (goal)
        {
            case CellGoal cellGoal:
                EnsureCellExists(definition, cellGoal.Cell, "Cell goal");
                break;
            case AnchorGoal anchorGoal when definition.Anchors.Any(anchor => anchor.Id == anchorGoal.AnchorId):
                break;
            case ZoneGoal zoneGoal when definition.Zones.Any(zone => zone.Id == zoneGoal.ZoneId):
                break;
            case AnchorGoal anchorGoal:
                throw new InvalidOperationException($"Anchor goal '{anchorGoal.AnchorId}' does not exist.");
            case ZoneGoal zoneGoal:
                throw new InvalidOperationException($"Zone goal '{zoneGoal.ZoneId}' does not exist.");
            default:
                throw new InvalidOperationException($"Unsupported movement goal '{goal.GetType().Name}'.");
        }
    }

    internal static void ValidateLeg(SpatialDefinition definition, CurrentLeg leg)
    {
        EnsureCellExists(definition, leg.From, "Current leg source");
        EnsureCellExists(definition, leg.To, "Current leg destination");
        if (leg.EdgeKind == SpatialEdgeKind.Orthogonal)
        {
            long manhattan =
                Math.Abs((long)leg.From.X - leg.To.X) +
                Math.Abs((long)leg.From.Y - leg.To.Y);
            if (leg.From.MapId != leg.To.MapId || manhattan != 1 || leg.PortalId is not null)
            {
                throw new InvalidOperationException("Orthogonal current leg must join adjacent cells in one map.");
            }

            return;
        }

        PortalId portalId = leg.PortalId ?? throw new InvalidOperationException(
            "Portal current leg requires a portal identifier.");
        PortalDefinition portal = RequirePortal(definition, portalId);
        if (portal.From != leg.From || portal.To != leg.To)
        {
            throw new InvalidOperationException($"Current leg does not match directed portal '{portalId}'.");
        }
    }

    internal static void ValidateMutation(SpatialDefinition definition, ScheduledSpatialMutation mutation)
    {
        switch (mutation)
        {
            case SetPortalStateMutation portal:
                RequirePortal(definition, portal.PortalId);
                break;
            case SetCellOverrideMutation cell:
                EnsureCellExists(definition, cell.Cell, "Scheduled cell mutation");
                if (cell.Value is not null)
                {
                    ValidateCellOverride(definition, cell.Cell, cell.Value);
                }

                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported scheduled mutation '{mutation.GetType().Name}'.");
        }
    }

    internal static void EnsureEntityId(EntityId entityId, string description)
    {
        if (entityId.Value <= 0)
        {
            throw new InvalidOperationException($"{description} contains an uninitialized EntityId.");
        }
    }

    internal static void EnsureJourneyId(JourneyId journeyId, string description)
    {
        if (journeyId.Value <= 0)
        {
            throw new InvalidOperationException($"{description} contains an uninitialized JourneyId.");
        }
    }

    internal static void EnsureMutationId(ScheduledMutationId mutationId, string description)
    {
        if (mutationId.Value <= 0)
        {
            throw new InvalidOperationException($"{description} contains an uninitialized ScheduledMutationId.");
        }
    }

    internal static bool IsGoalSatisfied(SpatialDefinition definition, CellRef cell, MoveGoal goal) =>
        goal switch
        {
            CellGoal cellGoal => cellGoal.Cell == cell,
            AnchorGoal anchorGoal => definition.Anchors.Single(
                anchor => anchor.Id == anchorGoal.AnchorId).Cell == cell,
            ZoneGoal zoneGoal => definition.Zones.Single(
                zone => zone.Id == zoneGoal.ZoneId).Cells.Contains(cell),
            _ => throw new InvalidOperationException($"Unsupported movement goal '{goal.GetType().Name}'."),
        };

    internal static void ValidateCellOverride(
        SpatialDefinition definition,
        CellRef cell,
        CellOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsEmpty)
        {
            throw new InvalidOperationException("Cell override must be non-empty.");
        }

        CellDefinition staticCell = definition.GetCell(cell);
        if (value.BlocksMovement == staticCell.BlocksMovement ||
            value.BlocksSight == staticCell.BlocksSight ||
            value.MoveCost == staticCell.MoveCost)
        {
            throw new InvalidOperationException(
                "Cell override fields equal to Definition must be omitted for canonical sparse state.");
        }

        int effectiveMoveCost = value.MoveCost ?? staticCell.MoveCost;
        GridMapDefinition map = definition.GetMap(cell.MapId);
        _ = checked(map.OrthogonalStepDuration.Ticks * effectiveMoveCost);
    }

    private static void ValidateCanonicalIds<T>(IEnumerable<T> values, string description)
        where T : IComparable<T>
    {
        T[] array = [.. values];
        for (int index = 1; index < array.Length; index++)
        {
            if (array[index - 1].CompareTo(array[index]) >= 0)
            {
                throw new InvalidOperationException($"Spatial {description} collection is not unique and canonical.");
            }
        }
    }

    private static void ValidateUniqueIds<T>(IEnumerable<T> values, string description)
    {
        var set = new HashSet<T>();
        if (values.Any(value => !set.Add(value)))
        {
            throw new InvalidOperationException($"Spatial {description} collection contains duplicate identities.");
        }
    }

    private static void ValidateCanonicalMutationOrder(
        IReadOnlyList<ScheduledSpatialMutationState> mutations)
    {
        for (int index = 1; index < mutations.Count; index++)
        {
            ScheduledSpatialMutationState previous = mutations[index - 1];
            ScheduledSpatialMutationState current = mutations[index];
            if (previous.Due > current.Due ||
                (previous.Due == current.Due && previous.Id.CompareTo(current.Id) >= 0))
            {
                throw new InvalidOperationException("Scheduled mutations are not unique and canonical.");
            }
        }
    }

    private static void ValidateUniqueMutationTargets(
        IReadOnlyList<ScheduledSpatialMutationState> mutations)
    {
        for (int first = 0; first < mutations.Count; first++)
        {
            for (int second = first + 1; second < mutations.Count; second++)
            {
                if (mutations[first].Due == mutations[second].Due &&
                    SameTarget(mutations[first].Mutation, mutations[second].Mutation))
                {
                    throw new InvalidOperationException(
                        "Scheduled mutation target and due-time pairs must be unique.");
                }
            }
        }
    }

    private static bool SameTarget(ScheduledSpatialMutation first, ScheduledSpatialMutation second) =>
        (first, second) switch
        {
            (SetPortalStateMutation left, SetPortalStateMutation right) => left.PortalId == right.PortalId,
            (SetCellOverrideMutation left, SetCellOverrideMutation right) => left.Cell == right.Cell,
            _ => false,
        };
}
