using Atelia.DurableGraph;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

[DurableType("DramaBoard.Playground.Navigation", 1)]
public sealed partial record PlaygroundNavigation(
    [field: DurableField(1)] PlaygroundPath Path,
    [field: DurableField(2)] int CompletedSteps) : IDurableObject {
    internal static bool Same(PlaygroundNavigation? a, PlaygroundNavigation? b) => ReferenceEquals(a, b) ||
        a is not null && b is not null && a.CompletedSteps == b.CompletedSteps && PlaygroundPath.Same(a.Path, b.Path);
}

[DurableType("DramaBoard.Playground.NavigationChange", 1)]
public sealed partial record PlaygroundNavigationChange(
    [field: DurableField(1)] string ActorId,
    [field: DurableField(2)] PlaygroundNavigation? Navigation) : IDurableObject;

/// <summary>One segment shared by ordinary movement and explicit, stop-at-each-place navigation.</summary>
internal static class PlaygroundNavigationRules {
    internal static PlaygroundPassageDefinition? NextEntrance(PlaygroundBusinessState state, string actorId, PlaygroundCommand command) {
        if (PlaygroundActions.Location(state, actorId) is not AtPlaceLocation at) { return null; }
        string? entranceId = command.Kind == PlaygroundCommandKind.Go ? command.EntranceId : null;
        PlaygroundPath? path = command.Kind == PlaygroundCommandKind.Navigate
            ? state.Expedition?.CandidatePath(actorId) is { } candidate && candidate.Id == command.PathId ? candidate : null
            : command.Kind == PlaygroundCommandKind.Continue ? state.Expedition?.Navigation(actorId)?.Path : null;
        int progress = command.Kind == PlaygroundCommandKind.Continue ? state.Expedition?.Navigation(actorId)?.CompletedSteps ?? 0 : 0;
        if (path is not null && progress < path.Steps.Count) { entranceId = path.Steps[progress].EntranceId; }
        return entranceId is null ? null : state.Definition.GetEntrances(at.PlaceId.Value).SingleOrDefault(p => p.EntranceId == entranceId);
    }

    internal static string? CheckDeparture(PlaygroundWorld world, string actorId, PlaygroundPassageDefinition? entrance) {
        if (entrance is null) { return "当前没有可用的下一段入口；所有人保持原状。"; }
        foreach (PlaygroundCharacter member in PlaygroundFollowingRules.MovingMembers(world.Business, actorId)) {
            if (!member.IsActive || member.Activity is not null || PlaygroundActions.CurrentPlace(world, member.Id) != entrance.From ||
                member.DecisionGeneration >= long.MaxValue - 2 ||
                world.Definition.Runtime.Planner.TryStartTraversal(world.Expedition!.Spatial, new(member.Id), new(entrance.Id), 1, world.Time) is not SpatialPlanAccepted) {
                return $"同行成员 {member.Name} 当前无法出发；所有人保持原状。";
            }
        }
        return null;
    }

    internal static string? CheckNavigate(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        if (command != PlaygroundCommand.Navigate(command.PathId!)) { return "navigate 只接受一份候选路径编号。"; }
        if (world.Expedition is null) { return "此场景未启用导航。"; }
        if (world.Expedition.Navigation(actorId) is not null) { return "已有活动导航，请先 cancel。"; }
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        if (actor.Activity is not null || actor.FollowingLeaderId is not null) { return "请先取消活动或解除跟随，再启动导航。"; }
        PlaygroundPath? candidate = world.Expedition.CandidatePath(actorId);
        if (candidate is null || candidate.Id != command.PathId) { return "这份路径不是本人当前候选，可能已被替换；请使用当前候选路径编号。"; }
        if (PlaygroundActions.CurrentPlace(world, actorId) != candidate.OriginPlaceId) { return "当前位置与路径起点不同，不能启动这份导航。"; }
        return candidate.Steps.Count == 0 ? null : CheckDeparture(world, actorId, NextEntrance(world.Business, actorId, command));
    }

    internal static PlaygroundFact Move(PlaygroundWorld world, string actorId, PlaygroundCommand command) {
        PlaygroundPassageDefinition entrance = NextEntrance(world.Business, actorId, command)!;
        string result = $"沿入口 {entrance.EntranceId} 出发；本段移动不可取消。";
        PlaygroundCharacter[] members = PlaygroundFollowingRules.MovingMembers(world.Business, actorId).ToArray();
        GraphSpatialFact[] movements = members.SelectMany(member => ((SpatialPlanAccepted)world.Definition.Runtime.Planner.TryStartTraversal(
            world.Expedition!.Spatial, new(member.Id), new(entrance.Id), 1, world.Time)).Facts).ToArray();
        PlaygroundNavigationChange? change = command.Kind == PlaygroundCommandKind.Navigate
            ? new(actorId, new(world.Expedition!.CandidatePath(actorId)!, 0)) : null;
        return new(PlaygroundEventKind.MoveStarted, world.Time, actorId,
            changedCharacters: members.Select(member => member.Id == actorId
                ? member.Change(null, notices: [], result: result, communication: member.Communication.Acknowledge()) : member.Change(null)),
            lastServedActorId: actorId, result: result, spatialFacts: movements, navigationChange: change);
    }

    internal static PlaygroundFact Instant(PlaygroundWorld world, string actorId, PlaygroundCommand command, PlaygroundControllerOperation operation) {
        bool cancel = command.Kind == PlaygroundCommandKind.Cancel;
        string result = cancel ? "已取消剩余导航；已开始的路段、到达期限和待确认提醒保持不变。" : "已在路径目标地点，零段导航完成，无需移动。";
        var fact = new PlaygroundFact(cancel ? PlaygroundEventKind.NavigationCancelled : PlaygroundEventKind.NavigationCompleted,
            world.Time, actorId, changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()], lastServedActorId: actorId,
            result: result, navigationChange: cancel ? new(actorId, null) : null).WithOperation(operation);
        return PlaygroundInteractionRules.Enrich(world, fact, operation);
    }

    internal static PlaygroundNavigationChange? ArrivalChange(PlaygroundBusinessState state, string actorId) {
        if (state.Expedition?.Navigation(actorId) is not { } active) { return null; }
        int completed = checked(active.CompletedSteps + 1);
        return new(actorId, completed == active.Path.Steps.Count ? null : new(active.Path, completed));
    }

    internal static void ValidateState(PlaygroundBusinessState state) {
        if (state.Expedition is not { } expedition) { return; }
        if (expedition.Navigations.Select(n => n.Path.ActorId).Distinct(StringComparer.Ordinal).Count() != expedition.Navigations.Count) {
            throw new InvalidOperationException("Each character has at most one active navigation.");
        }
        foreach (PlaygroundNavigation active in expedition.Navigations) {
            PlaygroundPathRules.ValidatePath(state, active.Path);
            CharacterContext? context = state.InteractionStates[active.Path.ActorId].CurrentContext;
            if (context is null || active.Path.ContextSequence > context.ContextSequence ||
                active.Path.ContextSequence == context.ContextSequence && active.Path.OperationSequence > context.LastOperationSequence) {
                throw new InvalidOperationException("An active route cannot precede its recorded path query.");
            }
            if (active.CompletedSteps < 0 || active.CompletedSteps >= active.Path.Steps.Count ||
                state.GetCharacter(active.Path.ActorId).Activity is not null || state.GetCharacter(active.Path.ActorId).FollowingLeaderId is not null) {
                throw new InvalidOperationException("Active navigation needs an unfinished path and an independent idle traveller.");
            }
            PlaygroundPathStep step = active.Path.Steps[active.CompletedSteps];
            SpatialLocation? location = PlaygroundActions.Location(state, active.Path.ActorId);
            bool matches = location is AtPlaceLocation at ? at.PlaceId.Value == step.FromPlaceId :
                location is TraversingLocation movement && movement.TargetPlaceId.Value == step.ToPlaceId &&
                    state.Definition.GetEntrances(step.FromPlaceId).Any(e => e.EntranceId == step.EntranceId && e.Id == movement.PassageId.Value);
            if (!matches) { throw new InvalidOperationException("Navigation progress must match its current spatial segment or waiting place."); }
        }
    }

    internal static void Validate(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundFact fact) {
        PlaygroundCommand? command = fact.RawInput is null ? null : PlaygroundCommandParser.Parse(fact.RawInput);
        PlaygroundNavigationChange? expected = null;
        if (fact.OperationDisposition == PlaygroundOperationDisposition.Succeeded && before.Expedition?.Navigation(fact.ActorId!) is not null) {
            if (command?.Kind == PlaygroundCommandKind.Cancel && fact.Kind != PlaygroundEventKind.NavigationCancelled) {
                throw new InvalidOperationException("Cancel with navigation must only clear the remaining route and preserve reminders.");
            }
            if (command?.Kind == PlaygroundCommandKind.Continue) {
                PlaygroundEventKind required = PlaygroundActions.Location(before, fact.ActorId!) is AtPlaceLocation
                    ? PlaygroundEventKind.MoveStarted : PlaygroundEventKind.AttentionAcknowledged;
                if (fact.Kind != required) { throw new InvalidOperationException("Navigation continue must match its place departure or travelling acknowledgement."); }
            }
        }
        if (fact.OperationDisposition == PlaygroundOperationDisposition.Succeeded && before.Expedition?.Navigation(fact.ActorId!) is not null &&
            command?.Kind is PlaygroundCommandKind.Go or PlaygroundCommandKind.Navigate or PlaygroundCommandKind.Wait or PlaygroundCommandKind.Gather or PlaygroundCommandKind.Produce or PlaygroundCommandKind.Follow) {
            throw new InvalidOperationException("Timed independent actions require cancelling navigation first.");
        }
        if (fact.Kind == PlaygroundEventKind.MoveStarted && command?.Kind == PlaygroundCommandKind.Navigate) {
            PlaygroundPath? path = before.Expedition?.CandidatePath(fact.ActorId!);
            if (path is null || path.Id != command.PathId || path.Steps.Count == 0 || before.Expedition?.Navigation(fact.ActorId!) is not null ||
                before.GetCharacter(fact.ActorId!).Activity is not null || before.GetCharacter(fact.ActorId!).FollowingLeaderId is not null ||
                PlaygroundActions.Location(before, fact.ActorId!) is not AtPlaceLocation start || start.PlaceId.Value != path.OriginPlaceId) {
                throw new InvalidOperationException("Navigation start must select the current personal candidate at its real origin.");
            }
            expected = new(fact.ActorId!, new(path, 0));
        } else if (fact.Kind == PlaygroundEventKind.MoveArrived && before.Expedition?.Navigation(fact.ActorId!) is { } active) {
            PlaygroundPathStep step = active.Path.Steps[active.CompletedSteps];
            if (PlaygroundActions.Location(before, fact.ActorId!) is not TraversingLocation movement || movement.TargetPlaceId.Value != step.ToPlaceId ||
                !before.Definition.GetEntrances(step.FromPlaceId).Any(e => e.EntranceId == step.EntranceId && e.Id == movement.PassageId.Value)) {
                throw new InvalidOperationException("Only a matching personal segment arrival can advance navigation.");
            }
            expected = ArrivalChange(before, fact.ActorId!);
        } else if (fact.Kind == PlaygroundEventKind.NavigationCancelled) {
            if (command != PlaygroundCommand.Cancel() || before.Expedition?.Navigation(fact.ActorId!) is null) { throw new InvalidOperationException("Navigation cancellation requires an active personal route."); }
            expected = new(fact.ActorId!, null);
        } else if (fact.Kind == PlaygroundEventKind.NavigationCompleted) {
            PlaygroundPath? path = before.Expedition?.CandidatePath(fact.ActorId!);
            if (command?.Kind != PlaygroundCommandKind.Navigate || path is null || path.Id != command.PathId || path.Steps.Count != 0 ||
                before.Expedition?.Navigation(fact.ActorId!) is not null || PlaygroundActions.Location(before, fact.ActorId!) is not AtPlaceLocation at || at.PlaceId.Value != path.OriginPlaceId ||
                before.GetCharacter(fact.ActorId!).Activity is not null || before.GetCharacter(fact.ActorId!).FollowingLeaderId is not null) {
                throw new InvalidOperationException("Zero-step navigation requires the real origin and creates no active plan.");
            }
        }
        if (expected?.ActorId != fact.NavigationChange?.ActorId || !PlaygroundNavigation.Same(expected?.Navigation, fact.NavigationChange?.Navigation)) {
            throw new InvalidOperationException("Recorded navigation replacement must match this personal start, cancellation or arrival exactly.");
        }
        foreach (PlaygroundCharacter actor in before.Characters) {
            PlaygroundNavigation? next = !after.GetCharacter(actor.Id).IsActive ? null : expected?.ActorId == actor.Id
                ? expected.Navigation : before.Expedition?.Navigation(actor.Id);
            if (!PlaygroundNavigation.Same(next, after.Expedition?.Navigation(actor.Id))) { throw new InvalidOperationException("An unrelated fact cannot alter navigation."); }
        }
    }
}
