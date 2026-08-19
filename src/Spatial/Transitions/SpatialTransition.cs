using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Contains one atomically planned Spatial event sequence and its scratch projection.</summary>
internal sealed class SpatialTransitionResult
{
    internal SpatialTransitionResult(
        SpatialState resultingState,
        IEnumerable<UncommittedDomainEvent<SpatialEvent>> events)
    {
        ArgumentNullException.ThrowIfNull(resultingState);
        ArgumentNullException.ThrowIfNull(events);
        ResultingState = resultingState;
        Events = Array.AsReadOnly(events.ToArray());
    }

    /// <summary>Gets the complete scratch state after every returned event.</summary>
    internal SpatialState ResultingState { get; }

    /// <summary>Gets primary events, canonical derived outcomes, and an optional final moment event.</summary>
    internal IReadOnlyList<UncommittedDomainEvent<SpatialEvent>> Events { get; }
}

/// <summary>Plans one complete, non-interleaved Spatial transition against an immutable pre-state.</summary>
internal static class SpatialTransition
{
    /// <summary>Completes an immediate transition, appending one canonical relationship diff.</summary>
    internal static SpatialTransitionResult Complete(
        SpatialDefinition definition,
        SpatialState preState,
        ModelTime modelTime,
        IEnumerable<SpatialEvent> primaryBodyEvents) =>
        CompleteCore(
            definition,
            preState,
            modelTime,
            primaryBodyEvents,
            resolvedWorkCount: null);

    /// <summary>
    /// Completes a SpatialMoment transition and appends exactly one MomentResolved event last.
    /// </summary>
    internal static SpatialTransitionResult CompleteMoment(
        SpatialDefinition definition,
        SpatialState preState,
        ModelTime modelTime,
        IEnumerable<SpatialEvent> primaryBodyEvents,
        int resolvedWorkCount)
    {
        if (resolvedWorkCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedWorkCount),
                "A completed SpatialMoment must resolve positive work.");
        }

        ArgumentNullException.ThrowIfNull(preState);
        SpatialStateValidator.ValidateComplete(definition, preState);
        long dueWorkCount = (long)preState.Journeys.Count(
            journey => journey.CurrentLeg.Due == modelTime) +
            preState.ScheduledMutations.Count(mutation => mutation.Due == modelTime);
        if (dueWorkCount == 0)
        {
            throw new InvalidOperationException(
                "A completed SpatialMoment requires work due exactly at its model time.");
        }

        if (dueWorkCount != resolvedWorkCount)
        {
            throw new InvalidOperationException(
                $"Resolved work count {resolvedWorkCount} does not match {dueWorkCount} due work items.");
        }

        return CompleteCore(
            definition,
            preState,
            modelTime,
            primaryBodyEvents,
            resolvedWorkCount);
    }

    private static SpatialTransitionResult CompleteCore(
        SpatialDefinition definition,
        SpatialState preState,
        ModelTime modelTime,
        IEnumerable<SpatialEvent> primaryBodyEvents,
        int? resolvedWorkCount)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(preState);
        ArgumentNullException.ThrowIfNull(primaryBodyEvents);
        SpatialEvent[] body = [.. primaryBodyEvents];
        if (body.Any(payload => payload is null))
        {
            throw new ArgumentException("Spatial transition body cannot contain null events.", nameof(primaryBodyEvents));
        }

        if (body.Any(payload => !IsPrimaryBodyEvent(payload)))
        {
            throw new ArgumentException(
                "Spatial transition body accepts only state-changing primary Spatial events.",
                nameof(primaryBodyEvents));
        }

        if (resolvedWorkCount.HasValue && body.Length == 0)
        {
            throw new ArgumentException(
                "A completed SpatialMoment must contain at least one primary body event.",
                nameof(primaryBodyEvents));
        }

        SpatialStateValidator.ValidateComplete(definition, preState);
        SpatialState workingState = preState;
        var events = new List<UncommittedDomainEvent<SpatialEvent>>(body.Length + 1);
        foreach (SpatialEvent payload in body)
        {
            var uncommitted = new UncommittedDomainEvent<SpatialEvent>(
                SpatialEventKinds.For(payload),
                payload);
            workingState = SpatialProjector.Apply(
                definition,
                workingState,
                uncommitted.Kind,
                uncommitted.Payload,
                modelTime);
            events.Add(uncommitted);
        }

        SpatialStateValidator.ValidateComplete(definition, workingState);
        IReadOnlyList<SpatialEvent> derived = DerivedSpatialRelations.DiffValidated(
            definition,
            preState,
            workingState);
        foreach (SpatialEvent payload in derived)
        {
            var uncommitted = new UncommittedDomainEvent<SpatialEvent>(
                SpatialEventKinds.For(payload),
                payload);
            workingState = SpatialProjector.Apply(
                definition,
                workingState,
                uncommitted.Kind,
                uncommitted.Payload,
                modelTime);
            events.Add(uncommitted);
        }

        if (resolvedWorkCount is int workCount)
        {
            var resolved = new MomentResolvedEvent(workingState.NextMomentOrdinal, workCount);
            var terminal = new UncommittedDomainEvent<SpatialEvent>(
                SpatialEventKinds.MomentResolved,
                resolved);
            workingState = SpatialProjector.Apply(
                definition,
                workingState,
                terminal.Kind,
                terminal.Payload,
                modelTime);
            events.Add(terminal);
            SpatialStateValidator.ValidateComplete(definition, workingState);
        }

        return new SpatialTransitionResult(workingState, events);
    }

    private static bool IsPrimaryBodyEvent(SpatialEvent payload) => payload is
        EntityPlacedEvent or
        EntityRemovedEvent or
        ObservationStateChangedEvent or
        JourneyStartedEvent or
        JourneyRetargetedEvent or
        JourneyCancelledEvent or
        JourneyInterruptedEvent or
        EntitySteppedEvent or
        JourneyReroutedEvent or
        JourneyContinuedEvent or
        JourneyCompletedEvent or
        JourneyBlockedEvent or
        PortalStateChangedEvent or
        CellStateChangedEvent or
        MutationScheduledEvent or
        MutationConsumedEvent;
}
