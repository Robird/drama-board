using System.Text.Json;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterGym;
using DramaBoard.Goals;
using DramaBoard.Microtown.Cli.Gym;

namespace DramaBoard.Microtown.Cli.Tests;

public sealed class TownConsoleTests {
    [Fact]
    public async Task One_host_shares_call_budget_across_three_private_observations_and_borrows_client() {
        using var files = new Files();
        var initial = TownGameFactory.Create();
        TownBusinessState before = initial.World.Business;
        var actors = before.Characters.Select(actor => actor with {
            DecisionSequence = 1,
            OperationSequence = 1,
            Goals = GoalRules.Apply(new(), new(GoalOperationKind.Create,
                JsonSerializer.Serialize(new { title = "private-plan-" + actor.Id, criteria = "personal criterion" })), 1, 1).After
        });
        var seeded = new TownBusinessState(before.Time, before.EndTime, actors, before.Market, before.Economy, before.Spatial);
        using (TownDurableHistory.Create(files.Store, TownEngine.Rebuild(seeded), initial.Cursor)) { }
        var client = new Client(Act("a", "buy grain 1"), Act("b", "buy grain 1"), Act("c", "buy grain 1"));
        var output = new StringWriter();

        int code = await TownConsole.RunAsync(new() { Store = files.Store, Llm = true, MaxCalls = 3 },
            TextReader.Null, output, client);

        Assert.Equal(2, code);
        Assert.Equal(3, client.Requests.Count);
        Assert.Contains("call-budget-exhausted", output.ToString());
        for (int index = 0; index < 3; index++) {
            string actor = new[] { "a", "b", "c" }[index];
            string material = Material(client.Requests[index]);
            Assert.Contains("private-plan-" + actor, material);
            foreach (string other in new[] { "a", "b", "c" }.Where(other => other != actor)) {
                Assert.DoesNotContain("private-plan-" + other, material);
            }
            Assert.Empty(client.Requests[index].TailMessages);
        }
        using var reopened = TownDurableHistory.Open(files.Store);
        Assert.Equal(0, reopened.State.Time.Ticks);
        Assert.Equal("a", reopened.State.Business.OpenDecisionActorId);
        Assert.All(reopened.State.Business.Characters, actor => {
            Assert.Equal(2, actor.OperationSequence);
            Assert.Equal(1, actor.Inventory.Count("grain"));
            Assert.Null(actor.Activity);
            Assert.Equal(actor.Id, actor.LastOperationReceipt!.Operation.ToolCallId);
        });
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task Human_first_EOF_does_not_resolve_missing_auth_or_request_a_model() {
        using var files = new Files();
        var options = new TownHostOptions { Store = files.Store, Llm = true, HumanActor = "a",
            Connection = new() { AuthFile = Path.Combine(files.Root, "does-not-exist.json") } };
        var output = new StringWriter();
        Assert.Equal(0, await TownConsole.RunAsync(options, TextReader.Null, output));
        Assert.DoesNotContain("LLM binding", output.ToString());
        Assert.Contains("End of input", output.ToString());
        using var reopened = TownDurableHistory.Open(files.Store);
        Assert.Equal("a", reopened.State.Business.OpenDecisionActorId);
        Assert.Equal(0, reopened.State.Time.Ticks);
        Assert.All(reopened.State.Business.Characters, actor => Assert.Equal(0, actor.OperationSequence));
    }

    [Fact]
    public async Task Ended_cold_store_needs_no_auth_even_when_reopened_in_LLM_mode() {
        using var files = new Files();
        Assert.Equal(0, await TownConsole.RunAsync(new() { Store = files.Store, Hours = 1 },
            new StringReader("wait 1\nwait 1\nwait 1\n"), new StringWriter()));
        string completed;
        using (var ended = TownDurableHistory.Open(files.Store)) {
            Assert.True(ended.State.Business.Ended);
            completed = TownDurableHistory.FormatCommitId(ended.CompletedStateCommitId);
        }
        var output = new StringWriter();
        Assert.Equal(0, await TownConsole.RunAsync(new() { Store = files.Store, Llm = true,
            Connection = new() { AuthFile = Path.Combine(files.Root, "does-not-exist.json") } }, TextReader.Null, output));
        Assert.DoesNotContain("LLM binding", output.ToString());
        using var reopened = TownDurableHistory.Open(files.Store);
        Assert.Equal(completed, TownDurableHistory.FormatCommitId(reopened.CompletedStateCommitId));
    }

    [Fact]
    public async Task Host_feedback_roundtrip_is_durable_before_followup_and_records_actual_game_receipt() {
        using var files = new Files();
        var client = new Client(Result(new("report-feedback", "report-a", JsonSerializer.Serialize(new {
            kind = "feature-request", summary = "Price history", obstacle = "Only current prices are visible"
        }))), Act("action-after-feedback", "buy grain 2"));
        client.BeforeResponse = (index, request) => {
            if (index != 2) { return; }
            GymRecord feedback = Assert.Single(files.Read().Records, record => record.Kind == "feedback");
            string id = feedback.Payload.GetProperty("FeedbackId").GetString()!;
            ToolResult receipt = Assert.Single(Assert.IsType<ToolResultsMessage>(request.TailMessages[1]).Results);
            Assert.Equal("report-a", receipt.ToolCallId);
            Assert.Contains(id, receipt.GetFlattenedText());
        };
        var output = new StringWriter();
        int code = await TownConsole.RunAsync(new() { Store = files.Store, Llm = true, Feedback = true,
            MaxCalls = 2, MaxOperations = 1 }, TextReader.Null, output, client);
        Assert.Equal(0, code);
        Assert.Equal(2, client.Requests.Count);
        GymJournalSnapshot log = files.Read();
        GymRecord[] requests = log.Records.Where(record => record.Kind == "request").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request => Assert.Equal("a", request.ActorId));
        Assert.Equal(requests[0].Payload.GetProperty("Boundary").GetRawText(), requests[1].Payload.GetProperty("Boundary").GetRawText());
        GymRecord result = Assert.Single(log.Records, record => record.Kind == "operation");
        Assert.Equal("Succeeded", result.Payload.GetProperty("Result").GetProperty("Status").GetString());
        using var reopened = TownDurableHistory.Open(files.Store);
        TownOperationReceipt actual = reopened.State.Business.Character("a").LastOperationReceipt!;
        Assert.Equal("action-after-feedback", actual.Operation.ToolCallId);
        Assert.Equal(actual.Operation.OperationRecordId, result.Payload.GetProperty("OperationRecordId").GetString());
        Assert.Equal(2, reopened.State.Business.Character("a").Inventory.Count("grain"));
        Assert.Equal(0, reopened.State.Business.Character("b").OperationSequence);
        Assert.Equal("b", reopened.State.Business.OpenDecisionActorId);
        Assert.Equal(0, reopened.State.Time.Ticks);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task Cancellation_during_provider_call_retains_open_decision_without_substitute_action() {
        using var files = new Files();
        using var cancellation = new CancellationTokenSource();
        var client = new Client { Invoke = (_, token) => {
            cancellation.Cancel();
            return Task.FromCanceled<CompletionResult>(token);
        } };
        var output = new StringWriter();
        Assert.Equal(130, await TownConsole.RunAsync(new() { Store = files.Store, Llm = true, Gym = true },
            TextReader.Null, output, client, cancellation.Token));
        Assert.Single(client.Requests);
        Assert.False(client.Disposed);
        Assert.DoesNotContain(files.Read().Records, record => record.Kind is "operation" or "operation-received");
        using var reopened = TownDurableHistory.Open(files.Store);
        Assert.Null(reopened.PendingEvent);
        Assert.Equal("a", reopened.State.Business.OpenDecisionActorId);
        Assert.Equal(0, reopened.State.Time.Ticks);
        Assert.All(reopened.State.Business.Characters, actor => {
            Assert.Equal(0, actor.OperationSequence);
            Assert.Null(actor.Activity);
        });
    }

    [Fact]
    public async Task Protocol_pause_does_not_turn_invalid_output_into_a_game_action() {
        using var files = new Files();
        var client = new Client(Result(new("invented-tool", "invalid", "{}")));
        var output = new StringWriter();
        Assert.Equal(2, await TownConsole.RunAsync(new() { Store = files.Store, Llm = true, MaxFailures = 1 },
            TextReader.Null, output, client));
        Assert.Single(client.Requests);
        Assert.Contains("No substitute action", output.ToString());
        using var reopened = TownDurableHistory.Open(files.Store);
        Assert.Equal("a", reopened.State.Business.OpenDecisionActorId);
        Assert.Equal(0, reopened.State.Time.Ticks);
        Assert.All(reopened.State.Business.Characters, actor => Assert.Equal(0, actor.OperationSequence));
        Assert.False(client.Disposed);
    }

    private static string Material(CompletionRequest request) =>
        Assert.IsType<ObservationMessage>(Assert.Single(request.PromptPrefix.SharedContextMessages)).Content!;
    private static CompletionResult Act(string id, string command) => Result(new("act", id, JsonSerializer.Serialize(new { command })));
    private static CompletionResult Result(RawToolCall call) =>
        new(new ActionMessage([new ActionBlock.ToolCall(call)]), new("fixture", "fixture", "fixture-model"));

    private sealed class Client(params CompletionResult[] responses) : ICompletionClient, IDisposable, IAsyncDisposable {
        private readonly Queue<CompletionResult> _responses = new(responses);
        public string Name => "fixture";
        public string ApiSpecId => "fixture";
        public bool Disposed { get; private set; }
        public List<CompletionRequest> Requests { get; } = [];
        public Action<int, CompletionRequest>? BeforeResponse { get; set; }
        public Func<CompletionRequest, CancellationToken, Task<CompletionResult>>? Invoke { get; init; }
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default) {
            Requests.Add(request);
            BeforeResponse?.Invoke(Requests.Count, request);
            return Invoke?.Invoke(request, cancellationToken) ?? Task.FromResult(_responses.Dequeue());
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "town-console-tests-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "game");
        public GymJournalSnapshot Read() => GymJournal.Read(TownGymSession.JournalPath(Store));
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
