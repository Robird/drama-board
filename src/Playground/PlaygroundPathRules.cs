using System.Globalization;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

internal static class PlaygroundPathRules {
    private static string Duration(long ticks) => (ticks / (decimal)(PlaygroundTime.Hour / 60)).ToString("0.###", CultureInfo.InvariantCulture) + " 分钟";
    internal static string Identity(string actorId, long context, long operation) =>
        $"path:{actorId}:{context.ToString(CultureInfo.InvariantCulture)}:{operation.ToString(CultureInfo.InvariantCulture)}";

    internal static string? Check(PlaygroundBusinessState state, string actorId, PlaygroundCommand command) {
        if (state.Expedition is null) { return "此场景未启用个人地图。"; }
        if (command.Hours != 0 || command.ResourceId is not null || command.Count != 0 || command.All ||
            command.ItemId is not null || command.EntranceId is not null || command.TargetActorId is not null ||
            command.SpeechText is not null || command.HandoffText is not null || command.Goal is not null ||
            command.RecipeId is not null || command.MoneyAmount != 0 || command.GiveAsset is not null || command.WantAsset is not null || command.TradeId is not null) {
            return "地图查询只接受声明的地点参数。";
        }
        if (command.Kind == PlaygroundCommandKind.Map) { return command.TargetPlaceId is null && command.OriginPlaceId is null ? null : "map 不接受地点参数。"; }
        IReadOnlyList<PlaygroundKnownPlace> map = PlaygroundKnownMap.Observe(state, actorId);
        if (!map.Any(p => p.Id == command.TargetPlaceId) || command.OriginPlaceId is not null && !map.Any(p => p.Id == command.OriginPlaceId)) {
            return "只能使用本人已知地图中的地点编号。";
        }
        if (command.OriginPlaceId is null && PlaygroundActions.Location(state, actorId) is not AtPlaceLocation) {
            return "正在途中；规划路径请用 from 指定本人已知的起点。";
        }
        return null;
    }

    internal static PlaygroundFact Plan(PlaygroundWorld world, string actorId, PlaygroundCommand command, PlaygroundControllerOperation operation) {
        PlaygroundPath? path = null;
        if (command.Kind == PlaygroundCommandKind.Path) {
            string origin = command.OriginPlaceId ?? ((AtPlaceLocation)PlaygroundActions.Location(world, actorId)!).PlaceId.Value;
            var search = PlaygroundPathSearch.Find(PlaygroundKnownMap.Observe(world.Business, actorId), origin, command.TargetPlaceId!);
            if (search.Overflow) { return PlaygroundInteractionRules.Reject(world, actorId, operation, "已知路线的总行程超过支持范围；原候选路径保持不变。"); }
            if (search.Steps is not null) {
                CharacterContext context = world.Business.InteractionStates[actorId].CurrentContext!;
                long sequence = checked(context.LastOperationSequence + 1);
                long total = 0;
                foreach (PlaygroundPathStep step in search.Steps) { total = checked(total + step.DurationTicks); }
                path = new(Identity(actorId, context.ContextSequence, sequence), actorId, context.ContextSequence, sequence,
                    origin, command.TargetPlaceId!, search.Steps, total);
            }
        }
        PlaygroundEventKind kind = command.Kind == PlaygroundCommandKind.Map ? PlaygroundEventKind.MapQueried : PlaygroundEventKind.PathQueried;
        var fact = new PlaygroundFact(kind, world.Time, actorId,
            changedCharacters: [world.GetCharacter(actorId).AdvanceDecisionGeneration()], lastServedActorId: actorId,
            result: Describe(world.Business, actorId, command, path), candidatePath: path).WithOperation(operation);
        // Queries do not run Observe, survival acknowledgement, or any resource refresh.
        return PlaygroundInteractionRules.Enrich(world, fact, operation);
    }

    internal static string Describe(PlaygroundBusinessState state, string actorId, PlaygroundCommand command, PlaygroundPath? path) {
        IReadOnlyList<PlaygroundKnownPlace> map = PlaygroundKnownMap.Observe(state, actorId);
        string Name(string id) => map.Single(p => p.Id == id).Name + "（" + id + "）";
        if (command.Kind == PlaygroundCommandKind.Map) {
            return "本人已知地图：\n" + string.Join("\n", map.Select(place => Name(place.Id) + "：\n" + string.Join("\n", place.Entrances.Select(e =>
                $"  {e.Id} {e.Description}；行程 {Duration(e.DurationTicks)}；" +
                (e.KnownDestinationId is null ? "去向未确认" : "已确认通向 " + Name(e.KnownDestinationId))))));
        }
        if (path is null) { return "在本人已知地图中未找到路径；已清空候选路径。"; }
        string heading = $"候选路径 {path.Id}：{Name(path.OriginPlaceId)} → {Name(path.TargetPlaceId)}；行程合计 {Duration(path.TotalTicks)}。";
        if (path.Steps.Count == 0) { return heading + "规划起终点相同，无需移动。"; }
        return heading + "\n" + string.Join("\n", path.Steps.Select(step =>
            $"  {Name(step.FromPlaceId)} — {step.EntranceId} → {Name(step.ToPlaceId)}（{Duration(step.DurationTicks)}）")) +
            "\n预计行程不含确认停留、补给或他人干预，不保证途中存活。";
    }

    internal static void ValidatePath(PlaygroundBusinessState state, PlaygroundPath path) {
        if (!state.Characters.Any(c => c.Id == path.ActorId && c.IsActive) || path.ContextSequence <= 0 || path.OperationSequence <= 0 ||
            path.Id != Identity(path.ActorId, path.ContextSequence, path.OperationSequence)) { throw new InvalidOperationException("Invalid personal path identity."); }
        IReadOnlyList<PlaygroundKnownPlace> map = PlaygroundKnownMap.Observe(state, path.ActorId);
        if (!map.Any(p => p.Id == path.OriginPlaceId) || !map.Any(p => p.Id == path.TargetPlaceId)) { throw new InvalidOperationException("Path endpoints must be personally known."); }
        if (path.OriginPlaceId == path.TargetPlaceId && (path.Steps.Count != 0 || path.TotalTicks != 0)) {
            throw new InvalidOperationException("An equal-origin path must be a zero-step path with zero cost.");
        }
        string current = path.OriginPlaceId;
        long cost = 0;
        foreach (PlaygroundPathStep step in path.Steps) {
            PlaygroundKnownEntrance? entrance = map.Single(p => p.Id == current).Entrances.SingleOrDefault(e => e.Id == step.EntranceId);
            if (step.FromPlaceId != current || entrance is null || entrance.KnownDestinationId != step.ToPlaceId || entrance.DurationTicks != step.DurationTicks) {
                throw new InvalidOperationException("A path step must be a continuous personally confirmed entrance.");
            }
            cost = checked(cost + step.DurationTicks); current = step.ToPlaceId;
        }
        if (current != path.TargetPlaceId || cost != path.TotalTicks) { throw new InvalidOperationException("Path endpoint or total cost differs from its recorded steps."); }
    }

    internal static void ValidateCandidates(PlaygroundBusinessState state) {
        if (state.Expedition is not { } expedition) { return; }
        if (expedition.CandidatePaths.Select(p => p.ActorId).Distinct(StringComparer.Ordinal).Count() != expedition.CandidatePaths.Count) {
            throw new InvalidOperationException("A character has at most one candidate path.");
        }
        foreach (PlaygroundPath path in expedition.CandidatePaths) {
            ValidatePath(state, path);
            CharacterContext? context = state.InteractionStates[path.ActorId].CurrentContext;
            if (context is null || path.ContextSequence > context.ContextSequence ||
                path.ContextSequence == context.ContextSequence && path.OperationSequence > context.LastOperationSequence) {
                throw new InvalidOperationException("A candidate cannot precede its causal query operation.");
            }
        }
    }

    internal static void Validate(PlaygroundBusinessState before, PlaygroundBusinessState after, PlaygroundFact fact) {
        bool query = fact.Kind is PlaygroundEventKind.MapQueried or PlaygroundEventKind.PathQueried;
        if (fact.Kind != PlaygroundEventKind.PathQueried && fact.CandidatePath is not null) { throw new InvalidOperationException("Only a path query can record a candidate."); }
        foreach (PlaygroundCharacter actor in before.Characters) {
            PlaygroundPath? expected = !after.GetCharacter(actor.Id).IsActive ? null : fact.Kind == PlaygroundEventKind.PathQueried && fact.ActorId == actor.Id
                ? fact.CandidatePath : before.Expedition?.CandidatePath(actor.Id);
            if (!PlaygroundPath.Same(expected, after.Expedition?.CandidatePath(actor.Id))) { throw new InvalidOperationException("Unauthorized candidate replacement."); }
        }
        if (!query) { return; }
        PlaygroundCommand command = PlaygroundCommandParser.Parse(fact.RawInput!)!;
        if (Check(before, fact.ActorId!, command) is not null || fact.At != before.Time || fact.ObservedEvents.Count != 0 || fact.Visits.Count != 0 || fact.ConfirmedPassages.Count != 0 ||
            fact.Result != Describe(before, fact.ActorId!, command, fact.CandidatePath)) { throw new InvalidOperationException("A map query must preserve its accepted inputs and private recorded answer."); }
        string? queryOrigin = command.Kind == PlaygroundCommandKind.Path
            ? command.OriginPlaceId ?? ((AtPlaceLocation)PlaygroundActions.Location(before, fact.ActorId!)!).PlaceId.Value : null;
        if (command.Kind == PlaygroundCommandKind.Path && queryOrigin == command.TargetPlaceId && fact.CandidatePath is null) {
            throw new InvalidOperationException("An equal-origin query must record its zero-step path with zero cost.");
        }
        if (fact.CandidatePath is { } path) {
            CharacterContext context = before.InteractionStates[fact.ActorId!].CurrentContext!;
            if (path.ActorId != fact.ActorId || path.ContextSequence != context.ContextSequence || path.OperationSequence != checked(context.LastOperationSequence + 1) ||
                path.OriginPlaceId != queryOrigin || path.TargetPlaceId != command.TargetPlaceId) { throw new InvalidOperationException("The path must belong to this query's actor, sequence and endpoints."); }
            ValidatePath(before, path); // Local recorded-value checks, never rerun the search during Fold.
        }
    }
}
