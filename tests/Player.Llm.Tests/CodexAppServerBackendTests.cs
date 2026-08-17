using System.Text.Json;

namespace DramaBoard.Player.Llm.Tests;

public sealed class CodexAppServerBackendTests
{
    [Fact]
    public void ProcessTransport_JsonLineEncoding_DoesNotPrefixBom()
    {
        Assert.Empty(ProcessCodexAppServerConnection.JsonLineEncoding.GetPreamble());
    }

    [Fact]
    public async Task CompleteAsync_TwoRecordedTurns_ReusesProcessAndCreatesEphemeralThreads()
    {
        var connection = new FakeCodexConnection(
        [
            "{\"id\":1,\"result\":{\"userAgent\":\"codex-test\"}}",
            "{\"method\":\"thread/started\",\"params\":{\"thread\":{\"id\":\"thr_1\"}}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr_1\",\"ephemeral\":true}}}",
            "{\"method\":\"item/commandExecution/requestApproval\",\"id\":\"approval_1\",\"params\":{\"threadId\":\"thr_1\",\"turnId\":\"turn_1\"}}",
            AgentMessage("thr_1", "turn_1", "first answer"),
            TurnCompleted("thr_1", "turn_1", "first answer"),
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn_1\",\"status\":\"inProgress\",\"items\":[]}}}",
            "{\"id\":4,\"result\":{\"status\":\"unsubscribed\"}}",
            "{\"id\":5,\"result\":{\"thread\":{\"id\":\"thr_2\",\"ephemeral\":true}}}",
            "{\"id\":6,\"result\":{\"turn\":{\"id\":\"turn_2\",\"status\":\"inProgress\",\"items\":[]}}}",
            AgentMessage("thr_2", "turn_2", "second answer"),
            TurnCompleted("thr_2", "turn_2", "second answer"),
            "{\"id\":7,\"result\":{\"status\":\"unsubscribed\"}}",
        ]);
        int connectionCount = 0;
        await using var backend = CreateBackend(() =>
        {
            connectionCount++;
            return connection;
        });

        string first = await backend.CompleteAsync(
            new LlmChatRequest("system one", "user one"),
            CancellationToken.None);
        string second = await backend.CompleteAsync(
            new LlmChatRequest("system two", "user two"),
            CancellationToken.None);

        Assert.Equal("first answer", first);
        Assert.Equal("second answer", second);
        Assert.Equal(1, connectionCount);

        JsonElement[] messages = connection.Writes.Select(Parse).ToArray();
        Assert.Single(messages, message => Method(message) == "initialize");
        Assert.Single(messages, message => Method(message) == "initialized");
        JsonElement[] threadStarts = messages
            .Where(message => Method(message) == "thread/start")
            .ToArray();
        Assert.Equal(2, threadStarts.Length);
        Assert.All(threadStarts, message =>
        {
            JsonElement parameters = message.GetProperty("params");
            Assert.True(parameters.GetProperty("ephemeral").GetBoolean());
            Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());
            Assert.Equal("gpt-test", parameters.GetProperty("model").GetString());
        });

        JsonElement[] turnStarts = messages
            .Where(message => Method(message) == "turn/start")
            .ToArray();
        Assert.Equal(2, turnStarts.Length);
        JsonElement firstTurnParameters = turnStarts[0].GetProperty("params");
        Assert.Equal("never", firstTurnParameters.GetProperty("approvalPolicy").GetString());
        JsonElement sandbox = firstTurnParameters.GetProperty("sandboxPolicy");
        Assert.Equal("readOnly", sandbox.GetProperty("type").GetString());
        Assert.False(sandbox.GetProperty("networkAccess").GetBoolean());
        string prompt = firstTurnParameters.GetProperty("input")[0].GetProperty("text").GetString()!;
        Assert.Contains("system one", prompt);
        Assert.Contains("user one", prompt);
        Assert.Contains("Do not inspect files, run commands, or call tools", prompt);

        JsonElement approvalResponse = Assert.Single(
            messages,
            message =>
                message.TryGetProperty("id", out JsonElement id) &&
                id.ValueKind == JsonValueKind.String &&
                id.GetString() == "approval_1");
        Assert.Equal(
            "decline",
            approvalResponse.GetProperty("result").GetProperty("decision").GetString());
        Assert.Equal(2, messages.Count(message => Method(message) == "thread/unsubscribe"));
    }

    [Fact]
    public async Task CompleteAsync_EofPoisonsConnection_NextCallStartsFreshProcess()
    {
        var broken = new FakeCodexConnection(
        [
            "{\"id\":1,\"result\":{}}",
        ]);
        var recovered = new FakeCodexConnection(
        [
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr_recovered\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn_recovered\",\"status\":\"inProgress\",\"items\":[]}}}",
            AgentMessage("thr_recovered", "turn_recovered", "recovered answer"),
            TurnCompleted("thr_recovered", "turn_recovered", "recovered answer"),
            "{\"id\":4,\"result\":{\"status\":\"unsubscribed\"}}",
        ]);
        var connections = new Queue<ICodexAppServerConnection>([broken, recovered]);
        await using var backend = CreateBackend(() => connections.Dequeue());

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => backend.CompleteAsync(
                new LlmChatRequest("system", "first"),
                CancellationToken.None));
        string result = await backend.CompleteAsync(
            new LlmChatRequest("system", "second"),
            CancellationToken.None);

        Assert.Equal("recovered answer", result);
        Assert.True(broken.IsDisposed);
        Assert.Empty(connections);
    }

    private static CodexAppServerBackend CreateBackend(
        Func<ICodexAppServerConnection> connectionFactory) =>
        new(
            new CodexAppServerOptions(
                Model: "gpt-test",
                WorkingDirectory: Path.GetTempPath(),
                ReasoningEffort: "low",
                RequestTimeout: TimeSpan.FromSeconds(10)),
            connectionFactory);

    private static string AgentMessage(string threadId, string turnId, string text) =>
        JsonSerializer.Serialize(new
        {
            method = "item/completed",
            @params = new
            {
                threadId,
                turnId,
                item = new
                {
                    type = "agentMessage",
                    id = "msg_1",
                    text,
                    phase = "final_answer",
                },
            },
        });

    private static string TurnCompleted(string threadId, string turnId, string text) =>
        JsonSerializer.Serialize(new
        {
            method = "turn/completed",
            @params = new
            {
                threadId,
                turn = new
                {
                    id = turnId,
                    status = "completed",
                    items = new[]
                    {
                        new
                        {
                            type = "agentMessage",
                            id = "msg_1",
                            text,
                            phase = "final_answer",
                        },
                    },
                    error = (object?)null,
                },
            },
        });

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? Method(JsonElement message) =>
        message.TryGetProperty("method", out JsonElement method) &&
        method.ValueKind == JsonValueKind.String
            ? method.GetString()
            : null;

    private sealed class FakeCodexConnection : ICodexAppServerConnection
    {
        private readonly Queue<string> _reads;

        public FakeCodexConnection(IEnumerable<string> reads)
        {
            _reads = new Queue<string>(reads);
        }

        public bool IsAlive => !IsDisposed;

        public bool IsDisposed { get; private set; }

        public List<string> Writes { get; } = [];

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(line);
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_reads.TryDequeue(out string? line) ? line : null);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
