using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class CoreContractTests
{
    [Fact]
    public void WorldVersion_UsesLineageAndLongTransitionCount()
    {
        var first = new WorldVersion(lineageId: 7, transitionCount: (long)int.MaxValue + 1);
        var equal = new WorldVersion(lineageId: 7, transitionCount: (long)int.MaxValue + 1);
        var otherLineage = new WorldVersion(lineageId: 8, transitionCount: first.TransitionCount);

        Assert.Equal(first, equal);
        Assert.NotEqual(first, otherLineage);
        Assert.Equal((long)int.MaxValue + 1, first.TransitionCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldVersion(1, -1));
    }

    [Fact]
    public void TransitionDraft_CopiesFactsAndRejectsEmptyOrNullFacts()
    {
        string[] facts = ["a", "b"];
        var draft = new TransitionDraft<string>(facts);
        facts[0] = "mutated";

        Assert.Equal(["a", "b"], draft.Facts);
        Assert.Throws<ArgumentNullException>(() => new TransitionDraft<string>(null!));
        Assert.Throws<ArgumentException>(() => new TransitionDraft<string>([]));
        Assert.Throws<ArgumentException>(() => new TransitionDraft<string>(["a", null!]));
    }

    [Fact]
    public void StepStatus_HasOnlyThreeNormalOutcomes()
    {
        Assert.Equal(
            [StepStatus.Committed, StepStatus.Exhausted, StepStatus.BoundaryReached],
            Enum.GetValues<StepStatus>());
    }
}
