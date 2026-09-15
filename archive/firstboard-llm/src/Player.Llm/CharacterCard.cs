namespace DramaBoard.Player.Llm;

/// <summary>Describes the stable dramatic identity supplied to an LLM Player.</summary>
public sealed record CharacterCard(
    string Name,
    string Personality,
    string Goal,
    string SpeakingStyle);
