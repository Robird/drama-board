using DramaBoard.Kernel.Time;
using DramaBoard.Protocol;
using DramaBoard.Server.FreePlay;
using DramaBoard.Server.FreePlay.Runtime;
using DramaBoard.Spatial;

namespace DramaBoard.Server.Tests;

public sealed class FreePlayFactUnionTests {
    [Fact]
    public void FoldSpatialFactAdvancesOnlySpatialAndTime() {
        FreePlayScene scene = NewScene();
        FreePlayWorld genesis = scene.Genesis();
        var instant = new LogicalInstant(new ModelTime(100), 1);
        var fact = new SpatialFact(new EntityPlacedFact(new EntityId("npc"), new PlaceId("A")));

        FreePlayWorld folded = scene.Fold(genesis, instant, fact);

        Assert.NotSame(genesis.Spatial, folded.Spatial);
        SpatialEntity placed = folded.Spatial.Entities.Single(entity => entity.Id == new EntityId("npc"));
        AtPlaceLocation location = Assert.IsType<AtPlaceLocation>(placed.Location);
        Assert.Equal(new PlaceId("A"), location.PlaceId);
        Assert.True(ReferenceEquals(genesis.Process, folded.Process));
        Assert.Equal(new AwaitingProgrammerProcess(ProgrammingReason.MissingInstruction), folded.Process.Mode);
        Assert.Equal(new ModelTime(100), folded.EffectiveTime);
    }

    [Fact]
    public void FoldProcessFactAdvancesOnlyProcessAndTime() {
        FreePlayScene scene = NewScene();
        FreePlayWorld genesis = scene.Genesis();
        var instant = new LogicalInstant(new ModelTime(100), 1);
        var fact = new ProcessFact(new ProgramAcceptedFact([new StayInstruction()], new SubjectiveWorkspace("goal")));

        FreePlayWorld folded = scene.Fold(genesis, instant, fact);

        Assert.True(ReferenceEquals(genesis.Spatial, folded.Spatial));
        Assert.Equal(new ReadyProcess(), folded.Process.Mode);
        Assert.Equal(1, folded.Process.ControlRevision);
        Assert.Equal(new SubjectiveWorkspace("goal"), folded.Process.Workspace);
        Assert.True(folded.Process.Pending == InstructionBuffer.Of([new StayInstruction()]));
        Assert.Equal(new ModelTime(100), folded.EffectiveTime);
    }

    [Fact]
    public void ValidateRejectsInvalidProcessState() {
        FreePlayScene scene = NewScene();
        FreePlayWorld genesis = scene.Genesis();
        var invalid = new PlayerProcessState(new SubjectiveWorkspace(""), InstructionBuffer.Empty, new ReadyProcess(), 0);
        var world = new FreePlayWorld(genesis.Spatial, invalid, genesis.EffectiveTime);

        Assert.Throws<InvalidOperationException>(() => scene.Validate(world));
    }

    private static FreePlayScene NewScene() => new(
        "fact-union-tests",
        (_, _, _) => throw new InvalidOperationException("Fact-union tests never make decisions."));
}
