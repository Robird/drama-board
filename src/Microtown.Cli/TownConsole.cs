using Atelia.Completion.Abstractions;
using DramaBoard.CharacterControl.Completion;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Microtown.Cli.Gym;
using DramaBoard.Microtown.Completion;

namespace DramaBoard.Microtown.Cli;

/// <summary>Serial game host. Persisted facts authorize effects; model/Gym records are evidence only.</summary>
public static class TownConsole {
    public static async Task<int> RunAsync(TownHostOptions options, TextReader? input = null,
        TextWriter? output = null, ICompletionClient? suppliedClient = null, CancellationToken cancellationToken = default) {
        options.Validate();
        input ??= Console.In; output ??= Console.Out;
        using TownDurableHistory? durable = options.Store is null ? null : Directory.Exists(options.Store)
            ? TownDurableHistory.Open(options.Store, options.Branch is null ? null : TownDurableHistory.ParseBranchId(options.Branch))
            : CreateStore(options.Store, options.Hours);
        IOccurrenceHistory<TownWorld, TownFact> history;
        if (durable is not null) {
            history = durable;
            output.WriteLine($"[Host] Store: {Path.GetFullPath(options.Store!)}; branch: {TownDurableHistory.FormatBranchId(durable.BranchId)}");
        } else {
            var created = TownGameFactory.Create(options.Hours);
            history = new InMemoryOccurrenceHistory<TownWorld, TownFact>(created.World, created.Cursor);
            output.WriteLine("[Host] In-memory game; use --store to retain progress.");
        }
        using TownGymSession? gym = options.Gym || options.Feedback ? new(options.Store!, durable!, new(
            Model: options.Connection.Model, Connection: options.Connection.Connection, MaxCalls: options.MaxCalls,
            MaxOperations: options.MaxOperations, FeedbackEnabled: options.Feedback, HumanActor: options.HumanActor,
            LlmEnabled: options.Llm, ExperimentNote: options.ExperimentNote, MaxFailures: options.MaxFailures,
            MaxContextCharacters: options.MaxContextCharacters)) : null;
        CompletionConnectionBinding? binding = null;
        TownLlmCharacterController? controller = null;
        int operations = 0;
        string stopReason = "host-scope-ended";
        var kernel = TownEngine.CreateControlledKernel(history, async (observation, token) => {
            token.ThrowIfCancellationRequested();
            if (operations >= options.MaxOperations) { throw new PauseRequested("Operation budget reached."); }
            TownControllerOperation operation;
            if (options.Llm && observation.ActorId != options.HumanActor) {
                if (controller is null) {
                    string model;
                    ICompletionClient client;
                    if (suppliedClient is not null) { client = suppliedClient; model = options.Connection.Model ?? "gpt-6.1-sol"; }
                    else {
                        binding = await CompletionConnectionBinding.ConnectAsync(options.Connection, token);
                        client = binding.Client; model = binding.Model;
                    }
                    output.WriteLine($"[Host] LLM binding: {model}; shared call budget {options.MaxCalls}.");
                    gym?.RecordBinding(model, binding?.ConnectionId ?? "supplied-client");
                    controller = new(client, model, new() { MaxCalls = options.MaxCalls,
                        MaxConsecutiveProtocolFailures = options.MaxFailures, MaxContextCharacters = options.MaxContextCharacters },
                        trace => {
                            gym?.Trace(trace);
                            if (trace.Kind == LlmControllerTraceKind.Request) {
                                output.WriteLine($"[Model request: {trace.CharacterId}, call {trace.CallNumber}, {trace.RequestId}]");
                                foreach (ObservationMessage material in trace.Request.PromptPrefix.SharedContextMessages.OfType<ObservationMessage>()) {
                                    output.WriteLine(material.Content);
                                }
                            } else if (trace.Operation is { } chosen) {
                                output.WriteLine($"[Model proposed: {trace.CharacterId}] {chosen.RawInput}");
                            } else if (trace.Kind == LlmControllerTraceKind.Feedback) {
                                output.WriteLine($"[Feedback saved: {trace.CharacterId}] {trace.FeedbackId}");
                            }
                        }, options.Feedback ? gym!.SaveFeedback : null,
                        observation => TownTextRenderer.Render(observation) + (options.ExperimentNote is null ? "" :
                            "\n## Developer experiment instruction (not a world fact or character memory)\n" + options.ExperimentNote + "\n"));
                }
                operation = await controller.ChooseCommandAsync(observation, token);
            } else {
                string material = TownTextRenderer.Render(observation);
                output.WriteLine(material);
                while (true) {
                    output.Write($"action[{observation.ActorId}] (:look, :quit)> ");
                    string? raw = await input.ReadLineAsync(token);
                    if (raw is null || raw.Trim() == ":quit") { throw new PauseRequested(raw is null ? "End of input." : "Requested by Human."); }
                    if (raw.Trim() == ":look") { output.WriteLine(material); continue; }
                    operation = new(raw, $"console-{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"));
                    gym?.RecordOperationReceived(observation.ActorId, operation);
                    break;
                }
            }
            operations++;
            return operation;
        });
        int result = 0;
        try {
            if (kernel.RecoverPending(cancellationToken)) { output.WriteLine("[Host] Recovered one pending occurrence without requesting another action."); }
            gym?.ReconcileCommitted();
            int steps = 0;
            while (!kernel.World.Business.Ended) {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > 100000) { throw new PauseRequested("Host transition budget reached."); }
                StepStatus status = await kernel.StepAsync(kernel.World.Business.EndTime, cancellationToken);
                if (status != StepStatus.Committed) { throw new InvalidOperationException($"Unexpected simulation stop: {status}"); }
                gym?.ReconcileCommitted();
                foreach (TownFact fact in kernel.LastCompletion!.Event.Facts) {
                    if (fact.Kind == TownEventKind.Operation) {
                        output.WriteLine($"[Result: {fact.ActorId}] {(fact.Accepted ? "Accepted" : "Rejected")}: {fact.Result}");
                    }
                }
            }
            stopReason = "experiment-ended";
            output.WriteLine("[Host] Experiment endpoint reached.");
        } catch (PauseRequested pause) {
            stopReason = pause.Message;
            output.WriteLine($"[Host] Paused: {pause.Message}");
        } catch (LlmControllerPausedException pause) {
            stopReason = pause.ReasonCode; result = 2;
            output.WriteLine($"[Host] LLM paused: {pause.ReasonCode}; calls={pause.CallsMade}. No substitute action was chosen.");
        } catch (OperationCanceledException) {
            stopReason = "cancelled"; result = 130;
            output.WriteLine("[Host] Cancelled; completed game state is retained when --store is supplied.");
        } catch (Exception error) {
            // Provider/configuration error messages can contain payloads or credentials. Record a type, never their message.
            stopReason = "host-failure/" + error.GetType().Name; result = 1;
            output.WriteLine($"[Host] Stopped ({error.GetType().Name}); reopen to inspect durable progress. No automatic retry.");
        } finally {
            try { gym?.Stop(stopReason); }
            finally { if (binding is not null) { await binding.DisposeAsync(); } }
        }
        TownStatistics.PrintStatistics(kernel.World.Business, output);
        if (durable is not null && !durable.IsFaulted) {
            output.WriteLine($"[Host] Completed state: {TownDurableHistory.FormatCommitId(durable.CompletedStateCommitId)}; reopen the same store to continue.");
        }
        return result;
    }

    private static TownDurableHistory CreateStore(string path, int hours) {
        var initial = TownGameFactory.Create(hours);
        return TownDurableHistory.Create(path, initial.World, initial.Cursor);
    }
    private sealed class PauseRequested(string message) : Exception(message);
}
