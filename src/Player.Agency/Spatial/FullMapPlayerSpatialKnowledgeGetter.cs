using DramaBoard.Spatial;

namespace DramaBoard.Player.Agency.Spatial;

/// <summary>Supplies the complete immutable objective definition as every subject's known map.</summary>
public sealed class FullMapPlayerSpatialKnowledgeGetter<TWorld> :
    IPlayerSpatialKnowledgeGetter<TWorld>
    where TWorld : notnull {
    private FullMapPlayerSpatialKnowledgeGetter() {
    }

    public static FullMapPlayerSpatialKnowledgeGetter<TWorld> Instance { get; } = new();

    public PlayerSpatialKnowledgeSnapshot GetKnownGraph(
        TWorld committedWorld,
        string subjectId,
        GraphDefinition objectiveGraph) {
        ArgumentNullException.ThrowIfNull(committedWorld);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (!StringComparer.Ordinal.Equals(subjectId, subjectId.Trim())) {
            throw new ArgumentException(
                "Spatial knowledge subject identifier cannot have leading or trailing whitespace.",
                nameof(subjectId));
        }

        return PlayerSpatialKnowledgeSnapshot.FullMap(objectiveGraph);
    }
}
