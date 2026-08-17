namespace DramaBoard.Kernel.Simulation;

/// <summary>Explains why a simulation run returned control to its host.</summary>
public enum StopReason
{
    /// <summary>No forecast candidate remains.</summary>
    Exhausted,

    /// <summary>The next forecast candidate is later than the inclusive run boundary.</summary>
    BoundaryReached,

    /// <summary>A committed resolve batch contained at least one decision request event.</summary>
    DecisionRequired,
}
