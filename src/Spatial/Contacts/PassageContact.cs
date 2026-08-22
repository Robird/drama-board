namespace DramaBoard.Spatial;

/// <summary>Classifies one exact intersection between two active passage segments.</summary>
public enum PassageContactKind
{
    HeadOnMeeting = 0,
    Overtake = 1,
}

/// <summary>Identifies one unordered pair of current movement segments on a passage.</summary>
public sealed record PassageContactKey : IComparable<PassageContactKey>
{
    public PassageContactKey(
        PassageId passageId,
        EntityId entityA,
        long movementGenerationA,
        EntityId entityB,
        long movementGenerationB)
    {
        SpatialIdentifier.Require(passageId, nameof(passageId));
        SpatialIdentifier.Require(entityA, nameof(entityA));
        SpatialIdentifier.Require(entityB, nameof(entityB));
        if (movementGenerationA < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementGenerationA),
                "Movement generation cannot be negative.");
        }

        if (movementGenerationB < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(movementGenerationB),
                "Movement generation cannot be negative.");
        }

        if (entityA == entityB)
        {
            throw new ArgumentException("A passage contact requires two different entities.", nameof(entityB));
        }

        PassageId = passageId;
        if (entityA.CompareTo(entityB) < 0)
        {
            EntityA = entityA;
            MovementGenerationA = movementGenerationA;
            EntityB = entityB;
            MovementGenerationB = movementGenerationB;
        }
        else
        {
            EntityA = entityB;
            MovementGenerationA = movementGenerationB;
            EntityB = entityA;
            MovementGenerationB = movementGenerationA;
        }
    }

    public PassageId PassageId { get; }

    public EntityId EntityA { get; }

    public long MovementGenerationA { get; }

    public EntityId EntityB { get; }

    public long MovementGenerationB { get; }

    public int CompareTo(PassageContactKey? other)
    {
        if (other is null)
        {
            return 1;
        }

        int comparison = PassageId.CompareTo(other.PassageId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = EntityA.CompareTo(other.EntityA);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = MovementGenerationA.CompareTo(other.MovementGenerationA);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = EntityB.CompareTo(other.EntityB);
        return comparison != 0
            ? comparison
            : MovementGenerationB.CompareTo(other.MovementGenerationB);
    }

    internal bool References(EntityId entityId, long movementGeneration) =>
        (EntityA == entityId && MovementGenerationA == movementGeneration) ||
        (EntityB == entityId && MovementGenerationB == movementGeneration);
}
