using Atelia.DurableGraph;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.FirstBoard.Persistence;

/// <summary>The complete world and finite scheduling cursor published together as one State.</summary>
[DurableType("DramaBoard.FirstBoard.CommittedState", 1)]
public sealed partial class FirstBoardCommittedState : IDurableObject
{
    [DurableField(1)] private readonly FirstBoardWorld _world;
    [DurableField(2)] private readonly KernelCursor _cursor;
    [DurableField(3)] private readonly FirstBoardRunBinding _binding;

    internal FirstBoardCommittedState(FirstBoardWorld world, KernelCursor cursor, FirstBoardRunBinding binding)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(binding);
        _world = world;
        _cursor = cursor;
        _binding = binding;
    }

    public FirstBoardWorld World => _world;
    public KernelCursor Cursor => _cursor;
    public FirstBoardRunBinding Binding => _binding;
}
