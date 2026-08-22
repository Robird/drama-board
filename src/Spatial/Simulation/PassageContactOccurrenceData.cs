namespace DramaBoard.Spatial;

/// <summary>Captures one current-segment passage contact selected by its canonical pair.</summary>
public sealed record PassageContactOccurrenceData(
    PassageContactKey ContactKey,
    PassageContactKind Kind);
