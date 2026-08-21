namespace DramaBoard.Protocol;

/// <summary>Provides the first-version action identifiers understood by the Player boundary.</summary>
public static class ActionKinds
{
    /// <summary>Identifies travel to a destination.</summary>
    public static ActionKind Travel { get; } = new("action.travel");

    /// <summary>Identifies waiting for a duration or model time.</summary>
    public static ActionKind Wait { get; } = new("action.wait");

    /// <summary>Identifies talking to another actor.</summary>
    public static ActionKind Talk { get; } = new("action.talk");

    /// <summary>Identifies observing the current context or a visible target.</summary>
    public static ActionKind Observe { get; } = new("action.observe");

    /// <summary>Identifies taking an object.</summary>
    public static ActionKind Take { get; } = new("action.take");

    /// <summary>Identifies placing a held object into the actor's current public context.</summary>
    public static ActionKind Put { get; } = new("action.put");

    /// <summary>Identifies giving an object to another actor.</summary>
    public static ActionKind Give { get; } = new("action.give");

    /// <summary>Identifies showing a held object to another actor without transferring it.</summary>
    public static ActionKind Show { get; } = new("action.show");

    /// <summary>Identifies using an object or contextual capability on a target object.</summary>
    public static ActionKind Use { get; } = new("action.use");
}
