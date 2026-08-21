using System.Security.Cryptography;
using System.Text.Json;

namespace DramaBoard.FirstBoard.Tests;

public sealed class ScenarioDefinitionTests
{
    [Fact]
    public void CanonicalDefinition_ReorderingDoesNotChangeBytesOrCurrentHash()
    {
        ScenarioDefinition original = ScenarioDefinition.Default;
        ScenarioDefinition reordered = original with
        {
            Places = original.Places.Reverse().ToArray(),
            Passages = original.Passages.Reverse().ToArray(),
            Actors = original.Actors.Reverse().ToArray(),
            Objects = original.Objects.Reverse().ToArray(),
        };

        byte[] canonical = original.ToCanonicalJsonUtf8();
        Assert.Equal(canonical, reordered.ToCanonicalJsonUtf8());
        Assert.Equal(original.ComputeSha256(), reordered.ComputeSha256());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant(),
            original.ComputeSha256());
        Assert.Equal(64, original.ComputeSha256().Length);

        using JsonDocument json = JsonDocument.Parse(canonical);
        Assert.Equal("dramaboard.scenario-definition/2", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(ScenarioDefinition.FirstBoardRuleset, json.RootElement.GetProperty("rulesetId").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt32());
    }

    [Fact]
    public void Validate_RejectsBrokenGraphAndDuplicateObjectLocationAuthority()
    {
        ScenarioDefinition original = ScenarioDefinition.Default;
        ScenarioPassageDefinition passage = original.Passages[0];
        ScenarioObjectDefinition coin = original.Objects.Single(item => item.Id == BoardIds.SilverCoinOne);

        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Passages =
            [
                passage with { EndpointAId = "missing-place" },
                .. original.Passages.Skip(1),
            ],
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Passages =
            [
                passage with { Length = 0 },
                .. original.Passages.Skip(1),
            ],
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Objects =
            [
                coin with { InitialPlaceId = BoardIds.Tavern },
                .. original.Objects.Where(item => item.Id != coin.Id),
            ],
        }).Validate());
    }

    [Fact]
    public void Validate_RejectsSpatialEntityCollisionsAndMalformedCellarGateContract()
    {
        ScenarioDefinition original = ScenarioDefinition.Default;
        ScenarioPassageDefinition cellarGate = original.Passages.Single(
            passage => passage.Id == BoardIds.CellarGatePassage);

        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Objects = [.. original.Objects, new(BoardIds.LockedChest, null, null)],
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Objects = [.. original.Objects, new(BoardIds.Alice, null, null)],
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Passages = original.Passages
                .Where(passage => passage.Id != BoardIds.CellarGatePassage)
                .ToArray(),
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (original with
        {
            Passages =
            [
                .. original.Passages.Where(passage => passage.Id != BoardIds.CellarGatePassage),
                cellarGate with
                {
                    EndpointAId = BoardIds.Cellar,
                    EndpointBId = BoardIds.CellarGate,
                },
            ],
        }).Validate());
    }

    [Fact]
    public void ScenarioInstance_FreezesMutableCollectionsAndSeparatesSeedIdentity()
    {
        var actors = ScenarioDefinition.Default.Actors.ToList();
        ScenarioDefinition mutable = ScenarioDefinition.Default with { Actors = actors };
        var first = new ScenarioInstance(mutable, worldSeed: 1);
        var secondSeed = new ScenarioInstance(mutable, worldSeed: 2);
        string snapshot = FirstBoardScenario.WorldSnapshot(first.CreateInitialWorld());

        ScenarioActorDefinition alice = actors.Single(actor => actor.Id == BoardIds.Alice);
        actors[0] = alice with { InitialPlaceId = BoardIds.Cellar };

        Assert.Equal(BoardIds.Tavern, first.Definition.Actor(BoardIds.Alice).InitialPlaceId);
        Assert.Equal(snapshot, FirstBoardScenario.WorldSnapshot(first.CreateInitialWorld()));
        Assert.Equal(first.DefinitionSha256, secondSeed.DefinitionSha256);
        Assert.NotEqual(first.InstanceSha256, secondSeed.InstanceSha256);
        Assert.Contains(first.DefinitionSha256[..12], first.Id, StringComparison.Ordinal);
    }
}
