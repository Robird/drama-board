using DramaBoard.Goals;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>Plans actor-owned deliveries after world effects. Recovery installs these records without calling this layer.</summary>
internal static class PlaygroundInteractionRules {
    internal static PlaygroundFact OpenContext(PlaygroundWorld world, string actorId) {
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        if (!PlaygroundActions.NeedsDecision(world, actorId) || state.CurrentContext?.Status is CharacterContextStatus.Open or CharacterContextStatus.Terminal) {
            throw new InvalidOperationException("The character cannot open a decision context at this boundary.");
        }
        var fact = new PlaygroundFact(PlaygroundEventKind.DecisionContextOpened, world.Time, actorId,
            changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()], result: "已开启新的决策回合。");
        if (world.Business.Economy is { } economy) {
            PlaygroundTradeState trades = economy.Trades.OpenContext(actorId, checked((state.CurrentContext?.ContextSequence ?? 0) + 1),
                world.Characters.Where(actor => !actor.IsActive).Select(actor => actor.Id).ToArray());
            if (!economy.Trades.SameState(trades)) { fact = fact.WithEconomy(economy.Change(trades: trades)); }
        }
        PlaygroundWorld candidate = Candidate(world, fact);
        CharacterContext context = CreateContext(candidate, state, state.UndeliveredExperiences, terminal: false);
        // Reclaiming a delivered terminal offer may change the other participant's still-open view.
        // Record that ordinary observation update in this same boundary without creating another operation.
        var changes = Enrich(world, fact).InteractionChanges.ToDictionary(change => change.ActorId, StringComparer.Ordinal);
        changes[actorId] = new(actorId, context, nextExperienceSequence: state.NextExperienceSequence, goals: state.Goals);
        return fact.WithInteractions(world.Characters.Where(actor => changes.ContainsKey(actor.Id)).Select(actor => changes[actor.Id]));
    }

    internal static PlaygroundFact EditHandoff(PlaygroundWorld world, string actorId) => new(PlaygroundEventKind.HandoffEdited,
        world.Time, actorId, changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()],
        lastServedActorId: actorId, result: "已更新给下一回合的交接草稿。");

    internal static PlaygroundFact Reject(PlaygroundWorld world, string actorId, PlaygroundControllerOperation operation, string reason) {
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        CharacterContext current = state.CurrentContext is { Status: CharacterContextStatus.Open } open ? open
            : throw new InvalidOperationException("A rejected operation requires an open context.");
        long sequence = checked(current.LastOperationSequence + 1);
        GoalReceipt? goalReceipt = GoalCommandParser.TryGetKind(operation.RawInput, out GoalOperationKind kind)
            ? GoalRules.Reject(kind, reason) : null;
        var item = new CharacterContextItem(current.NextItemSequence, sequence, operation.RawInput,
            operation.ToolCallId, operation.OperationRecordId, reason, isRejected: true, goalOperation: goalReceipt);
        var context = new CharacterContext(current.ContextSequence, CharacterContextStatus.Open, current.Opening,
            RetainItems(current.Items, sequence).Append(item), checked(current.NextItemSequence + 1), sequence);
        var fact = new PlaygroundFact(PlaygroundEventKind.OperationRejected, world.Time, actorId,
            changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()], result: reason, goalOperation: goalReceipt);
        return fact.WithOperation(operation, PlaygroundOperationDisposition.Rejected).WithInteractions([
            new(actorId, context, state.PendingAdvance, state.UndeliveredExperiences, state.DraftHandoff, state.NextExperienceSequence, state.Goals)]);
    }

    internal static PlaygroundFact Enrich(PlaygroundWorld world, PlaygroundFact fact, PlaygroundControllerOperation? operation = null) {
        PlaygroundWorld candidate = Candidate(world, fact);
        PlaygroundCommand? command = operation is null ? null : PlaygroundCommandParser.Parse(operation.RawInput)
            ?? throw new InvalidOperationException("Accepted operations require valid syntax.");
        bool release = command is not null && ReleasesSimulation(candidate.Business, fact.ActorId!, command);
        string? draftAfter = command is null ? null : command.Kind == PlaygroundCommandKind.Handoff
            ? command.HandoffText : release ? null : world.Business.InteractionStates[fact.ActorId!].DraftHandoff;
        fact = PlaygroundObservedEvents.Attach(world, fact, draftAfter);
        var changes = new List<CharacterInteractionState>();
        foreach (PlaygroundCharacter actor in world.Characters) {
            CharacterInteractionState state = world.Business.InteractionStates[actor.Id];
            if (state.CurrentContext?.Status == CharacterContextStatus.Terminal) { continue; }
            long nextExperience = state.NextExperienceSequence;
            CharacterExperience[] experiences = fact.ObservedEvents.Where(entry => entry.ObserverId == actor.Id)
                .Select((entry, index) => new CharacterExperience(nextExperience++, LocalEvent(entry, index))).ToArray();
            // Material contains only this observer's local ordinal, never gaps revealing other recipients.
            if (nextExperience <= 0) { throw new InvalidOperationException("Experience identity exhausted."); }
            bool terminal = actor.IsActive && !candidate.GetCharacter(actor.Id).IsActive;
            if (terminal) {
                CharacterContext context = CreateContext(candidate, state, state.UndeliveredExperiences.Concat(experiences), terminal: true);
                changes.Add(new(actor.Id, context, nextExperienceSequence: nextExperience, goals: state.Goals));
                continue;
            }
            bool own = operation is not null && fact.ActorId == actor.Id;
            if (own && state.CurrentContext is not { Status: CharacterContextStatus.Open }) {
                throw new InvalidOperationException("A controller operation requires its committed open context.");
            }
            CharacterContext? current = state.CurrentContext;
            PendingAdvance? pending = state.PendingAdvance;
            if (own && release) {
                long sequence = checked(current!.LastOperationSequence + 1);
                pending = Advance(world, candidate, state, operation!, sequence);
                current = new(current.ContextSequence, CharacterContextStatus.Closed, current.Opening,
                    RetainItems(current.Items, sequence), current.NextItemSequence, sequence);
            }
            if (current?.Status != CharacterContextStatus.Open) {
                if (experiences.Length != 0 || own) {
                    changes.Add(new(actor.Id, current, pending, state.UndeliveredExperiences.Concat(experiences),
                        own ? draftAfter : state.DraftHandoff, nextExperience, state.Goals));
                }
                continue;
            }
            PlaygroundObservation observation = PlaygroundDecisionContext.Observe(candidate, actor.Id);
            if (!own && experiences.Length == 0 && PlaygroundObservationEquality.Same(current.LatestObservation, observation)) { continue; }
            long operationSequence = own ? checked(current.LastOperationSequence + 1) : current.LastOperationSequence;
            var item = new CharacterContextItem(current.NextItemSequence, own ? operationSequence : null,
                own ? operation!.RawInput : null, own ? operation!.ToolCallId : null,
                own ? operation!.OperationRecordId : null, own ? PlaygroundOperationResults.Describe(fact) : null,
                experiences, observation);
            current = new(current.ContextSequence, CharacterContextStatus.Open, current.Opening,
                RetainItems(current.Items, operationSequence).Append(item), checked(current.NextItemSequence + 1), operationSequence);
            changes.Add(new(actor.Id, current, draftHandoff: own ? draftAfter : state.DraftHandoff,
                nextExperienceSequence: nextExperience, goals: state.Goals));
        }
        return fact.WithInteractions(changes);
    }

    internal static bool ReleasesSimulation(PlaygroundBusinessState candidate, string actorId, PlaygroundCommand command) =>
        command.Kind is PlaygroundCommandKind.Go or PlaygroundCommandKind.Wait or PlaygroundCommandKind.Gather or PlaygroundCommandKind.Follow or PlaygroundCommandKind.Produce ||
        command.Kind == PlaygroundCommandKind.Continue && !PlaygroundActions.NeedsDecision(candidate, actorId);

    internal static IEnumerable<CharacterContextItem> RetainItems(IReadOnlyList<CharacterContextItem> items, long operationSequence) =>
        items.Where(item => !item.IsRejected || item.OperationSequence >= operationSequence - 2);

    private static PlaygroundWorld Candidate(PlaygroundWorld world, PlaygroundFact fact) {
        PlaygroundBusinessState business = PlaygroundFactApplication.ApplyReplacements(world.Business, new(fact.At, 0), fact);
        return new(business, world.Forecasts, world.Spatial);
    }

    private static CharacterContext CreateContext(PlaygroundWorld world, CharacterInteractionState state,
        IEnumerable<CharacterExperience> experiences, bool terminal) {
        long contextSequence = checked((state.CurrentContext?.ContextSequence ?? 0) + 1);
        PlaygroundObservation observation = PlaygroundDecisionContext.ObserveOpening(world, state.ActorId, contextSequence);
        PlaygroundCharacter actor = world.GetCharacter(state.ActorId);
        bool ongoing = actor.Activity is not null || actor.FollowingLeaderId is not null || PlaygroundActions.IsTravelling(world, state.ActorId);
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, state.PendingAdvance,
            world.Time, terminal ? PlaygroundOutcomeActivityState.Terminal : ongoing ? PlaygroundOutcomeActivityState.Ongoing : PlaygroundOutcomeActivityState.Completed,
            experiences);
        return new(contextSequence,
            terminal ? CharacterContextStatus.Terminal : CharacterContextStatus.Open, opening);
    }

    private static PendingAdvance Advance(PlaygroundWorld before, PlaygroundWorld after, CharacterInteractionState state,
        PlaygroundControllerOperation operation, long operationSequence) {
        PlaygroundCommand command = PlaygroundCommandParser.Parse(operation.RawInput)
            ?? throw new InvalidOperationException("An advancing operation requires valid syntax.");
        PlaygroundObservation known = state.CurrentContext!.LatestObservation;
        PlaygroundCharacter actor = after.GetCharacter(state.ActorId);
        SpatialEntity? entity = after.Expedition?.Spatial.Entities.Single(e => e.Id.Value == state.ActorId);
        string description = DescribeAdvance(command, known, entity?.Location is TraversingLocation);
        return new(state.ActorId, state.CurrentContext.ContextSequence, operationSequence, command, operation.RawInput,
            operation.ToolCallId, operation.OperationRecordId, before.Time, description, known.PlaceName,
            actor.Activity?.Id, entity?.Location is TraversingLocation ? entity.MovementGeneration : null, state.DraftHandoff);
    }

    internal static string DescribeAdvance(PlaygroundCommand command, PlaygroundObservation known, bool travelling) => command.Kind switch {
            PlaygroundCommandKind.Go => known.Passages.Single(p => p.Id == command.EntranceId).Description,
            PlaygroundCommandKind.Gather => $"采集{known.Resources.Single(r => r.Id == command.ResourceId).Name}",
            PlaygroundCommandKind.Wait => $"在原地等待 {command.Hours} 小时",
            PlaygroundCommandKind.Produce => $"在工坊生产 {command.RecipeId} ×{command.Count} 批",
            PlaygroundCommandKind.Follow => $"跟随 {known.OtherCharacters.Single(actor => actor.Id == command.TargetActorId).Name}（{command.TargetActorId}）",
            PlaygroundCommandKind.Continue when !travelling && known.FollowingLeaderId is not null =>
                $"确认提醒并继续跟随 {known.FollowingLeaderName}（{known.FollowingLeaderId}）",
            _ => travelling ? "确认提醒并继续原定行程" : "确认提醒并继续原定活动"
        };

    internal static PlaygroundObservedEvent LocalEvent(PlaygroundObservedEvent entry, int ordinal) => new(ordinal, entry.ObserverId,
        entry.Kind, entry.From, entry.At, entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects, entry.Communication, entry.Production, entry.TradeChanges);

}
