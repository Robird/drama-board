using DramaBoard.Kernel.Journal;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence;

/// <summary>Checks constructor-only and complete-boundary conditions before exposing a stored graph.
/// Cross-domain gameplay invariants remain owned by FirstBoardReducer.Validate.</summary>
internal static class FirstBoardStoredGraphValidation
{
    internal static void State(FirstBoardWorld world)
    {
        try
        {
            StateCore(world);
        }
        catch (Exception error) when (error is NullReferenceException or ArgumentNullException)
        {
            throw new InvalidDataException("The saved FirstBoard graph contains a missing required object or collection.", error);
        }
    }

    internal static void Event(OccurrenceEvent<FirstBoardFact> occurrence)
    {
        occurrence.Validate();
        foreach (FirstBoardFact fact in occurrence.Facts)
        {
            switch (fact)
            {
                case GameBoardFact { Value: null }:
                case SpatialBoardFact { Value: null }:
                    throw new InvalidDataException("An occurrence fact must contain its domain payload.");
                case GameBoardFact { Value: PassageEncounterOpenedEvent opened }:
                    Contact(opened.ContactKey);
                    if (!Enum.IsDefined(opened.Kind))
                    {
                        throw new InvalidDataException("A recorded passage encounter has an unknown kind.");
                    }
                    break;
                case GameBoardFact { Value: PassageEncounterResolvedEvent resolved }:
                    Contact(resolved.ContactKey);
                    if (!Enum.IsDefined(resolved.Resolution))
                    {
                        throw new InvalidDataException("A recorded passage encounter has an unknown resolution.");
                    }
                    break;
                case GameBoardFact { Value: ActorObservedEvent observed }:
                    // The reducer may merge repeated learned entries. Uniqueness belongs
                    // to the resulting knowledge state, not the ordered input facts.
                    Knowledge(observed.LearnedFacts, requireUnique: false);
                    break;
                case SpatialBoardFact { Value: PassageContactOccurredFact contact }:
                    Contact(contact.ContactKey);
                    if (!Enum.IsDefined(contact.Kind))
                    {
                        throw new InvalidDataException("A recorded passage contact has an unknown kind.");
                    }
                    break;
            }
        }
    }

    private static void StateCore(FirstBoardWorld world)
    {
        if (world is null || world.Game is null || world.Spatial is null)
        {
            throw new InvalidDataException("A saved State requires both Game and Spatial worlds.");
        }
        long previousActorId = 0;
        foreach (BoardActor actor in world.Actors)
        {
            if (actor is null || actor.Id <= previousActorId || string.IsNullOrWhiteSpace(actor.Key) ||
                actor.Generation < 0 || actor.DecisionSequence < 0)
            {
                throw new InvalidDataException("Actors require positive increasing IDs, valid keys and nonnegative generations/sequences.");
            }
            previousActorId = actor.Id;
            if (actor.Activity is { } activity && activity.Due < world.Now)
            {
                throw new InvalidDataException("A completed State cannot retain an already past-due activity.");
            }
            if (actor.TravelGoalPlaceId is { } goal && string.IsNullOrWhiteSpace(goal.Value))
            {
                throw new InvalidDataException("A travel goal requires an initialized place ID.");
            }
            Knowledge(actor.KnownFacts);
        }
        long previousObjectId = 0;
        foreach (BoardObject item in world.Objects)
        {
            if (item is null || item.Id <= previousObjectId || string.IsNullOrWhiteSpace(item.Key) ||
                item.OwnerActorId is <= 0)
            {
                throw new InvalidDataException("Objects require positive increasing IDs, valid keys and valid optional owners.");
            }
            previousObjectId = item.Id;
        }
        if (world.Game.PendingEncounter is { } pending)
        {
            Contact(pending.ContactKey);
            if (!Enum.IsDefined(pending.Kind))
            {
                throw new InvalidDataException("A pending passage encounter has an unknown kind.");
            }
            // A committed arrival may make this pending key stale. Its next cleanup
            // occurrence owns that transition; do not require current segments here.
        }
        foreach (SpatialEntity entity in world.Spatial.Entities)
        {
            if (entity is null || string.IsNullOrWhiteSpace(entity.Id.Value) || entity.MovementGeneration < 0)
            {
                throw new InvalidDataException("A spatial entity requires an ID and nonnegative movement generation.");
            }
            switch (entity.Location)
            {
                case AtPlaceLocation atPlace when string.IsNullOrWhiteSpace(atPlace.PlaceId.Value):
                    throw new InvalidDataException("A place location requires an initialized place ID.");
                case TraversingLocation traversal:
                    if (string.IsNullOrWhiteSpace(traversal.PassageId.Value) ||
                        string.IsNullOrWhiteSpace(traversal.TargetPlaceId.Value) ||
                        traversal.AnchorOffset < 0 || traversal.SpeedSnapshot <= 0 ||
                        traversal.ArrivalDue <= traversal.AnchorTime ||
                        traversal.AnchorTime > world.Now || traversal.ArrivalDue < world.Now)
                    {
                        throw new InvalidDataException("A traversal must have valid anchored timing at this completed boundary.");
                    }
                    break;
                case null:
                    throw new InvalidDataException("A spatial entity requires a location.");
            }
        }
        foreach (PassageEntryAccessOverride entry in world.Spatial.PassageEntryAccessOverrides)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.PassageId.Value))
            {
                throw new InvalidDataException("An entry override requires an initialized passage ID.");
            }
        }
        foreach (ScheduledPassageEntryChange schedule in world.Spatial.ScheduledPassageEntryChanges)
        {
            if (schedule is null || string.IsNullOrWhiteSpace(schedule.PassageId.Value) || schedule.Due < world.Now)
            {
                throw new InvalidDataException("A saved passage schedule must have an ID and must not be past due.");
            }
        }
        foreach (PassageContactKey contact in world.Spatial.ConsumedContacts)
        {
            Contact(contact);
        }
    }

    private static void Knowledge(IEnumerable<BoardFact> facts, bool requireUnique = true)
    {
        if (facts is null)
        {
            throw new InvalidDataException("Knowledge must be a complete collection.");
        }
        HashSet<(string Kind, string? RelatedId)> keys = [];
        foreach (BoardFact fact in facts)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.Kind) || fact.Text is null ||
                (requireUnique && !keys.Add((fact.Kind, fact.RelatedId))))
            {
                throw new InvalidDataException("Knowledge requires unique kind/subject entries and their complete text.");
            }
        }
    }

    private static void Contact(PassageContactKey contact)
    {
        if (contact is null || string.IsNullOrWhiteSpace(contact.PassageId.Value) ||
            string.IsNullOrWhiteSpace(contact.EntityA.Value) || string.IsNullOrWhiteSpace(contact.EntityB.Value) ||
            contact.EntityA.CompareTo(contact.EntityB) >= 0 ||
            contact.MovementGenerationA < 0 || contact.MovementGenerationB < 0)
        {
            throw new InvalidDataException("A contact key requires canonical distinct entities and nonnegative segment generations.");
        }
    }
}
