namespace DramaBoard.Protocol;

/// <summary>Describes why the runtime requests a program from the dynamic programmer.</summary>
public enum ProgrammingReason {
    /// <summary>The actor has no instruction left to execute.</summary>
    MissingInstruction,

    /// <summary>The actor's move through a passage was blocked.</summary>
    MoveBlocked,

    /// <summary>The actor finished its think instruction.</summary>
    ThinkCompleted
}
