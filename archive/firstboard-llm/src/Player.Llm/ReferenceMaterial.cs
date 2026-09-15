namespace DramaBoard.Player.Llm;

/// <summary>Describes stable source material an actor can reread without asserting that its content is true.</summary>
public sealed record ReferenceMaterial(
    string Id,
    string Source,
    string Content);
