using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground.Cli.Tests;

/// <summary>Actual durable-host evidence with offline clients; no live model calls.</summary>
public sealed class PlaygroundLookaheadHostEvidenceTests {
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private const string CandidateOnly = "unadopted-candidate-secret";
    private static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture")
    };

    [Fact]
    public async Task Discarded_candidate_response_reasoning_and_proposed_operation_never_enter_durable_history() {
        using var files = new Files();
        await files.CreateSource(soundEdge: true);
        var client = new Client(request => IsA(request) ? "shout invalidate-prediction" : "handoff " + CandidateOnly,
            candidateMarker: true);
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(2), timeout.Token, new StringWriter(), client));
        JsonElement candidate = Assert.Single(files.Events("candidate-created"));
        string id = candidate.GetProperty("Id").GetString()!;
        Assert.Contains(files.Events("candidate-discarded"), record => record.GetProperty("PhysicalId").GetString() == id);
        Assert.Contains(files.Events("physical-completed"), record => record.GetProperty("PhysicalId").GetString() == id
            && record.GetRawText().Contains(CandidateOnly, StringComparison.Ordinal));
        Assert.Empty(files.Events("candidate-adopted"));
        PlaygroundHostJournalSnapshot host = files.Host();
        Assert.DoesNotContain(host.Records, record => record.Payload.GetRawText().Contains(CandidateOnly, StringComparison.Ordinal));
        Assert.DoesNotContain(host.Records, record => record.Kind.StartsWith("lookahead-", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Records, record => record.ActorId == "b" && (record.Kind is "model-response" or "operation-received"));
        string history = CharacterHistoryExporter.Render(host, HostFixture.BranchId(files.Store), "b");
        Assert.DoesNotContain(CandidateOnly, history);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(saved.PendingEvent);
        Assert.Null(saved.State.Business.InteractionStates["b"].DraftHandoff);
        Assert.DoesNotContain(CandidateOnly, CharacterContextRenderer.RenderFull(
            saved.State.Business.InteractionStates["b"], new DecisionPlayLayout()));
    }

    [Fact]
    public async Task Adopted_response_links_actual_logical_request_to_one_physical_usage_record() {
        using var files = new Files();
        await files.CreateSource();
        var client = new Client(_ => "wait 1");
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(2), timeout.Token, new StringWriter(), client));
        string physicalId = Assert.Single(files.Events("candidate-adopted")).GetProperty("PhysicalId").GetString()!;
        JsonElement delivery = Assert.Single(files.Events("actual-delivery"), record => record.GetProperty("PhysicalId").GetString() == physicalId);
        string logicalId = delivery.GetProperty("LogicalRequestId").GetString()!;
        PlaygroundHostJournalSnapshot host = files.Host();
        PlaygroundHostRecord request = Assert.Single(host.Records, record => record.Kind == "model-request"
            && record.Payload.GetProperty("RequestId").GetString() == logicalId);
        Assert.Equal("b", request.ActorId);
        Assert.Single(host.Records, record => record.Kind == "model-response" && record.Payload.GetProperty("RequestId").GetString() == logicalId);
        Assert.Single(files.Events("physical-request"), record => record.GetProperty("PhysicalId").GetString() == physicalId);
        Assert.Single(files.Events("physical-completed"), record => record.GetProperty("PhysicalId").GetString() == physicalId);
        Assert.Equal(2, client.Calls);
        var physical = files.Events("physical-completed");
        Assert.Equal(2, physical.Count);
        Assert.Equal(14L, physical.Sum(record => record.GetProperty("Result").GetProperty("Usage").GetProperty("OutputTokens").GetInt64()));
        Assert.Equal(2, physical.Select(record => record.GetProperty("PhysicalId").GetString()).Distinct().Count());
        // Logical model-response usage is delivery evidence; the physical ledger is the accounting denominator.
        Assert.Equal(2, Assert.Single(files.Events("run-summary")).GetProperty("Stats").GetProperty("PhysicalCalls").GetInt32());
    }

    [Fact]
    public async Task Different_connection_is_not_resolved_speculatively_but_becomes_ready_after_actual_use() {
        using var files = new Files();
        await files.CreateSource(soundEdge: true);
        files.WriteConnections();
        var factory = new Factory();
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(2, await PlaygroundHost.RunAsync(files.Store, files.Options(12) with {
            ConnectionsPath = files.Connections, CharacterControlsPath = files.Controls
        }, timeout.Token, new StringWriter(), clientFactory: factory));
        Assert.Equal(new[] { "main", "other" }, factory.Created.ToArray());
        var raw = LlmRawAudit.Read(files.Raw).Records;
        LlmRawAuditRecord skip = raw.First(record => Event(record, "projection-skip"));
        Assert.Equal("binding-not-ready", Detail(skip).GetProperty("Reason").GetString());
        LlmRawAuditRecord firstB = raw.First(record => Event(record, "physical-request") && Detail(record).GetProperty("ActorId").GetString() == "b");
        Assert.True(skip.Sequence < firstB.Sequence);
        Assert.Equal("demand", Detail(firstB).GetProperty("Purpose").GetString());
        Assert.Equal("other", Detail(firstB).GetProperty("ConnectionId").GetString());
        Assert.Contains(raw, record => record.Sequence > firstB.Sequence && Event(record, "physical-request")
            && Detail(record).GetProperty("Purpose").GetString() == "candidate"
            && Detail(record).GetProperty("ConnectionId").GetString() == "other");
    }

    [Fact]
    public async Task Host_response_flush_failure_stops_admission_and_observes_already_started_candidate() {
        using var files = new Files();
        await files.CreateSource();
        var stream = new FailResponseStream();
        var client = new Client(_ => "wait 1");
        using var timeout = new CancellationTokenSource(Limit);
        Assert.Equal(1, await PlaygroundHost.RunAsync(files.Store, files.Options(8), timeout.Token, new StringWriter(), client,
            journalFactory: _ => PlaygroundHostJournal.CreateForTesting(stream)));
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, files.Events("physical-request").Count);
        Assert.Equal(2, files.Events("physical-completed").Count);
        Assert.Single(files.Events("candidate-drained"));
        Assert.DoesNotContain("operation-received", stream.Kinds);
        using var saved = PlaygroundDurableHistory.Open(files.Store);
        Assert.Null(saved.PendingEvent);
    }

    private static bool Event(LlmRawAuditRecord record, string kind) => record.Kind == "lookahead-event"
        && record.Payload.GetProperty("Kind").GetString() == "lookahead-" + kind;
    private static JsonElement Detail(LlmRawAuditRecord record) => record.Payload.GetProperty("Data").GetProperty("Detail");
    private static bool IsA(CompletionRequest request) => ((ObservationMessage)request.PromptPrefix.SharedContextMessages[0]).Content!
        .Contains("· \"a\" ·", StringComparison.Ordinal);
    private sealed class Client(Func<CompletionRequest, string> choose, bool candidateMarker = false, string api = "fixture") : ICompletionClient {
        private int _calls;
        public int Calls => _calls;
        public string Name => "host-evidence-offline";
        public string ApiSpecId => api;
        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            int call = Interlocked.Increment(ref _calls);
            var origin = new CompletionDescriptor("fixture", api, request.ModelId);
            var blocks = new List<ActionBlock>();
            if (candidateMarker && !IsA(request)) { blocks.Add(new ActionBlock.TextReasoningBlock(CandidateOnly, origin)); }
            blocks.Add(new ActionBlock.ToolCall(new("act", "host-evidence-" + call, JsonSerializer.Serialize(new { command = choose(request) }))));
            return Task.FromResult(new CompletionResult(new ActionMessage(blocks), origin, usage: new CompletionUsage(outputTokens: 7)));
        }
    }
    private sealed class Factory : ICompletionClientFactory {
        public ConcurrentQueue<string> Created { get; } = new();
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Created.Enqueue(connection.Id);
            int calls = 0;
            // Uniform wait 1 would keep B as the demand actor and A as the adopted actor forever.
            // B's first real shout invalidates the A candidate and wakes A; A's demand can then
            // prepare B using the already-resolved other connection.
            return new Client(_ => connection.Id == "other" && Interlocked.Increment(ref calls) == 1
                ? "shout ready-binding-change" : "wait 1",
                api: connection.Kind == "openai-chat" ? "openai-chat-v1" : "openai-responses-v2");
        }
    }
    private sealed class FailResponseStream : MemoryStream {
        private bool _armed;
        public List<string> Kinds { get; } = [];
        public override void Write(ReadOnlySpan<byte> buffer) {
            using JsonDocument line = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            using JsonDocument record = JsonDocument.Parse(line.RootElement.GetProperty("Payload").GetString()!);
            string kind = record.RootElement.GetProperty("Kind").GetString()!;
            Kinds.Add(kind);
            _armed = kind == "model-response";
            base.Write(buffer);
        }
        public override void Flush() { if (_armed) { throw new IOException("injected-host-response-flush"); } base.Flush(); }
    }
    private sealed class Files : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lookahead-host-evidence-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(Root, "store");
        public string Raw => Path.Combine(Root, "raw.jsonl");
        public string Connections => Path.Combine(Root, "connections.json");
        public string Controls => Path.Combine(Root, "controls.json");
        public PlaygroundHostOptions Options(int calls) => new(MaxCalls: calls, MaxSteps: 1000, Gym: true, LogPath: Raw, Lookahead: true);
        public PlaygroundHostJournalSnapshot Host() => PlaygroundHostJournal.Read(PlaygroundHostJournal.JournalPath(Store));
        public List<JsonElement> Events(string kind) => LlmRawAudit.Read(Raw).Records.Where(record => Event(record, kind)).Select(Detail).ToList();
        public async Task CreateSource(bool soundEdge = false) {
            var time = new ModelTime(10 * PlaygroundTime.Hour);
            var initial = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
            var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
            string[] places = ["camp", soundEdge ? "stream" : "cabin"];
            var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, actors.Select((actor, i) => new EntityPlacement(new(actor.Id), new(places[i]))));
            var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
                actors.Select((actor, i) => new PlaygroundVisit(actor.Id, places[i], time,
                    initial.World.Resources.Where(resource => PlaygroundExpeditionMap.GetResourcePlace(resource.Id) == places[i]))));
            var business = new PlaygroundBusinessState(time, initial.World.Light, initial.World.Resources, initial.World.Script, actors, expedition: expedition);
            var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), initial.Cursor);
            var kernel = PlaygroundEngine.CreateKernel(history);
            for (int i = 0; i < 10 && !(PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a"
                && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open); i++) {
                Assert.Equal(StepStatus.Committed, await kernel.StepAsync(time));
            }
            var warm = await PlaygroundLookaheadProjection.ProjectAsync(history.State, history.Cursor, "a", Bindings, default, preparationLimit: Limit);
            Assert.Equal(PlaygroundLookaheadProjection.StopReason.TargetReady, warm.StopReason);
            using var durable = PlaygroundDurableHistory.Create(Store, history.State, history.Cursor);
        }
        public void WriteConnections() {
            File.WriteAllText(Connections, JsonSerializer.Serialize(new { version = 1, defaultConnectionId = "main", catalog = new { v = 3, connections = new object[] {
                new { id = "main", kind = "openai-responses", completionSurfaceId = "openai-responses", modelId = "model-main", baseAddress = "https://fixture.invalid/" },
                new { id = "other", kind = "openai-chat", completionSurfaceId = "openai-chat/strict", modelId = "model-other", baseAddress = "https://fixture.invalid/" }
            } } }));
            File.WriteAllText(Controls, JsonSerializer.Serialize(new { version = 1, overrides = new { b = new { kind = "llm", connectionId = "other" } } }));
        }
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }
}
