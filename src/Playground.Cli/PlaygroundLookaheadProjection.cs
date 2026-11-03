using System.Diagnostics;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Time;
using DramaBoard.Kernel.Simulation;

namespace DramaBoard.Playground.Cli;

/// <summary>One disposable counterfactual, using the game's real rules and independent history.</summary>
internal static class PlaygroundLookaheadProjection {
    internal enum StopReason { TargetReady, InvalidSource, CannotPredict, RepeatedActor, Human, UnknownControl, UnknownPlace, SamePlace, StepLimit, ModelTimeLimit, PreparationLimit, Exhausted }
    internal sealed record Result(PlaygroundDecisionContext? Target, StopReason StopReason, int Steps, TimeSpan Elapsed, string? PredictedCommand);

    internal static async Task<Result> ProjectAsync(PlaygroundWorld source, KernelCursor cursor, string actorId,
        IReadOnlyDictionary<string, CharacterControlBinding> bindings, CancellationToken token,
        int maximumSteps = 128, long maximumModelTicks = 24 * PlaygroundTime.Hour, TimeSpan? preparationLimit = null) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (maximumSteps < 0) { throw new ArgumentOutOfRangeException(nameof(maximumSteps)); }
        if (maximumModelTicks < 0) { throw new ArgumentOutOfRangeException(nameof(maximumModelTicks)); }
        TimeSpan limit = preparationLimit ?? TimeSpan.FromMilliseconds(250);
        if (limit < TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(preparationLimit)); }
        var watch = Stopwatch.StartNew();
        int steps = 0;
        string? predictedRaw = null;
        Result Finish(StopReason reason, PlaygroundDecisionContext? target = null) => new(target, reason, steps, watch.Elapsed, predictedRaw);
        token.ThrowIfCancellationRequested();
        if (cursor.CurrentModelTime != source.Time || !source.Business.InteractionStates.TryGetValue(actorId, out var sourceInteraction) ||
            sourceInteraction.CurrentContext?.Status != CharacterContextStatus.Open) {
            return Finish(StopReason.InvalidSource);
        }
        var initial = PlaygroundDecisionContext.Create(source, actorId);
        var actor = source.GetCharacter(actorId);
        bool continuing = actor.Activity is not null || actor.FollowingLeaderId is not null ||
            PlaygroundActions.Location(source, actorId) is DramaBoard.Spatial.TraversingLocation;
        PlaygroundCommand predicted = continuing ? PlaygroundCommand.Continue() : PlaygroundCommand.Wait(1);
        if (initial.Check(predicted) is not null) { return Finish(StopReason.CannotPredict); }
        string raw = continuing ? "continue" : "wait 1";
        predictedRaw = raw;
        string identity = "lookahead:" + Guid.NewGuid().ToString("N");
        var controller = new ProjectionController(actorId, raw, identity, () => watch.Elapsed >= limit);
        var history = new InMemoryOccurrenceHistory<PlaygroundWorld, PlaygroundFact>(source, cursor);
        var kernel = PlaygroundEngine.CreateKernel(history, characterController: controller);
        ModelTime boundary = new(source.Time.Ticks > long.MaxValue - maximumModelTicks ? long.MaxValue : source.Time.Ticks + maximumModelTicks);
        while (true) {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed >= limit) { return Finish(StopReason.PreparationLimit); }
            if (steps >= maximumSteps) { return Finish(StopReason.StepLimit); }
            try {
                StepStatus status = await kernel.StepAsync(boundary, token).ConfigureAwait(false);
                if (status == StepStatus.BoundaryReached) { return Finish(StopReason.ModelTimeLimit); }
                if (status == StepStatus.Exhausted) { return Finish(StopReason.Exhausted); }
                steps++;
            } catch (ProjectionStopped stopped) {
                if (stopped.InvalidSource) { return Finish(StopReason.InvalidSource); }
                if (stopped.Context is null || watch.Elapsed >= limit) { return Finish(StopReason.PreparationLimit); }
                PlaygroundDecisionContext next = stopped.Context;
                string nextId = next.InteractionState.ActorId;
                if (nextId == actorId) { return Finish(StopReason.RepeatedActor); }
                if (!bindings.TryGetValue(nextId, out var binding)) { return Finish(StopReason.UnknownControl); }
                if (binding is CharacterControlBinding.HumanConsole) { return Finish(StopReason.Human); }
                if (binding is not CharacterControlBinding.Llm) { return Finish(StopReason.UnknownControl); }
                string? from = PlaygroundActions.CurrentPlace(kernel.World, actorId);
                string? to = PlaygroundActions.CurrentPlace(kernel.World, nextId);
                if (from is null || to is null) { return Finish(StopReason.UnknownPlace); }
                if (from == to) { return Finish(StopReason.SamePlace); }
                return Finish(StopReason.TargetReady, next);
            }
        }
    }

    // Throw before returning any operation for the target: no target Plan/Fold/feedback executes.
    private sealed class ProjectionStopped(PlaygroundDecisionContext? context, bool invalidSource = false) : Exception {
        internal PlaygroundDecisionContext? Context { get; } = context;
        internal bool InvalidSource { get; } = invalidSource;
    }
    private sealed class ProjectionController(string actorId, string raw, string identity, Func<bool> expired) : IPlaygroundCharacterController {
        private bool _used;
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (expired()) { throw new ProjectionStopped(null); }
            if (!_used && context.InteractionState.ActorId != actorId) { throw new ProjectionStopped(null, invalidSource: true); }
            if (_used) { throw new ProjectionStopped(context); }
            _used = true;
            return ValueTask.FromResult(new PlaygroundControllerOperation(identity, raw, identity));
        }
    }
}
