using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

public static class BoardIds
{
    public const string Tavern = "tavern";
    public const string Market = "market";
    public const string CellarGate = "cellar-gate-front";
    public const string Cellar = "cellar";

    public const string TavernMarketRoad = "tavern-market-road";
    public const string TavernMarketFerry = "tavern-market-ferry";
    public const string MarketTavernCart = "market-tavern-cart";
    public const string MarketCellarApproach = "market-cellar-approach";
    public const string CellarGatePassage = "cellar-gate-passage";

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
    public const long TravelSpeed = 1;
    public const long DeadlineTicks = 3_600_000;
    public const long DefaultWaitTicks = 60_000;
    public const long RandomRunBoundaryTicks = 4_200_000;
}

public sealed record BoardFact(string Kind, string? RelatedId, string Text);

public sealed record BoardWaitActivity(ModelTime Due);

public sealed record BoardActor(
    long Id,
    string Key,
    long Generation,
    long DecisionSequence,
    BoardWaitActivity? Activity,
    IReadOnlyList<BoardFact> KnownFacts);

public sealed record BoardObject(
    long Id,
    string Key,
    long? OwnerActorId);

public sealed record FirstBoardGameState(
    ulong WorldSeed,
    long NextPersistentId,
    ModelTime Now,
    IReadOnlyList<BoardActor> Actors,
    IReadOnlyList<BoardObject> Objects,
    bool CellarSealed,
    bool ChestOpened)
{
    public BoardActor Actor(string actorId) =>
        Actors.Single(actor => actor.Key == actorId);

    public BoardActor Actor(long actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    public BoardObject Object(string objectId) =>
        Objects.Single(item => item.Key == objectId);

    public bool IsIdle(BoardActor actor) => actor.Activity is null;
}

/// <summary>Owns the complete Game + objective Graph Spatial committed world.</summary>
public sealed record FirstBoardWorld(
    FirstBoardGameState Game,
    GraphSpatialState Spatial)
{
    public ulong WorldSeed => Game.WorldSeed;
    public ModelTime Now => Game.Now;
    public IReadOnlyList<BoardActor> Actors => Game.Actors;
    public IReadOnlyList<BoardObject> Objects => Game.Objects;
    public bool CellarSealed => Game.CellarSealed;
    public bool ChestOpened => Game.ChestOpened;

    public static FirstBoardWorld CreateInitial(ulong worldSeed) =>
        ScenarioInstance.CreateDefault(worldSeed).CreateInitialWorld();

    public BoardActor Actor(string actorId) => Game.Actor(actorId);
    public BoardActor Actor(long actorId) => Game.Actor(actorId);
    public BoardObject Object(string objectId) => Game.Object(objectId);

    public bool IsAtPlace(string entityId, PlaceId placeId) =>
        Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity) &&
        entity!.Location is AtPlaceLocation atPlace &&
        atPlace.PlaceId == placeId;

    public bool TryGetPlace(string entityId, out PlaceId placeId)
    {
        if (Spatial.TryGetEntity(new EntityId(entityId), out SpatialEntity? entity) &&
            entity!.Location is AtPlaceLocation atPlace)
        {
            placeId = atPlace.PlaceId;
            return true;
        }

        placeId = default;
        return false;
    }

    public bool IsReadyForDecision(BoardActor actor) =>
        Game.IsIdle(actor) && TryGetPlace(actor.Key, out _);

    public bool AreCoLocated(string firstEntityId, string secondEntityId) =>
        TryGetPlace(firstEntityId, out PlaceId first) &&
        TryGetPlace(secondEntityId, out PlaceId second) &&
        first == second;
}

public abstract record BoardEventPayload;

/// <summary>Records Game decision progress without owning location or arrival time.</summary>
public sealed record ActorTravelStartedEvent(
    string ActorId,
    string ExitId,
    string DestinationId) : BoardEventPayload;

public sealed record TicketConsumedEvent(
    string ActorId,
    string TicketObjectId) : BoardEventPayload;

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

/// <summary>Exact Host fact union; every batch may combine Game and Spatial facts.</summary>
public abstract record FirstBoardFact;

public sealed record GameBoardFact(BoardEventPayload Value) : FirstBoardFact;

public sealed record SpatialBoardFact(GraphSpatialFact Value) : FirstBoardFact;

/// <summary>Folds the Host union while leaving cross-domain validation to the batch boundary.</summary>
public sealed class FirstBoardReducer
{
    private readonly GraphDefinition _definition;
    private readonly GraphSpatialReducer _spatialReducer;

    public FirstBoardReducer(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definition = definition;
        _spatialReducer = new GraphSpatialReducer(definition);
    }

    public FirstBoardWorld Apply(
        FirstBoardWorld world,
        LogicalInstant instant,
        FirstBoardFact fact)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);

        FirstBoardWorld updated = fact switch
        {
            GameBoardFact game => world with
            {
                Game = ApplyGame(world, instant, game.Value),
            },
            SpatialBoardFact spatial => world with
            {
                Spatial = _spatialReducer.Apply(world.Spatial, instant, spatial.Value),
            },
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard fact '{fact.GetType().Name}'."),
        };

        return updated with
        {
            Game = updated.Game with { Now = instant.ModelTime },
        };
    }

    public void Validate(FirstBoardWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        GraphSpatialStateValidator.ValidateComplete(_definition, world.Spatial);
        EnsureUnique(world.Actors.Select(actor => actor.Id), "actor id");
        EnsureUnique(world.Actors.Select(actor => actor.Key), "actor key");
        EnsureUnique(world.Objects.Select(item => item.Id), "object id");
        EnsureUnique(world.Objects.Select(item => item.Key), "object key");

        var actorIds = world.Actors.Select(actor => actor.Id).ToHashSet();
        if (world.Objects.Any(item =>
                item.OwnerActorId is long ownerId && !actorIds.Contains(ownerId)))
        {
            throw new InvalidOperationException("A FirstBoard object owner must exist.");
        }

        foreach (BoardActor actor in world.Actors)
        {
            if (!world.Spatial.TryGetEntity(new EntityId(actor.Key), out SpatialEntity? entity))
            {
                throw new InvalidOperationException(
                    $"Actor '{actor.Key}' must have one objective Spatial entity.");
            }

            if (actor.Activity is not null && entity!.Location is not AtPlaceLocation)
            {
                throw new InvalidOperationException(
                    $"Traversing actor '{actor.Key}' cannot also own a Wait activity.");
            }
        }

        foreach (BoardObject item in world.Objects)
        {
            bool hasSpatialEntity = world.Spatial.TryGetEntity(new EntityId(item.Key), out _);
            if (item.OwnerActorId is not null && hasSpatialEntity)
            {
                throw new InvalidOperationException(
                    $"Owned object '{item.Key}' cannot also have a standalone Spatial location.");
            }
        }
    }

    private static FirstBoardGameState ApplyGame(
        FirstBoardWorld world,
        LogicalInstant instant,
        BoardEventPayload fact)
    {
        FirstBoardGameState game = world.Game;
        return fact switch
        {
            ActorTravelStartedEvent started =>
                UpdateActor(game, started.ActorId, actor =>
                    AddFacts(CompleteDecision(actor), [LastOutcome(
                        $"Your travel via {started.ExitId} to {started.DestinationId} was accepted.")])),
            TicketConsumedEvent consumed =>
                ConsumeTicket(game, consumed),
            ActorWaitStartedEvent waited =>
                UpdateActor(game, waited.ActorId, actor =>
                    AddFacts(CompleteDecision(actor) with
                    {
                        Activity = new BoardWaitActivity(waited.CompleteAt),
                    }, [LastOutcome(
                        $"Your wait was accepted until model time {waited.CompleteAt.Ticks}ms.")])),
            ActorWaitedEvent waited =>
                UpdateActor(game, waited.ActorId, actor =>
                    AddFacts(CompleteActivity(actor), [LastOutcome(
                        $"You successfully finished waiting at model time {instant.ModelTime.Ticks}ms.")])),
            ActorSpokeEvent spoke =>
                ApplySpoke(game, spoke),
            ActorObservedEvent observed =>
                UpdateActor(game, observed.ActorId, actor =>
                    AddFacts(
                        CompleteDecision(actor),
                        observed.LearnedFacts.Append(LastOutcome(ObservationOutcome(observed))))),
            ObjectTakenEvent taken =>
                ApplyTaken(game, taken),
            ObjectPlacedEvent placed =>
                ApplyPlaced(world, game, placed),
            ObjectGivenEvent given =>
                ApplyGiven(game, given),
            ObjectShownEvent shown =>
                ApplyShown(game, shown),
            ChestOpenedEvent opened =>
                ApplyChestOpened(game, opened),
            ActionRejectedEvent rejected =>
                ApplyRejected(game, rejected),
            CellarSealedEvent =>
                ApplyCellarSealed(world, game),
            _ => throw new InvalidOperationException(
                $"Unknown FirstBoard Game fact '{fact.GetType().Name}'."),
        };
    }

    private static FirstBoardGameState ApplyRejected(
        FirstBoardGameState game,
        ActionRejectedEvent rejected)
    {
        var learnedFacts = new List<BoardFact>
        {
            RejectedActionFact(rejected.RejectedIntent, rejected.Reason),
            LastOutcome($"Your {rejected.RejectedIntent.ActionKind.Id} action was rejected: " +
                $"{rejected.Reason}."),
        };
        if (rejected.RejectedIntent.ActionKind == ActionKinds.Travel &&
            rejected.Reason == "cellar is sealed")
        {
            learnedFacts.Add(CellarSealedFact());
        }

        return UpdateActor(game, rejected.ActorId, actor =>
            AddFacts(CompleteDecision(actor), learnedFacts));
    }

    private static FirstBoardGameState ApplyCellarSealed(
        FirstBoardWorld world,
        FirstBoardGameState game)
    {
        FirstBoardGameState updated = game with { CellarSealed = true };
        foreach (BoardActor witness in game.Actors.Where(actor =>
                     world.IsAtPlace(actor.Key, new PlaceId(BoardIds.Cellar))))
        {
            updated = UpdateActor(updated, witness.Id, actor =>
                AddFacts(actor, [CellarSealedFact()]));
        }

        return updated;
    }

    private static FirstBoardGameState ApplySpoke(
        FirstBoardGameState game,
        ActorSpokeEvent spoke)
    {
        BoardActor speaker = game.Actor(spoke.ActorId);
        BoardFact? sharedFact = spoke.SharedFactKind is null
            ? null
            : speaker.KnownFacts.Single(fact => fact.Kind == spoke.SharedFactKind);
        FirstBoardGameState afterSpeaker = UpdateActor(game, spoke.ActorId, actor =>
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

            if (actor.Activity is not null)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {spoke.ActorId} spoke to you."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardGameState ApplyTaken(
        FirstBoardGameState game,
        ObjectTakenEvent taken)
    {
        BoardActor actor = game.Actor(taken.ActorId);
        FirstBoardGameState updated = UpdateObject(game, taken.ObjectId, item => item with
        {
            OwnerActorId = actor.Id,
        });
        return UpdateActor(updated, taken.ActorId, current =>
            AddFacts(CompleteDecision(current),
            [
                KeyLocationFact(),
                LastOutcome($"You successfully took {taken.ObjectId}."),
            ]));
    }

    private static FirstBoardGameState ApplyPlaced(
        FirstBoardWorld world,
        FirstBoardGameState game,
        ObjectPlacedEvent placed)
    {
        FirstBoardGameState updated = UpdateObject(game, placed.ObjectId, item => item with
        {
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

        foreach (BoardActor witness in game.Actors.Where(actor =>
                     actor.Key != placed.ActorId &&
                     world.IsAtPlace(actor.Key, new PlaceId(placed.PlaceId))))
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

                if (actor.Activity is not null)
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

    private static FirstBoardGameState ApplyGiven(
        FirstBoardGameState game,
        ObjectGivenEvent given)
    {
        BoardActor target = game.Actor(given.TargetActorId);
        FirstBoardGameState updated = UpdateObject(game, given.ObjectId, item => item with
        {
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

        return UpdateActor(updated, given.TargetActorId, actor =>
            AddFacts(actor, targetFacts));
    }

    private static FirstBoardGameState ApplyShown(
        FirstBoardGameState game,
        ObjectShownEvent shown)
    {
        FirstBoardGameState updated = UpdateActor(game, shown.ActorId, actor =>
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
            if (actor.Activity is not null)
            {
                facts.Add(LastOutcome(
                    $"Your wait was interrupted because {shown.ActorId} showed you {shown.ObjectId}."));
                actor = CompleteActivity(actor);
            }

            return AddFacts(actor, facts);
        });
    }

    private static FirstBoardGameState ApplyChestOpened(
        FirstBoardGameState game,
        ChestOpenedEvent opened)
    {
        FirstBoardGameState updated = UpdateObject(
            game with { ChestOpened = true },
            BoardIds.DuchessLetter,
            letter => letter with
            {
                OwnerActorId = game.Actor(opened.ActorId).Id,
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

    private static FirstBoardGameState ConsumeTicket(
        FirstBoardGameState game,
        TicketConsumedEvent consumed)
    {
        BoardActor actor = game.Actor(consumed.ActorId);
        BoardObject ticket = game.Object(consumed.TicketObjectId);
        if (ticket.OwnerActorId != actor.Id)
        {
            throw new InvalidOperationException(
                $"Ticket '{ticket.Key}' is not owned by actor '{actor.Key}'.");
        }

        return game with
        {
            Objects = Array.AsReadOnly(game.Objects
                .Where(item => item.Id != ticket.Id)
                .ToArray()),
        };
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
            $"exit={intent.ExitId}; destination={intent.DestinationId}; durationMs={intent.DurationMs}; " +
            $"untilModelTimeMs={intent.UntilModelTimeMs}.");

    private static BoardFact CellarSealedFact() =>
        new(BoardIds.CellarSealedKnown, BoardIds.Cellar, "The cellar is sealed against entry.");

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

    private static FirstBoardGameState UpdateActor(
        FirstBoardGameState game,
        string actorId,
        Func<BoardActor, BoardActor> update) =>
        game with
        {
            Actors = Array.AsReadOnly(game.Actors
                .Select(actor => actor.Key == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardGameState UpdateActor(
        FirstBoardGameState game,
        long actorId,
        Func<BoardActor, BoardActor> update) =>
        game with
        {
            Actors = Array.AsReadOnly(game.Actors
                .Select(actor => actor.Id == actorId ? update(actor) : actor)
                .OrderBy(actor => actor.Id)
                .ToArray()),
        };

    private static FirstBoardGameState UpdateObject(
        FirstBoardGameState game,
        string objectId,
        Func<BoardObject, BoardObject> update) =>
        game with
        {
            Objects = Array.AsReadOnly(game.Objects
                .Select(item => item.Key == objectId ? update(item) : item)
                .OrderBy(item => item.Id)
                .ToArray()),
        };

    private static void EnsureUnique<T>(IEnumerable<T> values, string description)
    {
        var known = new HashSet<T>();
        if (values.Any(value => !known.Add(value)))
        {
            throw new InvalidOperationException(
                $"FirstBoard {description} values must be unique.");
        }
    }
}
