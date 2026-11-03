using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class ContextDiagnosticsTests {
    private static readonly JsonSerializerOptions MaterialJson = new() { Converters = { new VisibleValueConverter() } };

    [Fact]
    public async Task Enabling_the_host_sink_does_not_change_gameplay_or_retained_character_material() {
        string directory = Path.Combine(Path.GetTempPath(), "drama-diagnostic-equivalence-" + Guid.NewGuid().ToString("N"));
        try {
            var records = new List<PlaygroundDiagnosticRecord>();
            string disabled = await RunHost("disabled", null);
            string enabled = await RunHost("enabled", records.Add);
            Assert.Equal(disabled, enabled);
            Assert.Contains(records, record => record.Completion.Event.Facts.Any(fact => fact.OperationDisposition == PlaygroundOperationDisposition.Rejected));
            Assert.Contains(records, record => record.Completion.Event.Facts.Any(fact => fact.Kind == PlaygroundEventKind.MoveArrived));

            async Task<string> RunHost(string name, Action<PlaygroundDiagnosticRecord>? sink) {
                string store = Path.Combine(directory, name);
                var initial = PlaygroundGameFactory.CreateSurvival();
                using (var saved = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor)) { }
                using var input = new StringReader("use item.water\ninvalid\nhandoff 等待查看溪谷\ngo e-c7\n:exit\n");
                using var output = new StringWriter();
                Assert.Equal(0, await HostFixture.RunHumanAsync(store, default, diagnosticSink: sink,
                    inputReader: input, outputWriter: output));
                using var history = PlaygroundDurableHistory.Open(store);
                JsonNode material = JsonSerializer.SerializeToNode(new { history.State.Business, history.Cursor }, MaterialJson)!;
                NormalizeHostGeneratedIdentities(material);
                return material.ToJsonString();
            }
        } finally {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Fact]
    public async Task Optional_diagnostics_preserve_world_and_context_and_link_the_original_committed_material() {
        var records = new List<PlaygroundDiagnosticRecord>();
        var disabled = await AdvanceToNextContext(null);
        var enabled = await AdvanceToNextContext(records.Add);

        // Include all public business/Context material, rather than the abbreviated diagnostic ToString.
        Assert.Equal(Snapshot(disabled.World.Business), Snapshot(enabled.World.Business));
        Assert.Equal(Snapshot(disabled.Cursor), Snapshot(enabled.Cursor));
        Assert.NotEmpty(records);
        Assert.Contains(records, record => record.Completion.Event.Facts.Any(fact => fact.Kind == PlaygroundEventKind.MoveArrived));
        foreach (PlaygroundDiagnosticRecord record in records) {
            Assert.Equal(record.Completion.Event.Facts.Sum(fact => fact.ObservedEvents.Count), record.Experiences.Count);
            foreach (PlaygroundExperienceAssociation association in record.Experiences) {
                PlaygroundFact fact = record.Completion.Event.Facts[association.FactOrdinal];
                PlaygroundObservedEvent original = Assert.Single(fact.ObservedEvents,
                    entry => entry.EventOrdinal == association.EventOrdinal);
                CharacterInteractionState changed = Assert.Single(fact.InteractionChanges,
                    state => state.ActorId == association.ActorId);
                CharacterExperience delivered = Assert.Single(Experiences(changed),
                    experience => experience.ExperienceSequence == association.ExperienceSequence);
                Assert.Equal(original.ObserverId, association.ActorId);
                Assert.Equal(original.Kind, delivered.Event.Kind);
                Assert.Equal(original.At, delivered.Event.At);
                Assert.Equal(Snapshot(original.Effects), Snapshot(delivered.Event.Effects));
            }
        }
        PlaygroundDiagnosticRecord opening = records.Last();
        Assert.All(opening.Completion.Event.Facts, fact => Assert.Equal(PlaygroundEventKind.DecisionContextOpened, fact.Kind));
        Assert.NotEmpty(opening.Completion.Event.Facts.Single().InteractionChanges.Single().CurrentContext!.Opening.Experiences);
        Assert.Empty(opening.Experiences); // Transferring old experiences into an Opening is not a second world event.
    }

    [Fact]
    public async Task Diagnostic_failure_stops_the_actual_host_after_commit_and_cold_open_does_not_repeat_delivery() {
        string store = Path.Combine(Path.GetTempPath(), "drama-context-diagnostic-" + Guid.NewGuid().ToString("N"));
        try {
            var initial = PlaygroundGameFactory.CreateSurvival();
            using (var saved = PlaygroundDurableHistory.Create(store, initial.World, initial.Cursor)) { }
            using var input = new StringReader("use item.water\nhandoff should-not-run\n:exit\n");
            using var output = new StringWriter();
            PlaygroundDiagnosticRecord? failed = null;
            var sinkError = new IOException("diagnostic-only-failure");
            IOException error = await Assert.ThrowsAsync<IOException>(() => HostFixture.RunHumanAsync(store, default,
                diagnosticSink: record => {
                    if (!record.Completion.Event.Facts.Any(fact => fact.Kind == PlaygroundEventKind.ItemUsed)) { return; }
                    failed = record;
                    throw sinkError;
                }, inputReader: input, outputWriter: output));
            Assert.Same(sinkError, error.InnerException);
            Assert.Contains("已提交", error.Message);
            Assert.NotNull(failed);
            Assert.Equal("handoff should-not-run", input.ReadLine());
            Assert.DoesNotContain("diagnostic-only-failure", output.ToString());

            string committedContext;
            long transition;
            using (var history = PlaygroundDurableHistory.Open(store)) {
                Assert.Null(history.PendingEvent);
                Assert.Equal(90 * PlaygroundSurvival.Scale, history.State.GetCharacter("a").Survival!.WaterUnits);
                Assert.Equal(0, history.State.GetCharacter("a").Backpack.Count("item.water"));
                CharacterInteractionState state = history.State.Business.InteractionStates["a"];
                Assert.Null(state.DraftHandoff);
                Assert.Equal("use item.water", Assert.Single(state.CurrentContext!.Items).RawInput);
                committedContext = Snapshot(state);
                transition = history.Cursor.Version.TransitionCount;
                Assert.Equal(failed!.Completion.Cursor.Version.TransitionCount, transition);
            }

            var recoveredRecords = new List<PlaygroundDiagnosticRecord>();
            using var reopenedInput = new StringReader(":exit\n");
            using var reopenedOutput = new StringWriter();
            Assert.Equal(0, await HostFixture.RunHumanAsync(store, default,
                diagnosticSink: recoveredRecords.Add, inputReader: reopenedInput, outputWriter: reopenedOutput));
            Assert.Empty(recoveredRecords);
            using var reopened = PlaygroundDurableHistory.Open(store);
            Assert.Equal(transition, reopened.Cursor.Version.TransitionCount);
            Assert.Equal(committedContext, Snapshot(reopened.State.Business.InteractionStates["a"]));
            using var operationsStore = PlaygroundHostJournal.Open(PlaygroundHostJournal.JournalPath(store));
            var operations = new PlaygroundOperationAudit(operationsStore, reopened.CurrentBranch.BranchId, "test-run");
            PlaygroundOperationEntry used = Assert.Single(operations.Entries, entry => entry.Received.RawInput == "use item.water");
            Assert.Equal(PlaygroundOperationStatus.Succeeded, used.Completed!.Status);
            Assert.Equal(failed!.Completion.Event.Facts.Single().ToolCallId, used.Completed.ToolCallId);
        } finally {
            if (Directory.Exists(store)) { Directory.Delete(store, recursive: true); }
        }
    }

    [Fact]
    public async Task Pending_event_recovery_restores_character_material_without_emitting_it_as_a_new_diagnostic_occurrence() {
        string store = Path.Combine(Path.GetTempPath(), "drama-diagnostic-pending-" + Guid.NewGuid().ToString("N"));
        try {
            var initial = PlaygroundGameFactory.CreateSurvival();
            var memory = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
            var kernel = PlaygroundEngine.CreateKernel(memory, characterController: new ScriptedController());
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            PlaygroundWorld openedWorld = kernel.World;
            KernelCursor openedCursor = kernel.Cursor;
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            using (var durable = PlaygroundDurableHistory.Create(store, openedWorld, openedCursor)) {
                durable.CommitEvent(kernel.LastCompletion!.Event);
            }
            var records = new List<PlaygroundDiagnosticRecord>();
            using var input = new StringReader(":exit\n");
            using var output = new StringWriter();
            Assert.Equal(0, await HostFixture.RunHumanAsync(store, default,
                diagnosticSink: records.Add, inputReader: input, outputWriter: output));
            Assert.Empty(records);
            using var restored = PlaygroundDurableHistory.Open(store);
            Assert.Null(restored.PendingEvent);
            Assert.Equal(kernel.Cursor.Version, restored.Cursor.Version);
            Assert.Equal(Snapshot(kernel.World.Business), Snapshot(restored.State.Business));
        } finally {
            if (Directory.Exists(store)) { Directory.Delete(store, recursive: true); }
        }
    }

    private static async Task<(PlaygroundWorld World, KernelCursor Cursor)> AdvanceToNextContext(Action<PlaygroundDiagnosticRecord>? sink) {
        var initial = PlaygroundGameFactory.CreateSurvival();
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(initial.World, initial.Cursor);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: new ScriptedController());
        for (int step = 0; step < 30; step++) {
            Assert.Equal(StepStatus.Committed, await kernel.StepAsync(new ModelTime(long.MaxValue)));
            PlaygroundContextDiagnostics.Publish(kernel.LastCompletion!, sink);
            if (kernel.World.Business.InteractionStates["a"].CurrentContext is { ContextSequence: 2, Status: CharacterContextStatus.Open }) {
                return (kernel.World, kernel.Cursor);
            }
        }
        throw new InvalidOperationException("The scripted journey did not open its second context.");
    }

    private static IEnumerable<CharacterExperience> Experiences(CharacterInteractionState state) =>
        state.UndeliveredExperiences.Concat(state.CurrentContext is { } context
            ? context.Opening.Experiences.Concat(context.Items.SelectMany(item => item.Experiences))
            : []);

    private static void NormalizeHostGeneratedIdentities(JsonNode node) {
        // Independent Human Console sessions deliberately create different opaque IDs. All other
        // persisted fields, including the complete visible Context graph, must be exactly equal.
        if (node is JsonObject obj) {
            foreach (string key in obj.Select(pair => pair.Key).ToArray()) {
                if (key is "ToolCallId" or "OperationRecordId" && obj[key] is not null) {
                    obj[key] = "host-generated-identity";
                } else if (obj[key] is { } child) {
                    NormalizeHostGeneratedIdentities(child);
                }
            }
        } else if (node is JsonArray array) {
            foreach (JsonNode? child in array) {
                if (child is not null) { NormalizeHostGeneratedIdentities(child); }
            }
        }
    }

    private static string Snapshot(object value) => JsonSerializer.Serialize(value, MaterialJson);

    // Serialize the entire discriminated value, not its numeric-only convenience accessor. This
    // test fingerprint keeps all six persistent fields even for nonnumeric kinds.
    private sealed class VisibleValueConverter : JsonConverter<PlaygroundVisibleValue> {
        public override PlaygroundVisibleValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The diagnostic test fingerprint is write-only.");

        public override void Write(Utf8JsonWriter writer, PlaygroundVisibleValue value, JsonSerializerOptions options) {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(value.Kind), (int)value.Kind);
            writer.WriteNumber(nameof(value.Units), value.Units);
            writer.WriteNumber(nameof(value.Scale), value.Scale);
            writer.WriteString(nameof(value.Unit), value.Unit);
            writer.WriteString(nameof(value.Text), value.Text);
            writer.WriteBoolean(nameof(value.Boolean), value.Boolean);
            writer.WriteEndObject();
        }
    }

    private sealed class ScriptedController : IPlaygroundCharacterController {
        private int _calls;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string raw = _calls++ switch {
                0 => "use item.water",
                1 => "handoff 保留经历",
                2 => "go e-c7",
                _ => throw new InvalidOperationException("No further input is expected.")
            };
            return ValueTask.FromResult(new PlaygroundControllerOperation("call-" + _calls, raw, "record-" + _calls));
        }
    }
}
