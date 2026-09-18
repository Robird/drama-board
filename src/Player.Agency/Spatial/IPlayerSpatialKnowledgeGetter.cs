using DramaBoard.Spatial;

namespace DramaBoard.Player.Agency.Spatial;

/// <summary>Projects one subject's usable static spatial knowledge from a committed world.</summary>
public interface IPlayerSpatialKnowledgeGetter<in TWorld>
    where TWorld : notnull {
    PlayerSpatialKnowledgeSnapshot GetKnownGraph(
        TWorld committedWorld,
        string subjectId,
        GraphDefinition objectiveGraph);
}
