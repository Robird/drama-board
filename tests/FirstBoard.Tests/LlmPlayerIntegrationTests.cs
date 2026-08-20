using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player.Llm;
using DramaBoard.Player.Llm.Tests;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Tests;

public sealed class LlmPlayerIntegrationTests
{
    [Fact]
    public async Task ScriptedLlmPlayers_ChangeWorldUpdateMemoryAndRecoverFromRejectedAction()
    {
        var aliceBackend = new FakeLlmBackend(
        [
            Response("action.travel", "我已前往市场。", "\"destination\":\"market\""),
            Response("action.travel", "那条路也许不存在。", "\"destination\":\"missing-place\""),
            Response(
                "action.talk",
                "非法路线被拒绝，我改为与鲍勃交谈。",
                "\"targetActor\":\"bob\"",
                dialogue: "鲍勃，钥匙先由你保管。"),
            Response("action.wait", "我会等待时机。", "\"durationMs\":5000000"),
        ]);
        var bobBackend = new FakeLlmBackend(
        [
            Response("action.wait", "我等爱丽丝到市场。", "\"durationMs\":300000"),
            Response("action.take", "我拿到了黄铜钥匙。", "\"targetObject\":\"brass-key\""),
            Response("action.wait", "我带着钥匙等待。", "\"durationMs\":5000000"),
        ]);
        var aliceMemoryBackend = new FakeLlmBackend(
        [
            Memory("我已前往市场。"),
            Memory("那条路也许不存在。"),
            Memory("非法路线被拒绝，我改为与鲍勃交谈。"),
            Memory("我会等待时机。"),
        ]);
        var bobMemoryBackend = new FakeLlmBackend(
        [
            Memory("我等爱丽丝到市场。"),
            Memory("我拿到了黄铜钥匙。"),
            Memory("我带着钥匙等待。"),
        ]);
        var alice = new LlmPlayerDriver(
            new CharacterCard("爱丽丝", "谨慎而执着", "找到密信", "克制"),
            InitialMemory("尚未找到钥匙。"),
            aliceBackend,
            [new LlmMemoryShardMaintainer("working", aliceMemoryBackend)]);
        var bob = new LlmPlayerDriver(
            new CharacterCard("鲍勃", "务实", "保护同伴", "直率"),
            InitialMemory("我在市场。"),
            bobBackend,
            [new LlmMemoryShardMaintainer("working", bobMemoryBackend)]);

        BoardRunCapture capture = await FirstBoardScenario.RunAsync(
            new Dictionary<string, DramaBoard.Player.IPlayerDriver>(StringComparer.Ordinal)
            {
                [BoardIds.Alice] = alice,
                [BoardIds.Bob] = bob,
            },
            worldSeed: 101,
            new ModelTime(BoardTiming.RandomRunBoundaryTicks));

        Assert.Equal(StopReason.BoundaryReached, capture.Result.StopReason);
        Assert.Equal(0, capture.Result.ForcedDecisionCount);
        Assert.Equal(
            capture.Result.World.Actor(BoardIds.Bob).Id,
            capture.Result.World.Object(BoardIds.BrassKey).OwnerActorId);
        ActionRejectedEvent rejection = Assert.IsType<ActionRejectedEvent>(Assert.Single(
            capture.Journal.Events,
            domainEvent => domainEvent.Kind == BoardEventKinds.ActionRejected).Payload);
        Assert.Equal(BoardIds.Alice, rejection.ActorId);
        Assert.Equal("missing-place", rejection.RejectedIntent.DestinationId);
        Assert.Single(capture.Journal.Events, domainEvent =>
            domainEvent.Kind == BoardEventKinds.ActorSpoke &&
            Assert.IsType<ActorSpokeEvent>(domainEvent.Payload).ActorId == BoardIds.Alice);

        Assert.Equal("我会等待时机。", alice.CurrentMemoryBank["working"].Content);
        Assert.Equal("我带着钥匙等待。", bob.CurrentMemoryBank["working"].Content);
        Assert.Equal(4, aliceBackend.Requests.Count);
        Assert.Equal(3, bobBackend.Requests.Count);
        string rejectionPrompt = aliceBackend.Requests[2].User;
        Assert.Contains("上次尝试被拒绝", rejectionPrompt);
        Assert.Contains("destination=missing-place", rejectionPrompt);
        Assert.Contains("destination does not exist", rejectionPrompt);
        Assert.Contains("action.talk", rejectionPrompt);
    }

    private static MemoryBank InitialMemory(string content) =>
        new(
        [
            new MemoryShard("working", "当前处境", "维护当前处境。", content),
        ]);

    private static string Memory(string content) =>
        $"{{\"operation\":\"replace\",\"content\":\"{content}\"}}";

    private static string Response(
        string action,
        string memory,
        string? extraJson = null,
        string? dialogue = null) =>
        $$"""
        【独白】测试中的内心独白。
        【行动】{"action":"{{action}}"{{(extraJson is null ? string.Empty : $",{extraJson}")}}}
        【台词】{{dialogue}}
        【记忆】{{memory}}
        """;
}
