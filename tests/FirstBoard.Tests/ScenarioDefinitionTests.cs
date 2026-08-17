using DramaBoard.Host;
using DramaBoard.Kernel.Time;

namespace DramaBoard.FirstBoard.Tests;

public sealed class ScenarioDefinitionTests
{
    [Fact]
    public void ScenarioInstance_MutableInputCollectionsChangeAfterConstruction_RemainsFrozen()
    {
        var actors = ScenarioDefinition.Default.Actors.ToList();
        ScenarioDefinition mutable = ScenarioDefinition.Default with { Actors = actors };
        var instance = new ScenarioInstance(mutable, worldSeed: 42);
        string hash = instance.DefinitionSha256;
        string snapshot = FirstBoardScenario.WorldSnapshot(instance.CreateInitialWorld());

        ScenarioActorDefinition alice = actors.Single(actor => actor.Id == BoardIds.Alice);
        actors[0] = alice with { InitialPlaceId = BoardIds.Cellar };

        Assert.Equal(hash, instance.DefinitionSha256);
        Assert.Equal(snapshot, FirstBoardScenario.WorldSnapshot(instance.CreateInitialWorld()));
        Assert.Equal(BoardIds.Tavern, instance.Definition.Actor(BoardIds.Alice).InitialPlaceId);
    }

    [Fact]
    public void ScenarioHashes_DefinitionMutationAndSeed_HaveSeparatedIdentities()
    {
        ScenarioDefinition original = ScenarioDefinition.Default;
        ScenarioActorDefinition alice = original.Actor(BoardIds.Alice);
        ScenarioReferenceMaterialDefinition firstMaterial = alice.Role.ReferenceMaterials[0];
        ScenarioActorDefinition changedAlice = alice with
        {
            Role = alice.Role with
            {
                ReferenceMaterials =
                [
                    firstMaterial with { Content = firstMaterial.Content + "（变体）" },
                    .. alice.Role.ReferenceMaterials.Skip(1),
                ],
            },
        };
        ScenarioDefinition changedMaterial = original with
        {
            Actors =
            [
                changedAlice,
                .. original.Actors.Where(actor => actor.Id != BoardIds.Alice),
            ],
        };
        ScenarioDefinition changedDeadline = original with
        {
            CellarDeadlineMs = original.CellarDeadlineMs - 1,
        };
        var firstSeed = new ScenarioInstance(original, worldSeed: 1);
        var secondSeed = new ScenarioInstance(original, worldSeed: 2);

        Assert.Equal(firstSeed.DefinitionSha256, secondSeed.DefinitionSha256);
        Assert.NotEqual(firstSeed.InstanceSha256, secondSeed.InstanceSha256);
        Assert.NotEqual(firstSeed.DefinitionSha256, changedMaterial.ComputeSha256());
        Assert.NotEqual(firstSeed.DefinitionSha256, changedDeadline.ComputeSha256());
        Assert.Equal(64, firstSeed.DefinitionSha256.Length);
        Assert.Equal(64, firstSeed.InstanceSha256.Length);
    }

    [Fact]
    public async Task RunAsync_MutatedDeadline_CommitsSealingAtDefinitionTime()
    {
        ScenarioDefinition definition = ScenarioDefinition.Default with
        {
            CellarDeadlineMs = 123,
        };
        var instance = new ScenarioInstance(definition, worldSeed: 3);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new NullPlayerDriver(),
            [BoardIds.Bob] = new NullPlayerDriver(),
        };

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            drivers,
            instance,
            new ModelTime(200));

        var sealedEvent = Assert.Single(
            capture.Journal.Events,
            domainEvent => domainEvent.Payload is CellarSealedEvent);
        Assert.Equal(123, sealedEvent.Timestamp.ModelTime.Ticks);
        Assert.True(capture.Result.World.CellarSealed);
    }

    [Fact]
    public void CanonicalDefinition_Default_RecreatesLegacyInitialWorldShape()
    {
        var instance = ScenarioInstance.CreateDefault(worldSeed: 47);
        FirstBoardWorld world = instance.CreateInitialWorld();

        Assert.Equal(BoardIds.Tavern, world.Actor(BoardIds.Alice).PlaceId);
        Assert.Equal(BoardIds.Market, world.Actor(BoardIds.Bob).PlaceId);
        Assert.Equal(BoardIds.Market, world.Object(BoardIds.BrassKey).PlaceId);
        Assert.Null(world.Object(BoardIds.DuchessLetter).PlaceId);
        Assert.Equal(
            world.Actor(BoardIds.Alice).Id,
            world.Object(BoardIds.SilverCoinOne).OwnerActorId);
        Assert.Equal(
            ScenarioDefinition.Default.ToCanonicalJsonUtf8(),
            instance.Definition.ToCanonicalJsonUtf8());
    }
}
