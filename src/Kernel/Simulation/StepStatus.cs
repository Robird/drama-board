namespace DramaBoard.Kernel.Simulation;

/// <summary>Describes the normal outcome of one authoritative simulation step.</summary>
public enum StepStatus
{
    /// <summary>Exactly one complete transition was published and installed.</summary>
    Committed,

    /// <summary>The current committed world forecasts no occurrence candidates.</summary>
    Exhausted,

    /// <summary>The unique next candidate is later than the inclusive caller boundary.</summary>
    BoundaryReached,
}
