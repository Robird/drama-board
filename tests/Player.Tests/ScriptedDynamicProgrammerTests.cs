using DramaBoard.Protocol;

namespace DramaBoard.Player.Tests;

public sealed class ScriptedDynamicProgrammerTests {
    [Fact]
    public async Task ScriptedDynamicProgrammer_ConsumesFactoriesInOrder() {
        ProgrammingRequest firstRequest = Request();
        ProgrammingRequest secondRequest = Request("activation-2");
        List<ProgrammingRequest> observedRequests = [];
        var programmer = new ScriptedDynamicProgrammer(
        [
            request => { observedRequests.Add(request); return Response(request); },
            request => { observedRequests.Add(request); return Response(request); },
        ]);

        ProgrammingResponse first = await programmer.ProgramAsync(firstRequest, CancellationToken.None);
        ProgrammingResponse second = await programmer.ProgramAsync(secondRequest, CancellationToken.None);

        Assert.Equal(firstRequest.ActivationId, first.ActivationId);
        Assert.Equal(secondRequest.ActivationId, second.ActivationId);
        Assert.Equal([firstRequest, secondRequest], observedRequests);
    }

    [Fact]
    public async Task ScriptedDynamicProgrammer_ThrowsWhenScriptIsExhausted() {
        var programmer = new ScriptedDynamicProgrammer([request => Response(request)]);

        await programmer.ProgramAsync(Request(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await programmer.ProgramAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ScriptedDynamicProgrammer_ThrowsWhenFactoryReturnsNull() {
        var programmer = new ScriptedDynamicProgrammer([_ => null!]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await programmer.ProgramAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullScriptThrowsArgumentNullException() {
        Assert.Throws<ArgumentNullException>(() =>
            new ScriptedDynamicProgrammer(null!));
    }

    [Fact]
    public void Constructor_NullFactoryThrowsArgumentException() {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new ScriptedDynamicProgrammer([request => Response(request), null!]));

        Assert.Contains("null factories", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramAsync_NullRequestThrowsArgumentNullException() {
        var programmer = new ScriptedDynamicProgrammer([request => Response(request)]);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await programmer.ProgramAsync(null!, CancellationToken.None));
    }

    private static ProgrammingRequest Request(
        string activationId = "activation-1") {
        return new ProgrammingRequest(
            new ActivationId(activationId),
            "actor.alice",
            10,
            ProgrammingReason.MissingInstruction,
            ReasonDetail: null,
            "place.square",
            [],
            new SubjectiveWorkspace(string.Empty),
            []);
    }

    private static ProgrammingResponse Response(ProgrammingRequest request) {
        return new ProgrammingResponse(
            request.ActivationId,
            [],
            new SubjectiveWorkspace(string.Empty));
    }
}
