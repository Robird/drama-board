using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard;

public static class BoardIds
{
    public const string Tavern = "tavern";
    public const string Market = "market";
    public const string Cellar = "cellar";
    public const string Alice = "alice";
    public const string Bob = "bob";
    public const string BrassKey = "brass-key";
    public const string LockedChest = "locked-chest";
    public const string DuchessLetter = "duchess-letter";
    public const string SilverCoinOne = "silver-coin-1";
    public const string SilverCoinTwo = "silver-coin-2";
    public const string KeyLocationKnown = "key.location-known";
    public const string ChestContainsLetter = "chest.contains-letter";
    public const string ChestOpenedKnown = "chest.opened-known";
    public const string CellarSealedKnown = "cellar.sealed-known";
    public const string ObjectHeld = "object.held";
    public const string ObjectReceived = "object.received";
    public const string ObjectShown = "object.shown";
    public const string ObjectPlaced = "object.placed";
    public const string ObjectInspected = "object.inspected";
    public const string LetterAuthenticityKnown = "duchess-letter.authenticity-known";
    public const string LetterContentsKnown = "duchess-letter.contents-known";
    public const string DialogueHeard = "dialogue.heard";
    public const string LastActionOutcome = "action.last-outcome";
    public const string ActionRejected = "action.rejected";
}

public static class BoardTiming
{
    public const long TravelTicks = 300_000;
    public const long DeadlineTicks = 3_600_000;
    public const long DefaultWaitTicks = 60_000;
    public const long RandomRunBoundaryTicks = 4_200_000;
}

public sealed record BoardPlace(long Id, string Key, IReadOnlyList<string> AdjacentPlaceIds);

public sealed record BoardFact(string Kind, string? RelatedId, string Text);

public enum BoardActivityKind
{
    Travel,
    Wait,
}

public sealed record BoardActivity(
    BoardActivityKind Kind,
    ModelTime Due,
    string? DestinationId = null);

public sealed record BoardActor(
    long Id,
    string Key,
    string PlaceId,
    long Generation,
    long DecisionSequence,
    BoardActivity? Activity,
    IReadOnlyList<BoardFact> KnownFacts);

public sealed record BoardObject(
    long Id,
    string Key,
    string? PlaceId,
    long? OwnerActorId);

public sealed record FirstBoardWorld(
    ulong WorldSeed,
    long NextPersistentId,
    ModelTime Now,
    IReadOnlyList<BoardPlace> Places,
    IReadOnlyList<BoardActor> Actors,
    IReadOnlyList<BoardObject> Objects,
    bool CellarSealed,
    bool ChestOpened)
{
    public static FirstBoardWorld CreateInitial(ulong worldSeed) =>
        ScenarioInstance.CreateDefault(worldSeed).CreateInitialWorld();

    public BoardActor Actor(string actorId) =>
        Actors.Single(actor => actor.Key == actorId);

    public BoardActor Actor(long actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    public BoardObject Object(string objectId) =>
        Objects.Single(item => item.Key == objectId);

    public BoardPlace Place(string placeId) =>
        Places.Single(place => place.Key == placeId);

    public bool AreAdjacent(string firstPlaceId, string secondPlaceId) =>
        Place(firstPlaceId).AdjacentPlaceIds.Contains(secondPlaceId, StringComparer.Ordinal);

    public bool IsIdle(BoardActor actor) =>
        actor.Activity is null;

    public bool IsPresent(BoardActor actor) =>
        actor.Activity?.Kind != BoardActivityKind.Travel;

}

public abstract record BoardEventPayload;

public sealed record ActorDepartedEvent(
    string ActorId,
    string OriginId,
    string DestinationId,
    ModelTime ArriveAt) : BoardEventPayload;

public sealed record ActorArrivedEvent(
    string ActorId,
    string DestinationId) : BoardEventPayload;

public sealed record ActorWaitStartedEvent(
    string ActorId,
    ModelTime CompleteAt) : BoardEventPayload;

public sealed record ActorWaitedEvent(string ActorId) : BoardEventPayload;

public sealed record ActorSpokeEvent(
    string ActorId,
    string TargetActorId,
    string Text,
    string? SharedFactKind) : BoardEventPayload;

public sealed record ActorObservedEvent(
    string ActorId,
    IReadOnlyList<BoardFact> LearnedFacts,
    string? TargetObjectId = null) : BoardEventPayload;

public sealed record ObjectTakenEvent(
    string ActorId,
    string ObjectId) : BoardEventPayload;

public sealed record ObjectPlacedEvent(
    string ActorId,
    string ObjectId,
    string PlaceId) : BoardEventPayload;

public sealed record ObjectGivenEvent(
    string ActorId,
    string TargetActorId,
    string ObjectId) : BoardEventPayload;

public sealed record ObjectShownEvent(
    string ActorId,
    string TargetActorId,
    string ObjectId) : BoardEventPayload;

public sealed record ChestOpenedEvent(
    string ActorId,
    string ObjectId,
    string KeyObjectId) : BoardEventPayload;

public sealed record ActionRejectedEvent(
    string ActorId,
    Intent RejectedIntent,
    string Reason) : BoardEventPayload;

public sealed record CellarSealedEvent : BoardEventPayload;

public sealed class FirstBoardReducer
{
    public FirstBoardWorld Apply(
        FirstBoardWorld world,
        LogicalInstant instant,
        BoardEventPayload fact)
    {
        FirstBoardWorld updated = fact switch
        {
            ActorDepartedEvent departed =>
                UpdateActor(world, departed.ActorId, actor =>
                    AddFacts(CompleteDecision(actor) with
                    {
                        Activity = new BoardActivity(
                            BoardActivityKind.Travel,
                            departed.ArriveAt,
                            departed.DestinationId),
                        PlaceId = departed.OriginId,
                    }, [LastOutcome(
                        $"Your travel to {departed.DestinationId} was accepted; arrival is pending.")])),
            ActorArrivedEvent arrived =>
                UpdateActor(world, arrived.ActorId, actor =>
                    AddFacts(CompleteActivity(actor) with
                    {
                        PlaceId = arrived.DestinationId,
                    }, [LastOutcome($"You successfully arrived at {arrived.DestinationId}.")])),
            ActorWaitStartedEvent waited =>
                UpdateActor(world, waited.ActorId, actor =>
                    AddFacts(CompleteDecision(actor) with
                    {
                        Activity = new BoardActivity(BoardActivityKind.Wait, waited.CompleteAt),
                    }, [LastOutcome(
                        $"Your wait was accepted until model time {waited.CompleteAt.Ticks}ms.")])),
            ActorWaitedEvent waited =>
                UpdateActor(world, waited.ActorId, actor =>
                    AddFacts(CompleteActivity(actor), [LastOutcome(
                        $"You successfully finished waiting at model time " +
                        $"{instant.ModelTime.Ticks}ms.")])),
            ActorSpokeEvent spoke =>
                ApplySpoke(world, spoke),
            ActorObservedEvent observed =>
                UpdateActor(world, observed.ActorId, actor =>
                    AddFacts(
                        CompleteDecision(actor),
                        observed.LearnedFacts.Append(LastOutcome(ObservationOutcome(observed))))),
            ObjectTakenEvent taken =>
                ApplyTaken(world, taken),
            ObjectPlacedEvent placed =>
                ApplyPlaced(world, placed),
            ObjectGivenEvent given =>
                ApplyGiven(world, given),
            ObjectShownEvent shown =>
                ApplyShown(world, shown),
            ChestOpenedEvent opened =>
                ApplyChestOpened(world, opened),
            ActionRejectedEvent rejected =>
                ApplyRejected(world, rejected),
            CellarSealedEvent =>
                ApplyCellarSealed(world),
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard fact '{fact.GetType().Name}'."),
        };

        return updated with { Now = instant.ModelTime };
    }

    private static FirstBoardWorld ApplyRejected(
        FirstBoardWorld world,
        ActionRejectedEvent rejected)
    {
        var learnedFacts = new List<BoardFact>
        {
            RejectedActionFact(rejected.RejectedIntent, rejected.Reason),
            LastOutcome($"Your {rejected.RejectedIntent.ActionKind.Id} action was rejected: " +
                $"{rejected.Reason}."),
        };
        if (rejected.RejectedIntent.ActionKind == ActionKinds.Travel &&
            rejected.RejectedIntent.DestinationId == BoardIds.Cellar &&
            rejected.Reason == "cellar is sealed")
        {
            learnedFacts.Add(CellarSealedFact());
        }

        return UpdateActor(world, rejected.ActorId, actor =>
            AddFacts(CompleteDecision(actor), learnedFacts));
    }

    private static FirstBoardWorld ApplyCellarSealed(FirstBoardWorld world)
    {
        FirstBoardWorld updated = world with { CellarSealed = true };
        foreach (BoardActor witness in world.Actors.Where(actor =>
                     world.IsPresent(actor) && actor.PlaceId == BoardIds.Cellar))
        {
            updated = UpdateActor(updated, witness.Id, actor => AddFacts(actor, [CellarSealedFact()]));
        }

        return updated;
    }

    private static FirstBoardWorld ApplySpoke(FirstBoardWorld world, ActorSpokeEvent spoke)
    {
        BoardActor speaker = world.Actor(spoke.ActorId);
        BoardFact? sharedFact = spoke.SharedFactKind is null
            ? null
            : speaker.KnownFacts.Single(fact => fact.Kind == spoke.SharedFactKind);
        FirstBoardWorld afterSpeaker = UpdateActor(world, spoke.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully spoke to {spoke.TargetActorId}: {spoke.Text}")]));
        return UpdateActor(afterSpeaker, spoke.TargetActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                new(
                    BoardIds.DialogueHeard,
                    spoke.ActorId,
                    $"{spoke.ActorId} said to you: {spoke.Text}"),
            };
            if (sharedFact is not null)
            {
                facts.Add(sharedFact);
            }

            if (actor.Activity?.Kind == BoardActivityKind.Wait)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {spoke.ActorId} spoke to you."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardWorld ApplyTaken(FirstBoardWorld world, ObjectTakenEvent taken)
    {
        BoardActor actor = world.Actor(taken.ActorId);
        FirstBoardWorld updated = UpdateObject(world, taken.ObjectId, item => item with
        {
            PlaceId = null,
            OwnerActorId = actor.Id,
        });
        return UpdateActor(updated, taken.ActorId, current =>
            AddFacts(CompleteDecision(current),
            [
                KeyLocationFact(),
                LastOutcome($"You successfully took {taken.ObjectId}."),
            ]));
    }

    private static FirstBoardWorld ApplyPlaced(FirstBoardWorld world, ObjectPlacedEvent placed)
    {
        FirstBoardWorld updated = UpdateObject(world, placed.ObjectId, item => item with
        {
            PlaceId = placed.PlaceId,
            OwnerActorId = null,
        });
        updated = UpdateActor(updated, placed.ActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                LastOutcome(
                    $"You placed {placed.ObjectId} at {placed.PlaceId}; it is now public and anyone there may inspect or take it."),
            };
            if (placed.ObjectId == BoardIds.BrassKey)
            {
                facts.Add(KeyLocationFact(placed.PlaceId));
            }

            return AddFacts(CompleteDecision(actor), facts);
        });
        foreach (BoardActor witness in world.Actors.Where(actor =>
                     actor.Key != placed.ActorId &&
                     world.IsPresent(actor) &&
                     actor.PlaceId == placed.PlaceId))
        {
            updated = UpdateActor(updated, witness.Id, actor =>
            {
                var facts = new List<BoardFact>
                {
                    new(
                        BoardIds.ObjectPlaced,
                        placed.ObjectId,
                        $"{placed.ActorId} placed {placed.ObjectId} at {placed.PlaceId}; it was publicly available at that moment."),
                };
                if (placed.ObjectId == BoardIds.BrassKey)
                {
                    facts.Add(KeyLocationFact(placed.PlaceId));
                }

                if (actor.Activity?.Kind == BoardActivityKind.Wait)
                {
                    facts.Add(LastOutcome(
                        $"Your wait was interrupted because {placed.ActorId} placed {placed.ObjectId} here."));
                    actor = CompleteActivity(actor);
                }

                return AddFacts(actor, facts);
            });
        }

        return updated;
    }

    private static FirstBoardWorld ApplyGiven(FirstBoardWorld world, ObjectGivenEvent given)
    {
        BoardActor target = world.Actor(given.TargetActorId);
        FirstBoardWorld updated = UpdateObject(world, given.ObjectId, item => item with
        {
            PlaceId = null,
            OwnerActorId = target.Id,
        });
        updated = UpdateActor(updated, given.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully gave {given.ObjectId} to {given.TargetActorId}.")]));
        var targetFacts = new List<BoardFact>
        {
            new(
                BoardIds.ObjectReceived,
                given.ObjectId,
                $"{given.ActorId} gave you {given.ObjectId}."),
        };
        if (given.ObjectId == BoardIds.BrassKey)
        {
            targetFacts.Add(KeyLocationFact());
        }

        return UpdateActor(updated, given.TargetActorId, actor => AddFacts(actor, targetFacts));
    }

    private static FirstBoardWorld ApplyShown(FirstBoardWorld world, ObjectShownEvent shown)
    {
        FirstBoardWorld updated = UpdateActor(world, shown.ActorId, actor =>
            AddFacts(CompleteDecision(actor), [LastOutcome(
                $"You successfully showed {shown.ObjectId} to {shown.TargetActorId} without giving it away.")]));
        return UpdateActor(updated, shown.TargetActorId, actor =>
        {
            var facts = new List<BoardFact>
            {
                new(
                    BoardIds.ObjectShown,
                    shown.ObjectId,
                    $"{shown.ActorId} showed you {shown.ObjectId}; you verified that they held it at that moment."),
            };
            if (actor.Activity?.Kind == BoardActivityKind.Wait)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {shown.ActorId} showed you {shown.ObjectId}."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardWorld ApplyChestOpened(
        FirstBoardWorld world,
        ChestOpenedEvent opened)
    {
        FirstBoardWorld updated = UpdateObject(
            world with { ChestOpened = true },
            BoardIds.DuchessLetter,
            letter => letter with
            {
                PlaceId = null,
                OwnerActorId = world.Actor(opened.ActorId).Id,
            });
        return UpdateActor(updated, opened.ActorId, actor =>
            AddFacts(CompleteDecision(actor),
            [
                new BoardFact(
                    BoardIds.ChestContainsLetter,
                    BoardIds.DuchessLetter,
                    "You recovered the duchess's letter from the opened chest and now carry it."),
                LastOutcome(
                    $"You successfully used {opened.KeyObjectId} to open {opened.ObjectId} " +
                    "and took the duchess's letter."),
            ]));
    }

    private static BoardActor CompleteDecision(BoardActor actor) =>
        actor with
        {
            Generation = checked(actor.Generation + 1),
            DecisionSequence = checked(actor.DecisionSequence + 1),
        };

    private static BoardActor CompleteActivity(BoardActor actor) =>
        actor with
        {
            Activity = null,
            Generation = checked(actor.Generation + 1),
        };

    private static BoardActor AddFacts(BoardActor actor, IEnumerable<BoardFact> facts)
    {
        BoardFact[] merged =
        [
            .. actor.KnownFacts
                .Concat(facts)
                .GroupBy(fact => (fact.Kind, fact.RelatedId))
                .Select(group => group.Last())
                .OrderBy(fact => fact.Kind, StringComparer.Ordinal)
                .ThenBy(fact => fact.RelatedId, StringComparer.Ordinal),
        ];
        return actor with { KnownFacts = Array.AsReadOnly(merged) };
    }

    private static BoardFact RejectedActionFact(Intent intent, string reason) =>
        new(
            BoardIds.ActionRejected,
            intent.ActionKind.Id,
            $"Action {intent.ActionKind.Id} was rejected: {reason}; " +
            $"targetActor={intent.TargetActorId}; targetObject={intent.TargetObjectId}; " +
            $"destination={intent.DestinationId}; durationMs={intent.DurationMs}; " +
            $"untilModelTimeMs={intent.UntilModelTimeMs}.");

    private static BoardFact CellarSealedFact() =>
        new(BoardIds.CellarSealedKnown, BoardIds.Cellar, "The cellar is sealed.");

    private static BoardFact KeyLocationFact(string? publicPlaceId = null) =>
        new(
            BoardIds.KeyLocationKnown,
            BoardIds.BrassKey,
            publicPlaceId is null
                ? "The brass key is in an actor's possession."
                : $"The brass key was placed in the public environment at {publicPlaceId}.");

    private static BoardFact LastOutcome(string text) =>
        new(BoardIds.LastActionOutcome, RelatedId: null, Text: text);

    private static string ObservationOutcome(ActorObservedEvent observed) =>
        observed.TargetObjectId is null
            ? $"You successfully observed the current place; " +
              $"the event reported {observed.LearnedFacts.Count} visible facts."
            : $"You successfully inspected {observed.TargetObjectId}; " +
              $"the event reported {observed.LearnedFacts.Count} inspection facts.";

    private static FirstBoardWorld UpdateActor(
        FirstBoardWorld world,
        string actorId,
        Func<BoardActor, BoardActor> update) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Key == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardWorld UpdateActor(
        FirstBoardWorld world,
        long actorId,
        Func<BoardActor, BoardActor> update) =>
        world with
        {
            Actors = Array.AsReadOnly(world.Actors
                .Select(actor => actor.Id == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardWorld UpdateObject(
        FirstBoardWorld world,
        string objectId,
        Func<BoardObject, BoardObject> update) =>
        world with
        {
            Objects = Array.AsReadOnly(world.Objects
                .Select(item => item.Key == objectId ? update(item) : item)
                .OrderBy(item => item.Id)
                .ToArray()),
        };
}
