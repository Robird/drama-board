using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Checks recorded delivery ownership, immutable prefixes and endpoint requirements; never regenerates perception.</summary>
internal static class CycleInteractionValidation {
    internal static void Validate(CycleBusinessState before, CycleBusinessState after, CycleFact fact) {
        if (fact.InteractionChanges.Select(state => state.ActorId).Distinct(StringComparer.Ordinal).Count() != fact.InteractionChanges.Count ||
            fact.InteractionChanges.Any(state => !before.InteractionStates.ContainsKey(state.ActorId))) {
            throw new InvalidOperationException("Interaction replacements contain unknown or duplicate actors.");
        }
        foreach ((string actorId, CharacterInteractionState prior) in before.InteractionStates) {
            CharacterInteractionState? recorded = fact.InteractionChanges.SingleOrDefault(state => state.ActorId == actorId);
            if (prior.CurrentContext?.Status == CharacterContextStatus.Terminal) {
                Require(recorded is null, "A terminal context is frozen.");
                continue;
            }
            CycleObservedEvent[] events = fact.ObservedEvents.Where(entry => entry.ObserverId == actorId).ToArray();
            bool own = fact.ActorId == actorId && fact.OperationRecordId is not null;
            bool opening = fact.Kind == CycleEventKind.DecisionContextOpened && fact.ActorId == actorId;
            bool terminal = before.GetCharacter(actorId).IsActive && !after.GetCharacter(actorId).IsActive;
            bool visibleChange = prior.CurrentContext?.Status == CharacterContextStatus.Open && CycleVisibleEndpointChanges.Changed(before, after, actorId);
            Require(recorded is not null || !(own || opening || terminal || events.Length > 0 || visibleChange),
                "A committed operation or observable change is missing its interaction replacement.");
            if (recorded is null) { continue; }
            recorded.Validate(actorId, fact.At);
            Require(recorded.NextExperienceSequence == checked(prior.NextExperienceSequence + events.Length),
                "Experience identities must advance exactly once for each recorded personal event.");
            if (opening || terminal) {
                ValidateOpening(before, after, fact, prior, recorded, events, terminal);
                continue;
            }
            CharacterContext? oldContext = prior.CurrentContext;
            if (own) { Require(oldContext?.Status == CharacterContextStatus.Open, "An operation must belong to an open context."); }
            bool release = own && (fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.MoveStarted ||
                fact.Kind == CycleEventKind.AttentionAcknowledged && !CycleActions.NeedsDecision(after, actorId));
            if (release) {
                CharacterContext next = recorded.CurrentContext!;
                Require(next.Status == CharacterContextStatus.Closed && next.ContextSequence == oldContext!.ContextSequence &&
                    next.LastOperationSequence == checked(oldContext.LastOperationSequence + 1) && next.NextItemSequence == oldContext.NextItemSequence &&
                    CharacterContextEquality.Same(next.Opening, oldContext.Opening), "A release must close the original context without a response item.");
                ValidateRetained(oldContext!, next, next.LastOperationSequence, 0);
                ValidateAdvance(before, after, fact, prior, recorded);
                ValidateDelivery(prior.UndeliveredExperiences, recorded.UndeliveredExperiences, prior.NextExperienceSequence, events);
                continue;
            }
            Require(CharacterContextEquality.Same(prior.PendingAdvance, recorded.PendingAdvance), "A non-release cannot replace the pending action.");
            if (oldContext?.Status == CharacterContextStatus.Open) {
                CharacterContext next = recorded.CurrentContext!;
                Require(next.Status == CharacterContextStatus.Open && next.ContextSequence == oldContext.ContextSequence &&
                    CharacterContextEquality.Same(next.Opening, oldContext.Opening) &&
                    next.LastOperationSequence == checked(oldContext.LastOperationSequence + (own ? 1 : 0)) &&
                    next.NextItemSequence == checked(oldContext.NextItemSequence + 1), "An open delivery must append exactly one item in the same context.");
                ValidateRetained(oldContext, next, next.LastOperationSequence, 1);
                CharacterContextItem item = next.Items[^1];
                bool rejected = fact.Kind == CycleEventKind.OperationRejected;
                Require(item.ItemSequence == oldContext.NextItemSequence && item.IsRejected == rejected &&
                    (rejected ? item.Observation is null && item.Experiences.Count == 0 : item.Observation is not null),
                    "A delivery requires its next item identity and the material appropriate to its committed disposition.");
                if (own) {
                    Require(item.OperationSequence == next.LastOperationSequence && item.ToolCallId == fact.ToolCallId &&
                        item.OperationRecordId == fact.OperationRecordId && item.RawInput == fact.RawInput &&
                        item.Result == CycleOperationResults.Describe(fact) &&
                        (item.GoalOperation is null ? fact.GoalOperation is null : item.GoalOperation.SameAs(fact.GoalOperation)),
                        "The operation result must retain the original captured identity and recorded answer.");
                    if (!rejected) { ValidateInstantCommand(before, after, fact, recorded); }
                } else {
                    Require(item.OperationSequence is null, "Another actor's or natural event is not this actor's tool result.");
                    Require(events.Length > 0 || !CycleObservationEquality.Same(oldContext.LatestObservation, item.Observation!),
                        "A perception item must contain an event or a changed observation.");
                }
                if (!rejected) { ValidateObservation(after, actorId, item.Observation!); }
                ValidateDelivery([], item.Experiences, prior.NextExperienceSequence, events);
            } else {
                Require(SameContext(prior.CurrentContext, recorded.CurrentContext), "Closed and unstarted contexts cannot receive items.");
                ValidateDelivery(prior.UndeliveredExperiences, recorded.UndeliveredExperiences, prior.NextExperienceSequence, events);
            }
            if (!own || fact.Kind != CycleEventKind.HandoffEdited) {
                Require(recorded.DraftHandoff == prior.DraftHandoff, "Only handoff editing or a release consumes the draft.");
            }
        }
    }

    private static void ValidateOpening(CycleBusinessState before, CycleBusinessState after, CycleFact fact,
        CharacterInteractionState prior, CharacterInteractionState next, IReadOnlyList<CycleObservedEvent> events, bool terminal) {
        CharacterContext context = next.CurrentContext ?? throw new InvalidOperationException("Opening requires a context.");
        Require(context.ContextSequence == checked((prior.CurrentContext?.ContextSequence ?? 0) + 1) &&
            context.Status == (terminal ? CharacterContextStatus.Terminal : CharacterContextStatus.Open) &&
            context.Items.Count == 0 && context.NextItemSequence == 1 && context.LastOperationSequence == 0 &&
            context.Opening.DeliveredAt == fact.At && CharacterContextEquality.Same(context.Opening.PreviousAdvance, prior.PendingAdvance) &&
            next.PendingAdvance is null && next.UndeliveredExperiences.Count == 0 && next.DraftHandoff is null,
            "A new context must consume the pending delivery into one frozen opening.");
        if (!terminal) {
            Require(prior.CurrentContext is null or { Status: CharacterContextStatus.Closed } && CycleActions.NeedsDecision(before, prior.ActorId),
                "A decision opening requires an unstarted or closed character at an input boundary.");
        }
        bool ongoing = after.GetCharacter(prior.ActorId).Activity is not null || CycleActions.IsTravelling(after, prior.ActorId);
        Require(context.Opening.ActivityState == (terminal ? CycleOutcomeActivityState.Terminal : ongoing
            ? CycleOutcomeActivityState.Ongoing : CycleOutcomeActivityState.Completed), "The opening must describe the recorded activity endpoint.");
        ValidateObservation(after, prior.ActorId, context.Opening.Observation);
        ValidateDelivery(prior.UndeliveredExperiences, context.Opening.Experiences, prior.NextExperienceSequence, events);
    }

    private static void ValidateAdvance(CycleBusinessState before, CycleBusinessState after, CycleFact fact,
        CharacterInteractionState prior, CharacterInteractionState next) {
        PendingAdvance pending = next.PendingAdvance ?? throw new InvalidOperationException("A release requires a pending action.");
        bool kindMatches = (pending.Command.Kind, fact.Kind) is
            (CycleCommandKind.Wait, CycleEventKind.WaitStarted) or (CycleCommandKind.Gather, CycleEventKind.GatherStarted) or
            (CycleCommandKind.Go, CycleEventKind.MoveStarted) or (CycleCommandKind.Continue, CycleEventKind.AttentionAcknowledged);
        SpatialEntity? entity = after.Expedition?.Spatial.Entities.Single(e => e.Id.Value == prior.ActorId);
        Require(kindMatches && pending.ActorId == fact.ActorId && pending.SourceContextSequence == prior.CurrentContext!.ContextSequence &&
            pending.ToolCallId == fact.ToolCallId && pending.OperationRecordId == fact.OperationRecordId && pending.AcceptedAt == fact.At &&
            pending.RawInput == fact.RawInput && CycleCommandParser.Parse(pending.RawInput) == pending.Command &&
            pending.OperationSequence == next.CurrentContext!.LastOperationSequence && pending.Handoff == prior.DraftHandoff &&
            pending.OriginPlaceName == prior.CurrentContext.LatestObservation.PlaceName &&
            pending.ActivityId == after.GetCharacter(prior.ActorId).Activity?.Id &&
            pending.MovementGeneration == (entity?.Location is TraversingLocation ? entity.MovementGeneration : null) &&
            next.DraftHandoff is null && prior.PendingAdvance is null,
            "The release must preserve its captured identity, acceptance-time knowledge and activity association.");
        if (pending.Command.Kind == CycleCommandKind.Gather) {
            CycleActivity activity = after.GetCharacter(prior.ActorId).Activity!;
            Require(pending.Command.ResourceId == fact.ResourceId && pending.Command == (pending.Command.All
                    ? CycleCommand.GatherAll(fact.ResourceId!) : CycleCommand.Gather(fact.ResourceId!, pending.Command.Count)) &&
                activity.SelectedAll == pending.Command.All && activity.TargetCount == (pending.Command.All
                    ? before.GetResource(fact.ResourceId!).Stock : pending.Command.Count),
                "The recorded gathering target and frozen count must match the accepted activity.");
        } else if (pending.Command.Kind == CycleCommandKind.Go) {
            Require(entity?.Location is TraversingLocation movement && pending.Command == CycleCommand.Go(movement.PassageId.Value),
                "The recorded passage does not match the movement.");
        } else if (pending.Command.Kind == CycleCommandKind.Wait) {
            Require(pending.Command == CycleCommand.Wait(pending.Command.Hours) &&
                after.GetCharacter(prior.ActorId).Activity?.DurationTicks == pending.Command.Hours * CycleTime.Hour,
                "The recorded wait duration does not match its activity.");
        } else {
            Require(pending.Command == CycleCommand.Continue(), "The resume action must preserve the accepted command.");
        }
        Require(pending.Description == CycleInteractionRules.DescribeAdvance(pending.Command, prior.CurrentContext!.LatestObservation,
            entity?.Location is TraversingLocation), "The action description must retain only acceptance-time visible knowledge.");
    }

    private static void ValidateInstantCommand(CycleBusinessState before, CycleBusinessState after, CycleFact fact,
        CharacterInteractionState interaction) {
        CycleCommand command = CycleCommandParser.Parse(fact.RawInput!)
            ?? throw new InvalidOperationException("A successful operation must retain parseable raw input.");
        if (fact.Kind == CycleEventKind.HandoffEdited) {
            Require(interaction.DraftHandoff == command.HandoffText,
                "The draft must contain the handoff accepted from the recorded raw input.");
        } else if (fact.Kind == CycleEventKind.ItemUsed) {
            CycleCharacter prior = before.GetCharacter(fact.ActorId!), next = after.GetCharacter(fact.ActorId!);
            Require(command.ItemId is not null && prior.Backpack.Count(command.ItemId) > 0 &&
                next.Backpack.Count(command.ItemId) == prior.Backpack.Count(command.ItemId) - 1 &&
                fact.ObservedEvents.Any(entry => entry.ObserverId == fact.ActorId &&
                    entry.Kind == CycleObservedEventKind.ItemUsed && entry.SubjectId == command.ItemId),
                "The used inventory item must match the recorded raw input.");
        }
    }

    private static void ValidateRetained(CharacterContext before, CharacterContext after, long operationSequence, int appendCount) {
        CharacterContextItem[] retained = before.Items.Where(item => !item.IsRejected || item.OperationSequence >= operationSequence - 2).ToArray();
        Require(after.Items.Count == retained.Length + appendCount &&
            retained.Zip(after.Items).All(pair => CharacterContextEquality.Same(pair.First, pair.Second)),
            "A delivery must preserve all retained item contents and identities.");
    }

    private static void ValidateDelivery(IReadOnlyList<CharacterExperience> prior, IReadOnlyList<CharacterExperience> next,
        long nextSequence, IReadOnlyList<CycleObservedEvent> events) {
        Require(next.Count == prior.Count + events.Count && prior.Zip(next).All(pair => CharacterContextEquality.Same(pair.First, pair.Second)),
            "A delivery cannot drop, repeat or rewrite prior experiences.");
        for (int i = 0; i < events.Count; i++) {
            CharacterExperience experience = next[prior.Count + i];
            CycleObservedEvent source = events[i], delivered = experience.Event;
            Require(experience.ExperienceSequence == checked(nextSequence + i) && delivered.EventOrdinal == i &&
                delivered.ObserverId == source.ObserverId && delivered.Kind == source.Kind && delivered.From == source.From && delivered.At == source.At &&
                delivered.SubjectId == source.SubjectId && delivered.SubjectName == source.SubjectName && delivered.Detail == source.Detail &&
                delivered.Effects.Count == source.Effects.Count && delivered.Effects.Zip(source.Effects).All(pair =>
                    pair.First.Key == pair.Second.Key && pair.First.Before == pair.Second.Before && pair.First.After == pair.Second.After),
                "Delivered experience must match its observer's recorded event with a local identity.");
        }
    }

    private static void ValidateObservation(CycleBusinessState state, string actorId, CycleObservation observation) {
        CycleCharacter actor = state.GetCharacter(actorId);
        Require(state.Expedition is null ? observation.Goals is null : state.InteractionStates[actorId].Goals.SameAs(observation.Goals),
            "The frozen goal material must match only this character's own goal collection.");
        Require(observation.Communication == (state.Expedition is null ? null : new CycleCommunicationObservation(
            CycleCommunicationRules.Remaining(actor, state.Time), CycleCommunicationRules.SendLimit,
            actor.Communication.AttentionPending, CycleCommunicationRules.MaximumSpeechScalars)),
            "Recorded communication must match the observer's own budget and attention state.");
        Require(observation.Time == state.Time && observation.Actor.Id == actorId && observation.Actor.Name == actor.Name &&
            observation.IsDay == state.Light.IsDay && observation.NextLightChange == state.Light.NextChange &&
            observation.Outcome == (actor.Ending?.Outcome ?? CycleExpeditionOutcome.Exploring) &&
            observation.EndingReason == actor.Ending?.Reason && observation.IsExpedition == (state.Expedition is not null),
            "Recorded observation does not match its actor and publication boundary.");
        // These are recorded endpoint checks, not a call to the perception builder or its command queries.
        Require(observation.Actor.SkillLevel == actor.SkillLevel && observation.Actor.LastResult == actor.LastResult &&
            observation.Actor.NextActivityId == actor.NextActivityId &&
            CycleObservationEquality.SameInventory(observation.Actor.Backpack, actor.Backpack) &&
            CycleObservationEquality.SameActivity(observation.Actor.Activity, actor.Activity) &&
            observation.Actor.Notices.Count == actor.Notices.Count && observation.Actor.Notices.Zip(actor.Notices).All(pair =>
                CycleObservationEquality.SameNotice(pair.First, pair.Second)), "Recorded private observation differs from its endpoint.");
        SpatialLocation? location = CycleActions.Location(state, actorId);
        string? place = (location as AtPlaceLocation)?.PlaceId.Value;
        CycleResource[] visible = state.Resources.Where(resource => state.Expedition is null ||
            place is not null && CycleExpeditionMap.GetResourcePlace(resource.Id) == place).ToArray();
        Require(observation.Resources.Count == visible.Length && observation.Resources.Zip(visible).All(pair =>
            CycleObservationEquality.SameResource(pair.First, pair.Second)), "Recorded resources must match the visible endpoint.");
        Require(observation.PlaceId == place && observation.PlaceName == (place is null ? null : CycleExpeditionMap.GetPlace(place).Name) &&
            observation.PlaceDescription == (place is null ? null : CycleExpeditionMap.GetPlace(place).Description) &&
            observation.ArrivalDue == (location is TraversingLocation moving ? moving.ArrivalDue : null),
            "Recorded location must match the visible spatial endpoint.");
        Require(observation.Journey == CycleJourneyObservation.Observe(state.Expedition, actorId, location),
            "Recorded journey must retain the traveller's route without revealing unknown destinations.");
        CycleCharacter[] others = state.Characters.Where(other => other.IsActive && other.Id != actorId && (state.Expedition is null ||
            place is not null && CycleActions.Location(state, other.Id) is AtPlaceLocation otherPlace &&
                otherPlace.PlaceId.Value == place)).ToArray();
        Require(observation.OtherCharacters.Count == others.Length && observation.OtherCharacters.Zip(others).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.GatheringResourceId == (pair.Second.Activity is { Kind: CycleActivityKind.Gather } gathering ? gathering.ResourceId : null) &&
            pair.First.Waiting == (pair.Second.Activity?.Kind == CycleActivityKind.Wait)),
            "Recorded public activities must match the visible actors without exposing private fields.");
        ValidateBodyAndWeather(state, actor, observation);
        ValidateKnownGeography(state, actorId, place, observation);
        Require(observation.HarvestDurations.Count == visible.Length && visible.All(resource =>
            observation.HarvestDurations.TryGetValue(resource.Id, out long duration) && duration == CycleActions.HarvestDuration(resource, actor)),
            "Recorded gathering durations must match the actor and visible resource definitions.");
        Require(observation.Commands.All(entry => entry.Command.Kind switch {
            CycleCommandKind.Go => observation.Passages.Any(p => p.Id == entry.Command.PassageId),
            CycleCommandKind.Gather => visible.Any(r => r.Id == entry.Command.ResourceId),
            CycleCommandKind.Use => actor.Survival is not null && (actor.Backpack.Items.ContainsKey(entry.Command.ItemId!) || visible.Any(r => r.ItemId == entry.Command.ItemId)),
            CycleCommandKind.Handoff or CycleCommandKind.Goal => state.Expedition is not null,
            _ => true
        }), "A recorded command catalogue must not expose unseen identities or unavailable scenario capabilities.");
    }

    private static void ValidateBodyAndWeather(CycleBusinessState state, CycleCharacter actor, CycleObservation observation) {
        CycleSurvival? body = actor.Survival?.Project(state.Time);
        CycleSurvivalObservation? visible = observation.Survival;
        Require((body is null) == (visible is null), "The recorded body must agree with the character's capabilities.");
        bool sheltered = CycleActions.Location(state, actor.Id) is AtPlaceLocation at && CycleExpeditionMap.GetPlace(at.PlaceId.Value).Sheltered;
        if (body is not null && visible is not null) {
            visible.Validate(observation.Time);
            Require(visible.Food == (decimal)body.FoodUnits / CycleSurvival.Scale && visible.Water == (decimal)body.WaterUnits / CycleSurvival.Scale &&
                visible.Warmth == (decimal)body.WarmthUnits / CycleSurvival.Scale && visible.FoodRate.SameAs(body.FoodRate) &&
                visible.WaterRate.SameAs(body.WaterRate) && visible.WarmthRate.SameAs(body.WarmthRate) && visible.Sheltered == sheltered &&
                visible.Minimum == CycleSurvival.MinimumValue && visible.Maximum == CycleSurvival.MaximumValue && visible.WarningThreshold == (decimal)CycleSurvival.WarningUnits / CycleSurvival.Scale &&
                visible.PendingWarningMask == body.PendingMask && visible.WarmthWarningPending == ((body.PendingMask & 4) != 0),
                "Recorded needs must match the integrated physical endpoint.");
            string[] warnings = new[] { (1, "饱食"), (2, "水分"), (4, "温暖") }.Where(pair => (body.PendingMask & pair.Item1) != 0)
                .Select(pair => $"{pair.Item2}曾降至警戒线（本组最早提醒于 {body.PendingSince}）。").ToArray();
            Require(visible.PendingWarnings.SequenceEqual(warnings), "Recorded body warnings must match the pending notices.");
        }
        CycleEnvironment? environment = state.Expedition?.Environment;
        CycleWeatherObservation? weather = observation.Weather;
        Require((environment is null) == (weather is null), "The recorded weather must agree with scenario capabilities.");
        if (environment is not null && weather is not null) {
            CycleWeatherEffect? effect = environment.LastEffects.SingleOrDefault(entry => entry.ActorId == actor.Id);
            Require(weather.NextWindow == (actor.IsActive ? environment.NextDue : null) && weather.IntervalTicks == CycleEnvironmentRules.IntervalTicks &&
                weather.ExposedWarmthLoss == CycleEnvironmentRules.ExposureLoss && weather.Sheltered == sheltered &&
                weather.LastAt == (effect is null ? null : environment.LastAt) && weather.LastKind == (effect is null ? null :
                    environment.LastCold ? CycleWeatherKind.ColdWind : CycleWeatherKind.Calm) &&
                weather.LastActualWarmthLoss == (decimal)(effect?.WarmthLossUnits ?? 0) / CycleSurvival.Scale &&
                weather.LastWasSheltered == (effect?.Sheltered ?? false), "Recorded weather must match the already resolved result.");
        }
    }

    private static void ValidateKnownGeography(CycleBusinessState state, string actorId, string? place, CycleObservation observation) {
        CycleVisit[] visits = state.Expedition?.Visits.Where(visit => visit.ActorId == actorId).ToArray() ?? [];
        CyclePassageDefinition[] passages = place is null ? [] : CycleExpeditionMap.GetPassages(place).ToArray();
        Require(observation.Passages.Count == passages.Length && observation.Passages.Zip(passages).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Description == pair.Second.Description && pair.First.DurationTicks == pair.Second.DurationTicks &&
            pair.First.KnownDestinationName == (visits.Any(visit => visit.PlaceId == pair.Second.To) ? CycleExpeditionMap.GetPlace(pair.Second.To).Name : null)),
            "Recorded passages must expose only currently visible clues and previously learned destinations.");
        CycleVisit[] memories = visits.Where(visit => visit.PlaceId != place).ToArray();
        Require(observation.Memories.Count == memories.Length && observation.Memories.Zip(memories).All(pair =>
            pair.First.PlaceId == pair.Second.PlaceId && pair.First.At == pair.Second.At && pair.First.Name == CycleExpeditionMap.GetPlace(pair.Second.PlaceId).Name &&
            pair.First.Resources.Count == pair.Second.Resources.Count && pair.First.Resources.Zip(pair.Second.Resources).All(resources =>
                CycleObservationEquality.SameResource(resources.First, resources.Second))),
            "Recorded memories must retain the actor's historical observations rather than distant current truth.");
    }

    private static bool SameContext(CharacterContext? a, CharacterContext? b) => ReferenceEquals(a, b) || a is not null && b is not null &&
        a.ContextSequence == b.ContextSequence && a.Status == b.Status && a.NextItemSequence == b.NextItemSequence &&
        a.LastOperationSequence == b.LastOperationSequence && CharacterContextEquality.Same(a.Opening, b.Opening) &&
        a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => CharacterContextEquality.Same(pair.First, pair.Second));

    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
