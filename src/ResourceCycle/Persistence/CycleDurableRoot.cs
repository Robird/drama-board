using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.ResourceCycle;

/// <summary>Business subgraphs and their completed Kernel boundary; predictions are rebuilt after loading.</summary>
[DurableType("DramaBoard.ResourceCycle.Root", 2)]
public sealed partial class CycleDurableRoot : IDurableObject {
    [DurableField(1)] private readonly CycleBusinessState _business;
    [DurableField(2)] private readonly KernelCursor _cursor;
    [DurableField(3)] private readonly int _gameRevision;

    public CycleDurableRoot(CycleBusinessState business, KernelCursor cursor)
        : this(business, cursor, CycleGameRevision.Current) {
        Validate();
    }

    private CycleDurableRoot(CycleBusinessState business, KernelCursor cursor, int gameRevision) {
        _business = business;
        _cursor = cursor;
        _gameRevision = gameRevision;
        ValidateStructure();
    }

    public CycleBusinessState Business => _business;
    public KernelCursor Cursor => _cursor;
    public int GameRevision => _gameRevision;

    /// <summary>Creates an intermediate representation without applying current business rules.</summary>
    /// <remarks>Candidates require full validation before runtime use or publication.</remarks>
    internal static CycleDurableRoot CreateRevisionCandidate(
        CycleBusinessState business, KernelCursor cursor, int gameRevision) => new(business, cursor, gameRevision);

    /// <summary>Also validates restored objects, whose constructors were not executed.</summary>
    public void Validate() {
        ValidateStructure();
        ValidateContent();
        _business.Validate();
    }

    internal void ValidateContent() {
        if (_business.Definition is null) { throw new InvalidDataException("The saved world has no content definition."); }
        try { _business.Definition.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) {
            throw new InvalidDataException("The saved world content definition is invalid.", error);
        }
    }

    internal void ValidateStructure() {
        ArgumentNullException.ThrowIfNull(_business);
        if (_gameRevision <= 0) {
            throw new InvalidDataException("A ResourceCycle State requires a positive GameRevision.");
        }
        _cursor.Validate();
        if (_business.Time != _cursor.CurrentModelTime) {
            throw new InvalidDataException("The world time must equal its completed Kernel cursor time.");
        }
    }
}
