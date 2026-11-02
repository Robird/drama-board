using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>The sole personal ending authority. Location is history at At, never an active spatial entity.</summary>
[DurableType("DramaBoard.Playground.CharacterEnding", 1)]
public sealed partial record PlaygroundCharacterEnding(
    [field: DurableField(1)] PlaygroundExpeditionOutcome Outcome,
    [field: DurableField(2)] ModelTime At,
    [field: DurableField(3)] string Reason,
    [field: DurableField(4)] SpatialLocation Location) : IDurableObject;

/// <summary>Derived host summary, not character knowledge and never independently persisted.</summary>
public enum PlaygroundExpeditionSummary { Exploring, AllEscaped, PartiallyEscaped, NoneEscaped }
