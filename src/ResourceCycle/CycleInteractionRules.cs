using System.Globalization;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Plans actor-owned deliveries after world effects. Recovery installs these records without calling this layer.</summary>
internal static class CycleInteractionRules {
    internal static CycleFact OpenContext(CycleWorld world, string actorId) {
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        if (!CycleActions.NeedsDecision(world, actorId) || state.CurrentContext?.Status is CharacterContextStatus.Open or CharacterContextStatus.Terminal) {
            throw new InvalidOperationException("The character cannot open a decision context at this boundary.");
        }
        var fact = new CycleFact(CycleEventKind.DecisionContextOpened, world.Time, actorId,
            changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()], result: "已开启新的决策回合。");
        CycleWorld candidate = Candidate(world, fact);
        CharacterContext context = CreateContext(candidate, state, state.UndeliveredExperiences, terminal: false);
        return fact.WithInteractions([new(actorId, context, nextExperienceSequence: state.NextExperienceSequence)]);
    }

    internal static CycleFact EditHandoff(CycleWorld world, string actorId) => new(CycleEventKind.HandoffEdited,
        world.Time, actorId, changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()],
        lastServedActorId: actorId, result: "已更新给下一回合的交接草稿。");

    internal static CycleFact Enrich(CycleWorld world, CycleFact fact, CycleControllerOperation? operation = null) {
        CycleWorld candidate = Candidate(world, fact);
        bool release = operation is not null && ReleasesSimulation(candidate.Business, fact.ActorId!, operation.Command);
        string? draftAfter = operation is null ? null : operation.Command.Kind == CycleCommandKind.Handoff
            ? operation.Command.HandoffText : release ? null : world.Business.InteractionStates[fact.ActorId!].DraftHandoff;
        fact = CycleObservedEvents.Attach(world, fact, draftAfter);
        var changes = new List<CharacterInteractionState>();
        foreach (CycleCharacter actor in world.Characters) {
            CharacterInteractionState state = world.Business.InteractionStates[actor.Id];
            if (state.CurrentContext?.Status == CharacterContextStatus.Terminal) { continue; }
            long nextExperience = state.NextExperienceSequence;
            CharacterExperience[] experiences = fact.ObservedEvents.Where(entry => entry.ObserverId == actor.Id)
                .Select((entry, index) => new CharacterExperience(nextExperience++, LocalEvent(entry, index))).ToArray();
            // Material contains only this observer's local ordinal, never gaps revealing other recipients.
            if (nextExperience <= 0) { throw new InvalidOperationException("Experience identity exhausted."); }
            bool terminal = fact.Outcome is CycleExpeditionOutcome.Dead or CycleExpeditionOutcome.Escaped;
            if (terminal) {
                CharacterContext context = CreateContext(candidate, state, state.UndeliveredExperiences.Concat(experiences), terminal: true);
                changes.Add(new(actor.Id, context, nextExperienceSequence: nextExperience));
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
                        own ? draftAfter : state.DraftHandoff, nextExperience));
                }
                continue;
            }
            CycleObservation observation = CycleDecisionContext.Observe(candidate, actor.Id);
            if (!own && experiences.Length == 0 && CycleObservationEquality.Same(current.LatestObservation, observation)) { continue; }
            long operationSequence = own ? checked(current.LastOperationSequence + 1) : current.LastOperationSequence;
            var item = new CharacterContextItem(current.NextItemSequence, own ? operationSequence : null,
                own ? CommandText(operation!.Command) : null, own ? operation!.ToolCallId : null,
                own ? operation!.OperationRecordId : null, own ? CycleOperationResults.Describe(fact) : null,
                experiences, observation);
            current = new(current.ContextSequence, CharacterContextStatus.Open, current.Opening,
                RetainItems(current.Items, operationSequence).Append(item), checked(current.NextItemSequence + 1), operationSequence);
            changes.Add(new(actor.Id, current, draftHandoff: own ? draftAfter : state.DraftHandoff,
                nextExperienceSequence: nextExperience));
        }
        return fact.WithInteractions(changes);
    }

    internal static bool ReleasesSimulation(CycleBusinessState candidate, string actorId, CycleCommand command) =>
        command.Kind is CycleCommandKind.Go or CycleCommandKind.Wait or CycleCommandKind.Gather ||
        command.Kind == CycleCommandKind.Continue && !CycleActions.NeedsDecision(candidate, actorId);

    internal static IEnumerable<CharacterContextItem> RetainItems(IReadOnlyList<CharacterContextItem> items, long operationSequence) =>
        items.Where(item => !item.IsRejected || item.OperationSequence >= operationSequence - 2);

    private static CycleWorld Candidate(CycleWorld world, CycleFact fact) {
        CycleBusinessState business = CycleFactApplication.ApplyReplacements(world.Business, new(fact.At, 0), fact);
        return new(business, world.Forecasts, world.Spatial);
    }

    private static CharacterContext CreateContext(CycleWorld world, CharacterInteractionState state,
        IEnumerable<CharacterExperience> experiences, bool terminal) {
        long contextSequence = checked((state.CurrentContext?.ContextSequence ?? 0) + 1);
        CycleObservation observation = CycleDecisionContext.ObserveOpening(world, state.ActorId, contextSequence);
        bool ongoing = world.GetCharacter(state.ActorId).Activity is not null || CycleActions.IsTravelling(world, state.ActorId);
        var opening = new CharacterContextOpening(CycleBackground.For(observation), observation, state.PendingAdvance,
            world.Time, terminal ? CycleOutcomeActivityState.Terminal : ongoing ? CycleOutcomeActivityState.Ongoing : CycleOutcomeActivityState.Completed,
            experiences);
        return new(contextSequence,
            terminal ? CharacterContextStatus.Terminal : CharacterContextStatus.Open, opening);
    }

    private static PendingAdvance Advance(CycleWorld before, CycleWorld after, CharacterInteractionState state,
        CycleControllerOperation operation, long operationSequence) {
        CycleCommand command = operation.Command;
        CycleObservation known = state.CurrentContext!.LatestObservation;
        CycleCharacter actor = after.GetCharacter(state.ActorId);
        SpatialEntity? entity = after.Expedition?.Spatial.Entities.Single(e => e.Id.Value == state.ActorId);
        string description = DescribeAdvance(command, known, entity?.Location is TraversingLocation);
        return new(state.ActorId, state.CurrentContext.ContextSequence, operationSequence, command,
            operation.ToolCallId, operation.OperationRecordId, before.Time, description, known.PlaceName,
            actor.Activity?.Id, entity?.Location is TraversingLocation ? entity.MovementGeneration : null, state.DraftHandoff);
    }

    internal static string DescribeAdvance(CycleCommand command, CycleObservation known, bool travelling) => command.Kind switch {
            CycleCommandKind.Go => known.Passages.Single(p => p.Id == command.PassageId).Description,
            CycleCommandKind.Gather => $"采集{known.Resources.Single(r => r.Id == command.ResourceId).Name}",
            CycleCommandKind.Wait => $"在原地等待 {command.Hours} 小时",
            _ => travelling ? "确认提醒并继续原定行程" : "确认提醒并继续原定活动"
        };

    internal static CycleObservedEvent LocalEvent(CycleObservedEvent entry, int ordinal) => new(ordinal, entry.ObserverId,
        entry.Kind, entry.From, entry.At, entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects);

    // Until raw submissions arrive in slice 2B, this is the exact structured intent expressed as a command.
    internal static string CommandText(CycleCommand command) => command.Kind switch {
        CycleCommandKind.Wait => $"wait {command.Hours.ToString(CultureInfo.InvariantCulture)}",
        CycleCommandKind.Gather => $"gather {command.ResourceId} {(command.All ? "all" : command.Count.ToString(CultureInfo.InvariantCulture))}",
        CycleCommandKind.Go => $"go {command.PassageId}",
        CycleCommandKind.Use => $"use {command.ItemId}",
        CycleCommandKind.Handoff => command.HandoffText is null ? "handoff --clear" : $"handoff {command.HandoffText}",
        CycleCommandKind.Continue => "continue",
        CycleCommandKind.Cancel => "cancel",
        _ => throw new InvalidOperationException("Unknown character command.")
    };
}
