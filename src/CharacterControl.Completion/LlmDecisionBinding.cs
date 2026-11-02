using Atelia.Completion.Abstractions;

namespace DramaBoard.CharacterControl.Completion;

/// <summary>A borrowed client and model fixed for one decision, with optional host connection identity.</summary>
/// <remarks>The composing host owns client lifetime. This binding never enters character memory.</remarks>
public sealed record LlmDecisionBinding(ICompletionClient Client, string ModelId, string? ConnectionId = null);
