using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>The sole personal ending authority. Location is history at At, never an active spatial entity.</summary>
[DurableType("DramaBoard.ResourceCycle.CharacterEnding", 1)]
public sealed partial record CycleCharacterEnding(
    [field: DurableField(1)] CycleExpeditionOutcome Outcome,
    [field: DurableField(2)] ModelTime At,
    [field: DurableField(3)] string Reason,
    [field: DurableField(4)] SpatialLocation Location) : IDurableObject;

/// <summary>Derived host summary, not character knowledge and never independently persisted.</summary>
public enum CycleExpeditionSummary { Exploring, AllEscaped, PartiallyEscaped, NoneEscaped }
