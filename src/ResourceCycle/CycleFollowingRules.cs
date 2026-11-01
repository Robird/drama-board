using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>Game-local direct following. The character relation is authoritative; membership is derived.</summary>
internal static class CycleFollowingRules {
    internal static IEnumerable<CycleCharacter> Followers(CycleBusinessState business, string leaderId) =>
        business.Characters.Where(c => c.FollowingLeaderId == leaderId);

    internal static IEnumerable<CycleCharacter> MovingMembers(CycleBusinessState business, string leaderId) =>
        business.Characters.Where(c => c.Id == leaderId || c.FollowingLeaderId == leaderId);

    internal static string? Check(CycleWorld world, CycleCharacter actor, CycleCommand command) {
        if (CycleActions.CurrentPlace(world, actor.Id) is not { } place) { return "跟随关系只能在地点内改变。"; }
        if (command.Kind == CycleCommandKind.Follow) {
            if (actor.Activity is not null) { return "已有耗时活动，请先完成或 cancel 再跟随。"; }
            if (actor.FollowingLeaderId is not null) { return "已经跟随领队；转跟前请先 unfollow。"; }
            if (Followers(world.Business, actor.Id).Any()) { return "已有队员，请先 dismiss-followers 再跟随。"; }
            CycleCharacter? leader = world.Characters.SingleOrDefault(c => c.Id == command.TargetActorId);
            if (leader is null || leader.Id == actor.Id || !leader.IsActive || leader.FollowingLeaderId is not null ||
                CycleActions.CurrentPlace(world, leader.Id) != place) {
                return "当前无法跟随这个角色；需要同地点、活跃且没有上级的其他角色。";
            }
            return null;
        }
        if (command.Kind == CycleCommandKind.Unfollow) {
            return actor.FollowingLeaderId is null ? "当前没有跟随领队。" : null;
        }
        CycleCharacter[] followers = Followers(world.Business, actor.Id).ToArray();
        if (followers.Length == 0) { return "当前没有直属队员。"; }
        return followers.Any(c => c.DecisionGeneration >= long.MaxValue - 2)
            ? "队员请求版本已达到支持上限。" : null;
    }

    internal static CycleFact Plan(CycleWorld world, CycleCharacter actor, CycleCommand command) {
        if (command.Kind == CycleCommandKind.Follow) {
            string result = $"开始跟随 {world.GetCharacter(command.TargetActorId!).Name}；独立耗时行动需先 unfollow。";
            return new(CycleEventKind.FollowStarted, world.Time, actor.Id,
                changedCharacters: [actor.WithFollowing(command.TargetActorId, acknowledge: true, result: result)],
                lastServedActorId: actor.Id, result: result, targetActorId: command.TargetActorId);
        }
        if (command.Kind == CycleCommandKind.Unfollow) {
            const string result = "已结束跟随，可以独立行动。";
            return new(CycleEventKind.FollowEnded, world.Time, actor.Id,
                changedCharacters: [actor.WithFollowing(null, result: result)], lastServedActorId: actor.Id, result: result);
        }
        const string dismissed = "已解散全部直属队员，各自恢复独立行动。";
        return new(CycleEventKind.FollowersDismissed, world.Time, actor.Id,
            changedCharacters: MovingMembers(world.Business, actor.Id).Select(c => c.Id == actor.Id
                ? c.Change(c.Activity, result: dismissed) : c.WithFollowing(null)), lastServedActorId: actor.Id, result: dismissed);
    }

    /// <summary>Record relation cleanup in the same terminal fact, retaining survivors' existing journeys.</summary>
    internal static CycleFact CleanupEndings(CycleWorld world, CycleFact fact) {
        var ended = fact.ChangedCharacters.Where(c => !c.IsActive).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ended.Count == 0) { return fact; }
        var changes = fact.ChangedCharacters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (CycleCharacter before in world.Characters) {
            CycleCharacter after = changes.GetValueOrDefault(before.Id, before);
            if (after.FollowingLeaderId is { } leader && ended.Contains(leader)) {
                changes[after.Id] = after.WithFollowing(null);
            }
        }
        return fact.WithCharacters(world.Characters.Where(c => changes.ContainsKey(c.Id)).Select(c => changes[c.Id]));
    }

    internal static void Validate(CycleBusinessState business) {
        foreach (CycleCharacter follower in business.Characters.Where(c => c.FollowingLeaderId is not null)) {
            CycleCharacter? leader = business.Characters.SingleOrDefault(c => c.Id == follower.FollowingLeaderId);
            if (business.Expedition is null || !follower.IsActive || follower.Activity is not null || leader is null ||
                leader.Id == follower.Id || !leader.IsActive || leader.FollowingLeaderId is not null || Followers(business, follower.Id).Any()) {
                throw new InvalidOperationException("Following must be a direct relation between active expedition participants without follower activities.");
            }
        }
        foreach (CycleCharacter leader in business.Characters.Where(c => Followers(business, c.Id).Any())) {
            SpatialLocation[] locations = MovingMembers(business, leader.Id)
                .Select(c => CycleActions.Location(business, c.Id) ?? throw new InvalidOperationException("Following requires actual spatial positions.")).ToArray();
            TraversingLocation? journey = locations.OfType<TraversingLocation>().FirstOrDefault();
            if (journey is null) {
                if (locations.Any(location => location is not AtPlaceLocation) ||
                    locations.Cast<AtPlaceLocation>().Select(at => at.PlaceId).Distinct().Count() != 1) {
                    throw new InvalidOperationException("Following members at rest must share one place.");
                }
                continue;
            }
            if (locations.Any(location => location switch {
                TraversingLocation travel => !journey.Equals(travel),
                AtPlaceLocation at => journey.ArrivalDue != business.Time || at.PlaceId != journey.TargetPlaceId,
                _ => true
            })) {
                throw new InvalidOperationException("Following journeys must match; mixed arrival positions are legal only at their shared arrival boundary.");
            }
        }
    }
}
