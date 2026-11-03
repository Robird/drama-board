using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Atelia.Completion.Abstractions;
using DramaBoard.CharacterGym;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;
using DramaBoard.Playground.Cli;
using DramaBoard.Spatial;

namespace DramaBoard.PlaygroundLookahead;

internal static class Program {
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 128, Converters = { new JsonStringEnumConverter(), new VisibleConverter() } };
    internal static readonly IReadOnlyDictionary<string, CharacterControlBinding> Bindings = new Dictionary<string, CharacterControlBinding> {
        ["a"] = new CharacterControlBinding.Llm("fixture"), ["b"] = new CharacterControlBinding.Llm("fixture") };
    public static async Task<int> Main(string[] args) {
        if (args.Contains("--online-self-check")) { return await OnlineSelfCheck.Run(args); }
        if (args.Contains("--online")) { return await OnlineRunner.Run(args); }
        var values = new Dictionary<string, string>();
        bool check = args.Contains("--self-check");
        for (int i = 0; i < args.Length; i++) {
            if (args[i] == "--self-check") { continue; }
            if (i + 1 >= args.Length || !new[] { "--output", "--pairs", "--operations", "--delay-ms" }.Contains(args[i]) || !values.TryAdd(args[i], args[++i])) {
                throw new ArgumentException("Expected --self-check and/or unique --output PATH --pairs N --operations N --delay-ms N.");
            }
        }
        string output = Path.GetFullPath(values["--output"]);
        if (Directory.Exists(output)) { throw new IOException("Output must be a fresh directory."); }
        Directory.CreateDirectory(output);
        int pairs = int.Parse(values.GetValueOrDefault("--pairs", check ? "1" : "5"));
        int operations = int.Parse(values.GetValueOrDefault("--operations", check ? "4" : "16"));
        int delay = int.Parse(values.GetValueOrDefault("--delay-ms", check ? "5" : "100"));
        if (pairs < 1 || operations < 1 || delay < 0 || !check && (pairs < 5 || operations < 16)) { throw new ArgumentOutOfRangeException(nameof(args)); }
        Save(Path.Combine(output, "manifest.json"), new { Format = 1, SelfCheck = check, pairs, operations, delay,
            StartedUtc = DateTimeOffset.UtcNow, Source = Provenance(), Runtime = Environment.Version.ToString(),
            Stop = "N committed Succeeded actor operations; checked by host between complete kernel steps",
            Timing = "startup and work wall separate; nested/concurrent metrics must not be added",
            Workloads = new[] { "low: camp/cabin, wait1 else legal continue", "high: camp/stream, first operation each actor/context shout, then wait1 else legal continue" },
            ResponseRule = "Pure complete request -> command; no global physical call sequence; tool id is stable constant",
            Identity = "Full raw values retained. Effective model inputs compare unchanged. Committed OperationRecordId properties map bijectively after actor/context/operation sequence validation; arbitrary strings and all other fields remain unchanged." });
        var samples = new List<Run>();
        try {
            foreach (bool high in new[] { false, true }) {
                var initial = await Source(high);
                foreach (bool shared in new[] { false, true }) {
                    // Warm the actual host on both paths in this same process; never include warmup in samples.
                    foreach (bool on in new[] { false, true }) {
                        await Execute(Path.Combine(output, $"warm-{high}-{shared}-{on}"), initial, high, shared, on, 4, delay);
                    }
                    for (int pair = 0; pair < pairs; pair++) {
                        var group = new List<Run>();
                        foreach (bool on in pair % 2 == 0 ? new[] { false, true } : new[] { true, false }) {
                            Run run = await Execute(Path.Combine(output, $"{(high ? "high" : "low")}-{(shared ? "shared" : "independent")}-{pair}-{(on ? "on" : "off")}"), initial, high, shared, on, operations, delay);
                            samples.Add(run); group.Add(run);
                        }
                        Compare(group.Single(r => !r.On), group.Single(r => r.On), output);
                    }
                }
            }
            Save(Path.Combine(output, "samples.json"), samples);
            Save(Path.Combine(output, "summary.json"), new { Passed = true, Samples = samples.Count,
                Groups = samples.GroupBy(r => new { r.High, r.Shared }).Select(g => new { g.Key,
                    Pairs = g.Count() / 2, OffWallMilliseconds = g.Where(r => !r.On).Select(r => r.WorkMilliseconds).ToArray(),
                    OnWallMilliseconds = g.Where(r => r.On).Select(r => r.WorkMilliseconds).ToArray(),
                    OffPhysical = g.Where(r => !r.On).Select(r => r.PhysicalCalls).ToArray(), OnPhysical = g.Where(r => r.On).Select(r => r.PhysicalCalls).ToArray() }),
                Timing = "Effective work starts at first actual Choose entry and ends at final commit; host wall separately includes initialization and drain. Startup is separately recorded. Nested counters are not additive." });
            return 0;
        } catch (Exception error) {
            Save(Path.Combine(output, "failure.json"), new { Type = error.GetType().FullName, error.Message, Samples = samples });
            throw;
        }
    }
    private static async Task<Run> Execute(string directory, Initial initial, bool high, bool shared, bool on, int count, int delay) {
        var startup = Stopwatch.StartNew();
        Directory.CreateDirectory(directory);
        using var timeline = new Timeline(Path.Combine(directory, "timeline.jsonl"));
        var history = new RecordingHistory(new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor), timeline);
        var client = new Fixture(high, shared, delay, timeline);
        Save(Path.Combine(directory, "initial.json"), new { initial.World.Business, initial.World.Forecasts, initial.World.Spatial, initial.Cursor });
        long firstServiceStart = 0;
        double productionRawMilliseconds = 0, hostJournalMilliseconds = 0;
        var services = new List<object>();
        var options = new PlaygroundHostOptions(MaxCalls: count * 8 + 32, MaxSteps: count * 40 + 100,
            MaxContextCharacters: 2_000_000, LogPath: Path.Combine(directory, "raw.jsonl"), Lookahead: on,
            StopRequested: () => history.Valid >= count,
            ServiceCompleted: (context, elapsed) => {
                long started = Stopwatch.GetTimestamp() - (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
                if (firstServiceStart == 0) { firstServiceStart = started; }
                var sample = new { Actor = context.InteractionState.ActorId, context.Context.ContextSequence, context.Context.LastOperationSequence, ServiceMilliseconds = elapsed.TotalMilliseconds };
                services.Add(sample); timeline.Write("actual-service-completed", sample);
            }, RawAuditCompleted: elapsed => productionRawMilliseconds = elapsed.TotalMilliseconds,
            HostJournalCompleted: elapsed => hostJournalMilliseconds = elapsed.TotalMilliseconds);
        startup.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        long hostStart = Stopwatch.GetTimestamp();
        var wall = Stopwatch.StartNew();
        int exit = await PlaygroundHost.RunAsync(history, Bindings, options, timeout.Token, TextWriter.Null, client);
        wall.Stop();
        long hostEnd = Stopwatch.GetTimestamp();
        var final = JsonSerializer.SerializeToElement(new { history.State.Business, history.State.Forecasts, history.State.Spatial, history.Cursor }, Json);
        Save(Path.Combine(directory, "services.json"), services);
        Save(Path.Combine(directory, "timing.json"), new { StartupPreparationMilliseconds = startup.Elapsed.TotalMilliseconds,
            HostBeforeFirstServiceMilliseconds = (firstServiceStart - hostStart) * 1000d / Stopwatch.Frequency,
            HostAfterFinalCommitMilliseconds = (hostEnd - history.LastCommitTimestamp) * 1000d / Stopwatch.Frequency,
            ProductionRawWriteMilliseconds = productionRawMilliseconds, HostJournalAppendMilliseconds = hostJournalMilliseconds,
            InMemoryEventCommitMilliseconds = history.EventCommitMilliseconds, InMemoryStateCommitMilliseconds = history.StateCommitMilliseconds,
            HostWallMilliseconds = wall.Elapsed.TotalMilliseconds,
            EffectiveWorkMilliseconds = (history.LastCommitTimestamp - firstServiceStart) * 1000d / Stopwatch.Frequency,
            Definition = "First actual Choose entry to final effective commit; host initialization and cleanup excluded. Service sample includes rendering and tool turns." });
        Save(Path.Combine(directory, "final.json"), final);
        Save(Path.Combine(directory, "events.json"), history.Events);
        Save(Path.Combine(directory, "operations.json"), history.Operations);
        var raw = LlmRawAudit.Read(options.LogPath!);
        var requests = raw.Records.Where(r => r.Kind == "model-request").Select(r => {
            var p = r.Payload;
            return JsonSerializer.SerializeToElement(new { CharacterId = p.GetProperty("CharacterId"), ContextSequence = p.GetProperty("ContextSequence"),
                ConnectionId = p.GetProperty("ConnectionId"), ApiSpecId = p.GetProperty("ApiSpecId"), ModelId = p.GetProperty("ModelId"), SystemPrompt = p.GetProperty("SystemPrompt"), OutputContract = p.GetProperty("OutputContract"),
                Shared = p.GetProperty("Shared"), Tail = p.GetProperty("Tail") }, Json);
        }).ToList();
        Save(Path.Combine(directory, "actual-inputs.json"), requests);
        if (!on) {
            var actualRequests = raw.Records.Where(r => r.Kind == "model-request").ToArray();
            var physicalInputs = client.PhysicalInputs.OrderBy(p => p.Id).ToArray();
            if (actualRequests.Length != physicalInputs.Length) { throw new InvalidDataException("Serial logical/physical count mismatch."); }
            var correlations = actualRequests.Select((record, i) => {
                var p = record.Payload;
                string effective = JsonSerializer.Serialize(new { ModelId = p.GetProperty("ModelId"), SystemPrompt = p.GetProperty("SystemPrompt"),
                    Contract = p.GetProperty("OutputContract"), Shared = p.GetProperty("Shared"), Tail = p.GetProperty("Tail") }, Json);
                if (effective != physicalInputs[i].Input) { throw new InvalidDataException("Serial full input physical correlation mismatch."); }
                return new { LogicalRequestId = p.GetProperty("RequestId"), PhysicalId = physicalInputs[i].Id, InputHash = Hash(effective) };
            }).ToArray();
            Save(Path.Combine(directory, "physical-logical-map.json"), correlations);
        }
        var lookaheadEvents = raw.Records.Where(r => r.Kind == "lookahead-event").Select(r => r.Payload).ToArray();
        int PhysicalOrdinal(JsonElement detail) => int.Parse(detail.GetProperty("PhysicalId").GetString()!.Split("-physical-", StringSplitOptions.None).Last());
        var requestDetails = lookaheadEvents.Where(e => e.GetProperty("Kind").GetString() == "lookahead-physical-request")
            .Select(e => e.GetProperty("Data").GetProperty("Detail")).ToDictionary(PhysicalOrdinal);
        var adoptedIds = lookaheadEvents.Where(e => e.GetProperty("Kind").GetString() == "lookahead-candidate-adopted")
            .Select(e => PhysicalOrdinal(e.GetProperty("Data").GetProperty("Detail"))).ToHashSet();
        var discardedIds = lookaheadEvents.Where(e => e.GetProperty("Kind").GetString() == "lookahead-candidate-discarded")
            .Select(e => PhysicalOrdinal(e.GetProperty("Data").GetProperty("Detail"))).ToHashSet();
        var physicalAccounting = client.Services.OrderBy(s => s.PhysicalId).Select(service => {
            string purpose = on ? requestDetails[service.PhysicalId].GetProperty("Purpose").GetString()! : "demand";
            if (on) {
                string projected = requestDetails[service.PhysicalId].GetProperty("FullInput").GetString()!;
                string original = client.PhysicalInputs.Single(i => i.Id == service.PhysicalId).Input;
                if (!JsonNode.DeepEquals(JsonNode.Parse(projected), JsonNode.Parse(original))) { throw new InvalidDataException("On physical ordinal/full input correlation mismatch."); }
            }
            string disposition = purpose != "candidate" ? "demand" : adoptedIds.Contains(service.PhysicalId) ? "adopted" : discardedIds.Contains(service.PhysicalId) ? "discarded" : "unadopted-at-stop";
            return new { service, Purpose = purpose, Disposition = disposition, RemoteOutcomeUnknown = false };
        }).ToArray();
        if (physicalAccounting.Length != client.Calls || physicalAccounting.Any(p => !p.service.Completed && p.service.Failure is null)) { throw new InvalidDataException("Unobserved fixture physical outcome."); }
        Save(Path.Combine(directory, "physical-service.json"), new { Calls = physicalAccounting,
            StartedServices = physicalAccounting.Count(p => p.service.ServiceStarted), CompletedServices = physicalAccounting.Count(p => p.service.Completed),
            DiscardedServiceMilliseconds = physicalAccounting.Where(p => p.Disposition is "discarded" or "unadopted-at-stop").Sum(p => p.service.ServiceMilliseconds),
            UnobservedOrRemoteUnknown = 0, Undrained = client.InFlight,
            Scope = "Deterministic local fixture: queued requests may cancel; shared provider ignores cancellation once service starts. Service milliseconds include response creation and physical-completed timeline write. No remote outcome ambiguity exists." });
        int EventCount(string kind) => lookaheadEvents.Count(p => p.GetProperty("Kind").GetString() == "lookahead-" + kind);
        long[] modelTicks = history.Operations.Select(o => o.At.Ticks).Distinct().Order().ToArray();
        var coverage = new { DistinctOperationTimes = modelTicks.Length, ModelTicks = modelTicks, ModelSpanTicks = modelTicks.LastOrDefault() - modelTicks.FirstOrDefault(),
            Created = EventCount("candidate-created"), Adopted = EventCount("candidate-adopted"), Discarded = EventCount("candidate-discarded"),
            ActorContexts = history.Operations.SelectMany(o => o.InteractionChanges.Where(c => c.ActorId == o.ActorId).Select(c => new { c.ActorId, c.CurrentContext!.ContextSequence })).Distinct().ToArray() };
        Save(Path.Combine(directory, "coverage.json"), coverage);
        var run = new Run(directory, high, shared, on, exit, startup.Elapsed.TotalMilliseconds, (history.LastCommitTimestamp - firstServiceStart) * 1000d / Stopwatch.Frequency,
            history.Valid, client.Calls, client.Peak, client.InFlight, history.ProjectionMilliseconds, timeline.WriteMilliseconds, productionRawMilliseconds, hostJournalMilliseconds, history.EventCommitMilliseconds, history.StateCommitMilliseconds,
            coverage.DistinctOperationTimes, coverage.ModelSpanTicks, coverage.Created, coverage.Adopted, coverage.Discarded,
            lookaheadEvents, history.Operations, history.Events, requests, final);
        Save(Path.Combine(directory, "sample.json"), run);
        if (exit != 0 || history.Valid != count || client.InFlight != 0) { throw new InvalidDataException($"Invalid run {directory}: exit={exit}, valid={history.Valid}, inflight={client.InFlight}"); }
        if (count >= 16 && (coverage.DistinctOperationTimes < 2 || on && !high && (coverage.Created < 2 || coverage.Adopted < 2) || on && high && coverage.Discarded < 1)) {
            throw new InvalidDataException("Continuous matrix coverage failed: require multiple model times, repeated low-coupling candidate creation/adoption, high-coupling discard.");
        }
        return run;
    }
    private static void Compare(Run off, Run on, string output) {
        var watch = Stopwatch.StartNew();
        if (off.Operations.Count != on.Operations.Count) { throw new InvalidDataException("Operation count mismatch."); }
        var left = new Dictionary<string, string>(StringComparer.Ordinal);
        var right = new Dictionary<string, string>(StringComparer.Ordinal);
        var mappings = new List<object>();
        var replacements = new List<object>();
        for (int i = 0; i < off.Operations.Count; i++) {
            var offFact = off.Operations[i]; var onFact = on.Operations[i];
            var offContext = offFact.InteractionChanges.Single(c => c.ActorId == offFact.ActorId).CurrentContext!;
            var onContext = onFact.InteractionChanges.Single(c => c.ActorId == onFact.ActorId).CurrentContext!;
            if (offFact.ActorId != onFact.ActorId || offContext.ContextSequence != onContext.ContextSequence || offContext.LastOperationSequence != onContext.LastOperationSequence) { throw new InvalidDataException("Actor/context/operation identity mismatch."); }
            string a = offFact.OperationRecordId!, b = onFact.OperationRecordId!, canonical = $"@operation:{i:D6}@";
            if (a.Contains("@operation:", StringComparison.Ordinal) || b.Contains("@operation:", StringComparison.Ordinal)) { throw new InvalidDataException("Identity collides with canonical namespace."); }
            if (!left.TryAdd(a, canonical) || !right.TryAdd(b, canonical)) { throw new InvalidDataException("Operation identity is not bijective."); }
            mappings.Add(new { Index = i, Actor = offFact.ActorId, offContext.ContextSequence, offContext.LastOperationSequence, Off = a, On = b, Canonical = canonical });
        }
        string Normalize(object value, Dictionary<string, string> map) {
            JsonNode? root = JsonSerializer.SerializeToNode(value, Json);
            void Visit(JsonNode? node, string path) {
                if (node is JsonObject obj) {
                    foreach (var property in obj.ToArray()) {
                        string current = path + "/" + property.Key;
                        if (property.Key == "OperationRecordId" && property.Value is JsonValue identity && identity.TryGetValue<string>(out string? original) && original is not null) {
                            if (!map.TryGetValue(original, out string? canonical)) { throw new InvalidDataException("Unmapped operation identity at " + current); }
                            replacements.Add(new { Side = ReferenceEquals(map, left) ? "off" : "on", Object = value.GetType().FullName, Path = current, Original = original, Canonical = canonical });
                            obj[property.Key] = canonical;
                        } else { Visit(property.Value, current); }
                    }
                } else if (node is JsonArray array) {
                    for (int i = 0; i < array.Count; i++) { Visit(array[i], path + "/" + i); }
                }
            }
            Visit(root, "");
            return root!.ToJsonString(Json);
        }
        // Model-visible material has no permitted normalization, even inside arbitrary data strings.
        bool inputs = JsonSerializer.Serialize(off.Inputs, Json) == JsonSerializer.Serialize(on.Inputs, Json);
        bool operations = Normalize(off.Operations, left) == Normalize(on.Operations, right);
        bool facts = Normalize(off.Events, left) == Normalize(on.Events, right);
        bool final = Normalize(off.Final, left) == Normalize(on.Final, right);
        watch.Stop();
        Save(Path.Combine(output, Path.GetFileName(off.Directory) + "-comparison.json"), new { off.Directory, On = on.Directory,
            InputsEqual = inputs, OperationsEqual = operations, FactsEqual = facts, FinalEqual = final, Mappings = mappings, Replacements = replacements,
            ComparisonMilliseconds = watch.Elapsed.TotalMilliseconds });
        if (!inputs || !operations || !facts || !final) { throw new InvalidDataException("Paired full trajectory mismatch; preserve artifacts."); }
    }
    internal static async Task<Initial> Source(bool high) {
        var time = new ModelTime(10 * PlaygroundTime.Hour);
        var original = PlaygroundGameFactory.CreateSurvival(time, enableNeeds: false, enableWeather: false, characterCount: 2);
        var actors = new[] { new PlaygroundCharacter("a", "A"), new PlaygroundCharacter("b", "B") };
        string[] places = ["camp", high ? "stream" : "cabin"];
        var spatial = GraphSpatialState.Create(PlaygroundExpeditionMap.Graph, actors.Select((a, i) => new EntityPlacement(new(a.Id), new(places[i]))));
        var expedition = new PlaygroundExpedition(PlaygroundExpeditionMap.ScenarioId, spatial,
            actors.Select((a, i) => new PlaygroundVisit(a.Id, places[i], time, original.World.Resources.Where(r => PlaygroundExpeditionMap.GetResourcePlace(r.Id) == places[i]))));
        var business = new PlaygroundBusinessState(time, original.World.Light, original.World.Resources, original.World.Script, actors, expedition: expedition);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(PlaygroundEngine.Rebuild(business, time), original.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history);
        for (int i = 0; i < 10; i++) {
            if (PlaygroundEngine.NextDecisionActor(history.State, history.Cursor) == "a" && history.State.Business.InteractionStates["a"].CurrentContext?.Status == CharacterContextStatus.Open) {
                return new(history.State, history.Cursor);
            }
            if (await kernel.StepAsync(time) != StepStatus.Committed) { throw new InvalidDataException("Opening failed."); }
        }
        throw new InvalidDataException("Opening not reached.");
    }
    internal static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object Provenance() {
        string Git(params string[] args) {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in args) { start.ArgumentList.Add(arg); }
            using var process = Process.Start(start)!;
            string text = process.StandardOutput.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) { throw new IOException("git provenance failed"); }
            return text;
        }
        return new { Dependencies = new[] { "../durable-graph", "../atelia-storage", "../atelia-completion" }.Select(path => new { Path = Path.GetFullPath(path), Commit = Git("-C", path, "rev-parse", "HEAD").Trim(), Status = Git("-C", path, "status", "--porcelain"), Diff = Git("-C", path, "diff", "HEAD") }).ToArray(),
            Commit = Git("rev-parse", "HEAD").Trim(), Status = Git("status", "--porcelain"), Diff = Git("diff", "HEAD"),
            ExperimentFiles = Directory.GetFiles("experiments/PlaygroundLookahead").Order().Select(p => new { Path = p, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }).ToArray() };
    }
}
internal sealed record Initial(PlaygroundWorld World, KernelCursor Cursor);
internal sealed record Run(string Directory, bool High, bool Shared, bool On, int Exit, double StartupMilliseconds, double WorkMilliseconds,
    int ValidOperations, int PhysicalCalls, int PeakInFlight, int Undrained, double FactProjectionMilliseconds, double TimelineWriteMilliseconds, double ProductionRawWriteMilliseconds, double HostJournalAppendMilliseconds, double InMemoryEventCommitMilliseconds, double InMemoryStateCommitMilliseconds,
    int DistinctOperationTimes, long ModelSpanTicks, int Created, int Adopted, int Discarded,
    JsonElement[] LookaheadEvents, List<PlaygroundFact> Operations, List<JsonElement> Events, List<JsonElement> Inputs, JsonElement Final);
internal sealed class Fixture(bool high, bool shared, int delay, Timeline timeline) : ICompletionClient {
    private readonly SemaphoreSlim _slot = new(1);
    private int _calls, _inflight, _peak;
    public ConcurrentQueue<(int Id, string Input)> PhysicalInputs { get; } = new();
    public ConcurrentQueue<PhysicalService> Services { get; } = new();
    public int Calls => _calls;
    public int Peak => _peak;
    public int InFlight => _inflight;
    public string Name => "resident-input-keyed-fixture";
    public string ApiSpecId => "fixture";
    public async Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
        string input = JsonSerializer.Serialize(new { request.ModelId, request.PromptPrefix.SystemPrompt,
            Contract = GymMessageProjection.Contract(request.PromptPrefix.OutputContract),
            Shared = request.PromptPrefix.SharedContextMessages.Select(GymMessageProjection.Message).ToArray(), Tail = request.TailMessages.Select(GymMessageProjection.Message).ToArray() }, Program.Json);
        string material = ((ObservationMessage)request.PromptPrefix.SharedContextMessages.Single()).Content!;
        var marker = Regex.Match(material, "LLM 上下文开始 · \"(?<actor>[ab])\" · (?<context>[0-9]+)");
        if (!marker.Success) { throw new InvalidDataException("Missing fixture actor/context identity."); }
        string actor = marker.Groups["actor"].Value;
        bool first = !material.Contains("### 本轮操作 ", StringComparison.Ordinal);
        string command = high && first ? "shout resident-signal-" + actor :
            Regex.IsMatch(material, @"- `wait 1`：[^\r\n]*可执行。") ? "wait 1" :
            Regex.IsMatch(material, @"- `continue`：[^\r\n]*可执行。") ? "continue" : throw new InvalidDataException("No supported legal fixture action.");
        int id = Interlocked.Increment(ref _calls), active = Interlocked.Increment(ref _inflight);
        int old;
        do { old = _peak; if (old >= active) { break; } } while (Interlocked.CompareExchange(ref _peak, active, old) != old);
        PhysicalInputs.Enqueue((id, input));
        var clock = Stopwatch.StartNew(); bool held = false, serviceStarted = false, completed = false;
        double queue = 0; long serviceStart = 0; string? failure = null;
        timeline.Write("physical-start", new { PhysicalId = id, actor, Context = marker.Groups["context"].Value, Input = input, InputHash = Program.Hash(input), command, InFlight = active });
        try {
            if (shared) { await _slot.WaitAsync(cancellationToken); held = true; }
            queue = clock.Elapsed.TotalMilliseconds;
            serviceStarted = true; serviceStart = Stopwatch.GetTimestamp();
            timeline.Write("provider-service-started", new { PhysicalId = id, QueueMilliseconds = queue, NonCooperativeOnceStarted = shared });
            await Task.Delay(delay, shared ? CancellationToken.None : cancellationToken);
            completed = true;
            var result = new CompletionResult(new ActionMessage([new ActionBlock.ToolCall(new("act", "resident-fixture-tool", JsonSerializer.Serialize(new { command })))]), new(Name, ApiSpecId, request.ModelId));
            timeline.Write("physical-completed", new { PhysicalId = id, QueueMilliseconds = queue, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                Result = new { Message = GymMessageProjection.Message(result.Message), result.Invocation, result.Termination, result.Usage, result.Failure, result.Errors } });
            return result;
        } catch (Exception error) {
            failure = error.GetType().FullName;
            timeline.Write("physical-failed", new { PhysicalId = id, Error = failure, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds }); throw;
        } finally {
            double serviceMilliseconds = serviceStarted ? Stopwatch.GetElapsedTime(serviceStart).TotalMilliseconds : 0;
            var service = new PhysicalService(id, serviceStarted, completed, failure, serviceStarted ? queue : clock.Elapsed.TotalMilliseconds, serviceMilliseconds, clock.Elapsed.TotalMilliseconds);
            Services.Enqueue(service); timeline.Write("physical-observed", service);
            if (held) { _slot.Release(); } Interlocked.Decrement(ref _inflight);
        }
    }
}
internal sealed record PhysicalService(int PhysicalId, bool ServiceStarted, bool Completed, string? Failure, double QueueMilliseconds, double ServiceMilliseconds, double TotalMilliseconds);
internal sealed class RecordingHistory(InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, Timeline timeline) : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
    public PlaygroundWorld State => inner.State;
    public KernelCursor Cursor => inner.Cursor;
    public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
    public int Valid { get; private set; }
    public long LastCommitTimestamp { get; private set; }
    public double ProjectionMilliseconds { get; private set; }
    public double EventCommitMilliseconds { get; private set; }
    public double StateCommitMilliseconds { get; private set; }
    public List<PlaygroundFact> Operations { get; } = [];
    public List<JsonElement> Events { get; } = [];
    public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
        long started = Stopwatch.GetTimestamp();
        try { inner.CommitEvent(occurrence); }
        finally { EventCommitMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
    public void CommitState(PlaygroundWorld nextState, KernelCursor nextCursor) {
        var occurrence = inner.PendingEvent!;
        long started = Stopwatch.GetTimestamp();
        try { inner.CommitState(nextState, nextCursor); }
        finally { StateCommitMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        LastCommitTimestamp = Stopwatch.GetTimestamp();
        var watch = Stopwatch.StartNew();
        var projected = JsonSerializer.SerializeToElement(new { occurrence.CauseKey, occurrence.TargetInstant, Facts = occurrence.Facts, Cursor = nextCursor }, Program.Json);
        watch.Stop(); ProjectionMilliseconds += watch.Elapsed.TotalMilliseconds;
        Events.Add(projected);
        foreach (var fact in occurrence.Facts.Where(f => f.OperationRecordId is not null)) {
            Operations.Add(fact);
            if (fact.OperationDisposition == PlaygroundOperationDisposition.Succeeded && fact.ActorId is not null) { Valid++; }
        }
        timeline.Write("state-committed", new { Valid, Event = projected });
    }
}
internal sealed class Timeline(string path) : IDisposable {
    private readonly StreamWriter _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sequence;
    public double WriteMilliseconds { get; private set; }
    public void Write(string kind, object detail) {
        lock (_gate) {
            var watch = Stopwatch.StartNew();
            _writer.WriteLine(JsonSerializer.Serialize(new { Sequence = ++_sequence, ElapsedMilliseconds = _clock.Elapsed.TotalMilliseconds, Kind = kind, Detail = detail }, Program.Json));
            _writer.Flush(); watch.Stop(); WriteMilliseconds += watch.Elapsed.TotalMilliseconds;
        }
    }
    public void Dispose() => _writer.Dispose();
}
internal sealed class VisibleConverter : JsonConverter<PlaygroundVisibleValue> {
    public override PlaygroundVisibleValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
    public override void Write(Utf8JsonWriter writer, PlaygroundVisibleValue value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new { value.Kind, value.Units, value.Scale, value.Unit, value.Text, value.Boolean }, options);
}
