using Atelia.DurableGraph;

namespace DramaBoard.Spatial;

/// <summary>Classifies one exact intersection between two active passage segments.</summary>
[DurableType("DramaBoard.Spatial.PassageContactKind", 1)]
public enum PassageContactKind
{
    HeadOnMeeting = 0,
    Overtake = 1,
}

/// <summary>Identifies one unordered pair of current movement segments on a passage.</summary>
[DurableType("DramaBoard.Spatial.PassageContactKey", 1)]
public sealed partial class PassageContactKey : IDurableObject, IComparable<PassageContactKey>, IEquatable<PassageContactKey>
{
    [DurableField(1)] private PassageId _passageId;
    [DurableField(2)] private EntityId _entityA;
    [DurableField(3)] private long _movementGenerationA;
    [DurableField(4)] private EntityId _entityB;
    [DurableField(5)] private long _movementGenerationB;
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

        _passageId = passageId;
        if (entityA.CompareTo(entityB) < 0)
        {
            _entityA = entityA; _movementGenerationA = movementGenerationA;
            _entityB = entityB; _movementGenerationB = movementGenerationB;
        }
        else
        {
            _entityA = entityB; _movementGenerationA = movementGenerationB;
            _entityB = entityA; _movementGenerationB = movementGenerationA;
        }
    }

    public PassageId PassageId => _passageId;

    public EntityId EntityA => _entityA;

    public long MovementGenerationA => _movementGenerationA;

    public EntityId EntityB => _entityB;

    public long MovementGenerationB => _movementGenerationB;

    public bool Equals(PassageContactKey? other) => other is not null &&
        PassageId == other.PassageId && EntityA == other.EntityA &&
        MovementGenerationA == other.MovementGenerationA && EntityB == other.EntityB &&
        MovementGenerationB == other.MovementGenerationB;
    public override bool Equals(object? obj) => Equals(obj as PassageContactKey);
    public override int GetHashCode() => HashCode.Combine(PassageId, EntityA, MovementGenerationA, EntityB, MovementGenerationB);
    public static bool operator ==(PassageContactKey? left, PassageContactKey? right) => Equals(left, right);
    public static bool operator !=(PassageContactKey? left, PassageContactKey? right) => !Equals(left, right);

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
