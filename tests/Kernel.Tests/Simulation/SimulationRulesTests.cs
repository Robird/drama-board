using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Kernel.Tests.Simulation;

public sealed class SimulationRulesTests
{
    [Fact]
    public void Constructor_PositiveBudgetPreservesValuesAndUsesValueEquality()
    {
        var first = new SimulationRules(worldSeed: 42, maxTransitionsPerModelTime: 100);
        var equal = new SimulationRules(worldSeed: 42, maxTransitionsPerModelTime: 100);

        Assert.Equal(42UL, first.WorldSeed);
        Assert.Equal(100, first.MaxTransitionsPerModelTime);
        Assert.Equal(first, equal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveBudgetThrowsArgumentOutOfRangeException(int budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SimulationRules(worldSeed: 0, maxTransitionsPerModelTime: budget));
    }
}
