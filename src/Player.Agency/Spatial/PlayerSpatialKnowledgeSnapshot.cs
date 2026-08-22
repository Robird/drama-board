using DramaBoard.Spatial;

namespace DramaBoard.Player.Agency.Spatial;

/// <summary>Wraps an immutable exact subgraph of the objective spatial definition.</summary>
public sealed class PlayerSpatialKnowledgeSnapshot
{
    private PlayerSpatialKnowledgeSnapshot(GraphDefinition knownGraph)
    {
        KnownGraph = knownGraph;
    }

    public GraphDefinition KnownGraph { get; }

    public static PlayerSpatialKnowledgeSnapshot FullMap(GraphDefinition objectiveGraph)
    {
        ArgumentNullException.ThrowIfNull(objectiveGraph);
        return new PlayerSpatialKnowledgeSnapshot(objectiveGraph);
    }

    public static PlayerSpatialKnowledgeSnapshot CreateExactSubgraph(
        GraphDefinition objectiveGraph,
        GraphDefinition exactSubgraph)
    {
        ArgumentNullException.ThrowIfNull(objectiveGraph);
        ArgumentNullException.ThrowIfNull(exactSubgraph);

        foreach (PlaceId placeId in exactSubgraph.Places)
        {
            if (!objectiveGraph.Contains(placeId))
            {
                throw new ArgumentException(
                    $"Known Place '{placeId}' does not exist in the objective graph.",
                    nameof(exactSubgraph));
            }
        }

        foreach (PassageDefinition knownPassage in exactSubgraph.Passages)
        {
            if (!objectiveGraph.Contains(knownPassage.Id))
            {
                throw new ArgumentException(
                    $"Known Passage '{knownPassage.Id}' does not exist in the objective graph.",
                    nameof(exactSubgraph));
            }

            PassageDefinition objectivePassage = objectiveGraph.GetPassage(knownPassage.Id);
            if (knownPassage.EndpointA != objectivePassage.EndpointA ||
                knownPassage.EndpointB != objectivePassage.EndpointB ||
                knownPassage.Length != objectivePassage.Length ||
                knownPassage.InitialEntryAccess != objectivePassage.InitialEntryAccess)
            {
                throw new ArgumentException(
                    $"Known Passage '{knownPassage.Id}' differs from its objective definition.",
                    nameof(exactSubgraph));
            }

            if (!exactSubgraph.Contains(knownPassage.EndpointA) ||
                !exactSubgraph.Contains(knownPassage.EndpointB))
            {
                throw new ArgumentException(
                    $"Known Passage '{knownPassage.Id}' requires both endpoints in the known graph.",
                    nameof(exactSubgraph));
            }
        }

        return new PlayerSpatialKnowledgeSnapshot(exactSubgraph);
    }
}
