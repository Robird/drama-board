using DramaBoard.Kernel.Time;

namespace DramaBoard.Spatial;

/// <summary>Contains one atomically planned Spatial fact sequence and its scratch projection.</summary>
internal sealed class SpatialTransitionResult
{
    internal SpatialTransitionResult(
        SpatialState resultingState,
        IEnumerable<SpatialEvent> events)
    {
        ArgumentNullException.ThrowIfNull(resultingState);
        ArgumentNullException.ThrowIfNull(events);
        ResultingState = resultingState;
        Facts = Array.AsReadOnly(events.ToArray());
    }

    /// <summary>Gets the complete scratch state after every returned fact.</summary>
    internal SpatialState ResultingState { get; }

    /// <summary>Gets primary facts followed by the canonical derived relation delta.</summary>
    internal IReadOnlyList<SpatialEvent> Facts { get; }
}

/// <summary>Plans one complete, non-interleaved Spatial transition against an immutable pre-state.</summary>
internal static class SpatialTransition
{
    /// <summary>Completes primary facts and appends one canonical relationship diff.</summary>
    internal static SpatialTransitionResult Complete(
        SpatialDefinition definition,
        SpatialState preState,
        ModelTime modelTime,
        IEnumerable<SpatialEvent> primaryBodyEvents)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(preState);
        ArgumentNullException.ThrowIfNull(primaryBodyEvents);
        SpatialEvent[] body = [.. primaryBodyEvents];
        if (body.Any(payload => payload is null))
        {
            throw new ArgumentException("Spatial transition body cannot contain null facts.", nameof(primaryBodyEvents));
        }

        if (body.Any(payload => !IsPrimaryBodyEvent(payload)))
        {
            throw new ArgumentException(
                "Spatial transition body accepts only state-changing primary Spatial facts.",
                nameof(primaryBodyEvents));
        }

        SpatialStateValidator.ValidateComplete(definition, preState);
        SpatialState workingState = preState;
        var events = new List<SpatialEvent>(body.Length + 1);
        foreach (SpatialEvent payload in body)
        {
            workingState = SpatialProjector.Apply(definition, workingState, payload, modelTime);
            events.Add(payload);
        }

        SpatialStateValidator.ValidateComplete(definition, workingState);
        IReadOnlyList<SpatialEvent> derived = DerivedSpatialRelations.DiffValidated(
            definition,
            preState,
            workingState);
        foreach (SpatialEvent payload in derived)
        {
            workingState = SpatialProjector.Apply(definition, workingState, payload, modelTime);
            events.Add(payload);
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
