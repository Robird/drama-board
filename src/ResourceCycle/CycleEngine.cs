using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

public static class CycleEngine {
    public static SimulationKernel<CycleWorld, CyclePrediction, CycleFact> CreateKernel(
        IOccurrenceHistory<CycleWorld, CycleFact> history, PredictionMode mode = PredictionMode.Incremental,
        CycleMetrics? metrics = null, ICycleDecisionSource? decisionSource = null) {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        CycleMetrics measurements = metrics ?? new();
        return new(history, new SimulationRules(0, 1024), [new CycleRule(measurements, decisionSource)],
            Fold, (before, instant, facts, folded) => Complete(before, instant, facts, folded, mode, measurements), Validate);
    }
    public static CycleWorld Rebuild(CycleBusinessState business, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(business);
        business.Validate();
        // Fixed coarse subscriptions retain owners whose current output is absent. Child identity filters below
        // prevent attention-only changes from recalculating any activity or unrelated resource.
        var world = new CycleWorld(business, new([], CreateBindings(business)));
        world = new(business, world.Forecasts.WithPredictions(Owners(world).Select(owner => Predict(world, owner)).OfType<CyclePrediction>()));
        Validate(world, boundaryTime);
        return world;
    }
    public static CycleWorld Fold(CycleWorld world, LogicalInstant instant, CycleFact fact) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.At != instant.ModelTime || fact.At < world.Time) {
            throw new InvalidOperationException("A fact must apply at its recorded time and cannot move time backwards.");
        }
        ValidateFact(world, fact);
        return new(world.Business.Replace(fact.At, fact.Light, fact.ChangedResources, fact.Script,
            fact.ChangedCharacters, fact.LastServedActorId), world.Forecasts);
    }
    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts, CycleWorld folded) =>
        Complete(before, instant, facts, folded, PredictionMode.Incremental);

    /// <summary>Pure preparation: recorded business remains untouched; only affected runtime owners are recomputed.</summary>
    public static CycleWorld Complete(CycleWorld before, LogicalInstant instant, IReadOnlyList<CycleFact> facts,
        CycleWorld folded, PredictionMode mode, CycleMetrics? metrics = null) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(folded);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        CycleWorld intermediate = before;
        foreach (CycleFact fact in facts) {
            CycleWorld next = Fold(intermediate, instant, fact);
            if (mode == PredictionMode.Incremental) {
                foreach (CycleBinding binding in before.Forecasts.FindBindings(fact)) {
                    if (metrics is not null) { metrics.RoutedNotifications++; }
                    if (Affected(binding.OwnerId, intermediate, next)) {
                        if (metrics is not null) { metrics.Invalidations++; }
                        dirty.Add(binding.OwnerId);
                    }
                }
                dirty.Add(ConsumedOwner(fact));
            }
            intermediate = next;
        }
        if (!SameFoldedBusiness(intermediate.Business, folded.Business)) {
            throw new InvalidOperationException("Folded business does not match the recorded fact sequence.");
        }
        if (mode == PredictionMode.Full) { dirty.UnionWith(Owners(folded)); }
        var predictions = before.Forecasts.Predictions.ToDictionary(p => p.OwnerId, StringComparer.Ordinal);
        foreach (string owner in dirty.Order(StringComparer.Ordinal)) {
            metrics?.Count(owner, $"complete@{instant.ModelTime}");
            CyclePrediction? next = Predict(folded, owner);
            if (next is null) { predictions.Remove(owner); }
            else if (!predictions.TryGetValue(owner, out CyclePrediction? old) || old.ToString() != next.ToString()) { predictions[owner] = next; }
        }
        bool changed = predictions.Count != before.Forecasts.Predictions.Count ||
            before.Forecasts.Predictions.Any(p => !predictions.TryGetValue(p.OwnerId, out CyclePrediction? current) || !ReferenceEquals(p, current));
        CycleForecasts forecasts = changed ? before.Forecasts.WithPredictions(predictions.Values) : before.Forecasts;
        return ReferenceEquals(forecasts, folded.Forecasts) ? folded : new(folded.Business, forecasts);
    }
    private static bool Affected(string owner, CycleWorld before, CycleWorld after) {
        if (owner == "light") { return !ReferenceEquals(before.Light, after.Light); }
        if (owner == "script") { return !ReferenceEquals(before.Script, after.Script); }
        if (owner.StartsWith("resource:", StringComparison.Ordinal)) {
            string id = owner["resource:".Length..^":regen".Length];
            return !ReferenceEquals(before.GetResource(id), after.GetResource(id));
        }
        bool activity = owner.EndsWith(":activity", StringComparison.Ordinal);
        string actorId = owner["actor:".Length..^(activity ? ":activity".Length : ":attention".Length)];
        CycleCharacter previous = before.GetCharacter(actorId), next = after.GetCharacter(actorId);
        return activity ? !ReferenceEquals(previous.Activity, next.Activity) : !ReferenceEquals(previous, next);
    }
    private static IEnumerable<CycleBinding> CreateBindings(CycleBusinessState business) {
        yield return new("light", CycleEventKind.LightChanged, "*");
        yield return new("script", CycleEventKind.ResourceDestroyed, "*");
        foreach (CycleResource resource in business.Resources) {
            string owner = $"resource:{resource.Id}:regen";
            yield return new(owner, CycleEventKind.LightChanged, "*");
            foreach (CycleEventKind kind in new[] { CycleEventKind.ResourceRegenerated, CycleEventKind.ResourceDestroyed, CycleEventKind.GatherYielded }) {
                yield return new(owner, kind, resource.Id);
            }
        }
        foreach (CycleCharacter actor in business.Characters) {
            foreach (CycleEventKind kind in new[] { CycleEventKind.WaitStarted, CycleEventKind.WaitCompleted,
                CycleEventKind.GatherStarted, CycleEventKind.GatherYielded, CycleEventKind.GatherFinished, CycleEventKind.ActivityCancelled }) {
                yield return new($"actor:{actor.Id}:activity", kind, actor.Id);
            }
            foreach (CycleEventKind kind in new[] { CycleEventKind.WaitStarted, CycleEventKind.WaitCompleted,
                CycleEventKind.GatherFinished, CycleEventKind.ActivityCancelled, CycleEventKind.AttentionAcknowledged }) {
                yield return new($"actor:{actor.Id}:attention", kind, actor.Id);
            }
            foreach (CycleEventKind kind in new[] { CycleEventKind.GatherStarted, CycleEventKind.GatherYielded, CycleEventKind.ResourceDestroyed }) {
                yield return new($"actor:{actor.Id}:attention", kind, "*");
            }
        }
    }
    private static bool SameFoldedBusiness(CycleBusinessState expected, CycleBusinessState actual) =>
        expected.Time == actual.Time && ReferenceEquals(expected.Light, actual.Light) && ReferenceEquals(expected.Script, actual.Script) &&
        expected.LastServedActorId == actual.LastServedActorId && expected.Resources.Count == actual.Resources.Count &&
        expected.Resources.Zip(actual.Resources).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        expected.Characters.Count == actual.Characters.Count && expected.Characters.Zip(actual.Characters).All(pair => ReferenceEquals(pair.First, pair.Second));

    public static void Validate(CycleWorld world, ModelTime boundaryTime) {
        ArgumentNullException.ThrowIfNull(world);
        world.Business.Validate();
        if (world.Time != boundaryTime) { throw new InvalidOperationException("Business time differs from its publication boundary."); }
        var owners = Owners(world).ToHashSet(StringComparer.Ordinal);
        string? previous = null;
        foreach (CyclePrediction prediction in world.Forecasts.Predictions) {
            if (prediction.Due < boundaryTime || !owners.Contains(prediction.OwnerId) || !Enum.IsDefined(prediction.Kind) ||
                (previous is not null && StringComparer.Ordinal.Compare(previous, prediction.OwnerId) >= 0) || !ValidIdentity(world, prediction)) {
                throw new InvalidOperationException("Invalid prediction set.");
            }
            previous = prediction.OwnerId;
        }
    }
    private static bool ValidIdentity(CycleWorld world, CyclePrediction prediction) {
        switch (prediction.Kind) {
            case CyclePredictionKind.ChangeLight:
                return prediction.OwnerId == "light" && prediction.TargetId == "world" && prediction.Generation == 0 && prediction.Sequence == 0 && prediction.Due == world.Light.NextChange;
            case CyclePredictionKind.DestroyResource:
                return prediction.OwnerId == "script" && prediction.Generation == world.Script.NextIndex && prediction.Sequence == 0 &&
                    world.Script.NextIndex < world.Script.Entries.Count && world.Script.Entries[world.Script.NextIndex].ResourceId == prediction.TargetId &&
                    world.Script.Entries[world.Script.NextIndex].Due == prediction.Due;
            case CyclePredictionKind.RegenerateResource:
                CycleResource? resource = world.Resources.SingleOrDefault(r => r.Id == prediction.TargetId);
                return resource is not null && prediction.OwnerId == $"resource:{resource.Id}:regen" && prediction.Generation == resource.Generation &&
                    prediction.Sequence == 0 && resource.Stock < resource.Capacity && resource.ActiveSince is not null;
            case CyclePredictionKind.CompleteActivity:
                CycleCharacter? actor = world.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return actor?.Activity is { } activity && prediction.OwnerId == $"actor:{actor.Id}:activity" &&
                    prediction.Generation == activity.Id && prediction.Sequence == activity.CycleIndex && prediction.Due == activity.Due;
            case CyclePredictionKind.RequestDecision:
                CycleCharacter? deciding = world.Characters.SingleOrDefault(c => c.Id == prediction.TargetId);
                return deciding is not null && deciding.NeedsDecision && prediction.OwnerId == $"actor:{deciding.Id}:attention" &&
                    prediction.Generation == deciding.DecisionGeneration && prediction.Sequence == 0 && prediction.Due == world.Time;
            default: return false;
        }
    }
    private static string ConsumedOwner(CycleFact fact) => fact.Kind switch {
        CycleEventKind.LightChanged => "light",
        CycleEventKind.ResourceDestroyed => "script",
        CycleEventKind.ResourceRegenerated => $"resource:{fact.ResourceId}:regen",
        CycleEventKind.GatherYielded or CycleEventKind.GatherFinished or CycleEventKind.WaitCompleted => $"actor:{fact.ActorId}:activity",
        _ => $"actor:{fact.ActorId}:attention"
    };
    private static void ValidateFact(CycleWorld world, CycleFact fact) {
        if (!Enum.IsDefined(fact.Kind) ||
            fact.ChangedResources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedResources.Count ||
            fact.ChangedCharacters.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != fact.ChangedCharacters.Count ||
            fact.ChangedResources.Any(r => !world.Resources.Any(old => old.Id == r.Id)) ||
            fact.ChangedCharacters.Any(c => !world.Characters.Any(old => old.Id == c.Id)) ||
            (fact.ResourceId is not null && !world.Resources.Any(r => r.Id == fact.ResourceId)) ||
            (fact.ActorId is not null && !world.Characters.Any(c => c.Id == fact.ActorId))) {
            throw new InvalidOperationException("Fact contains unknown or duplicate identities.");
        }
        bool actorEffect = fact.ActorId is not null && fact.ChangedCharacters.Any(c => c.Id == fact.ActorId);
        bool oneResource = fact.ChangedResources.Count == 1 && fact.ChangedResources[0].Id == fact.ResourceId;
        bool decision = fact.Kind is CycleEventKind.WaitStarted or CycleEventKind.GatherStarted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged;
        if ((decision ? fact.LastServedActorId != fact.ActorId : fact.LastServedActorId is not null) ||
            (fact.LastServedActorId is not null && !actorEffect)) {
            throw new InvalidOperationException("Service cursor does not match a recorded decision.");
        }
        bool valid = fact.Kind switch {
            CycleEventKind.LightChanged => fact.Light is not null && fact.Script is null && fact.ActorId is null && fact.ResourceId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceRegenerated => oneResource && fact.Light is null && fact.Script is null && fact.ActorId is null && fact.ChangedCharacters.Count == 0,
            CycleEventKind.ResourceDestroyed => oneResource && fact.Light is null && fact.Script is not null && fact.ActorId is null,
            CycleEventKind.GatherYielded => actorEffect && oneResource && fact.Light is null && fact.Script is null,
            CycleEventKind.GatherStarted or CycleEventKind.GatherFinished => actorEffect && fact.ResourceId is not null && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null,
            CycleEventKind.WaitStarted or CycleEventKind.WaitCompleted or CycleEventKind.ActivityCancelled or CycleEventKind.AttentionAcknowledged =>
                actorEffect && fact.ChangedCharacters.Count == 1 && fact.ChangedResources.Count == 0 && fact.Light is null && fact.Script is null && fact.ResourceId is null,
            _ => false
        };
        if (!valid) { throw new InvalidOperationException("Recorded effects do not match the event shape."); }
    }
    internal static IEnumerable<string> Owners(CycleWorld world) => new[] { "light", "script" }
        .Concat(world.Resources.Select(r => $"resource:{r.Id}:regen"))
        .Concat(world.Characters.SelectMany(c => new[] { $"actor:{c.Id}:activity", $"actor:{c.Id}:attention" }));
    internal static CyclePrediction? Predict(CycleWorld world, string owner) {
        if (owner == "light") { return new(owner, CyclePredictionKind.ChangeLight, "world", world.Light.NextChange, 0); }
        if (owner == "script") {
            if (world.Script.NextIndex == world.Script.Entries.Count) { return null; }
            CycleDestruction entry = world.Script.Entries[world.Script.NextIndex];
            return new(owner, CyclePredictionKind.DestroyResource, entry.ResourceId, entry.Due, world.Script.NextIndex);
        }
        if (owner.StartsWith("resource:", StringComparison.Ordinal) && owner.EndsWith(":regen", StringComparison.Ordinal)) {
            CycleResource resource = world.GetResource(owner["resource:".Length..^":regen".Length]);
            if (resource.ActiveSince is not { } since || resource.Stock == resource.Capacity) { return null; }
            long remaining = resource.RegenerationTicks - resource.AccumulatedTicks;
            if (since.Ticks > long.MaxValue - remaining) { return null; }
            return new(owner, CyclePredictionKind.RegenerateResource, resource.Id, new(since.Ticks + remaining), resource.Generation);
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":activity", StringComparison.Ordinal)) {
            CycleCharacter actor = world.GetCharacter(owner["actor:".Length..^":activity".Length]);
            return actor.Activity is { } activity ? new(owner, CyclePredictionKind.CompleteActivity, actor.Id, activity.Due, activity.Id, activity.CycleIndex) : null;
        }
        if (owner.StartsWith("actor:", StringComparison.Ordinal) && owner.EndsWith(":attention", StringComparison.Ordinal)) {
            CycleCharacter actor = world.GetCharacter(owner["actor:".Length..^":attention".Length]);
            return actor.NeedsDecision ? new(owner, CyclePredictionKind.RequestDecision, actor.Id, world.Time, actor.DecisionGeneration) : null;
        }
        throw new InvalidOperationException("Unknown prediction owner.");
    }

    private sealed class CycleRule(CycleMetrics metrics, ICycleDecisionSource? decisionSource) : IOccurrenceRule<CycleWorld, CyclePrediction, CycleFact> {
        public IReadOnlyList<OccurrenceCandidate<CyclePrediction>> Forecast(CycleWorld world, SimulationRules rules) {
            if (world.Forecasts.Predictions.Count == 0) { return []; }
            ModelTime due = world.Forecasts.Predictions.Min(p => p.Due);
            CyclePredictionKind kind = world.Forecasts.Predictions.Where(p => p.Due == due).Min(p => p.Kind);
            CyclePrediction[] eligible = world.Forecasts.Predictions.Where(p => p.Due == due && p.Kind == kind).ToArray();
            if (kind == CyclePredictionKind.RequestDecision) {
                int last = world.Characters.ToList().FindIndex(c => c.Id == world.Business.LastServedActorId);
                for (int offset = 1; offset <= world.Characters.Count; offset++) {
                    string id = world.Characters[(last + offset) % world.Characters.Count].Id;
                    CyclePrediction? next = eligible.SingleOrDefault(p => p.TargetId == id);
                    if (next is not null) { return [Candidate(next)]; }
                }
                throw new InvalidOperationException("No character owns an eligible decision request.");
            }
            return eligible.Select(Candidate).ToArray();
        }
        public async ValueTask<TransitionDraft<CycleFact>> PlanSelectedAsync(CycleWorld world,
            OccurrenceCandidate<CyclePrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            CyclePrediction prediction = winner.Data;
            metrics.PredictionChecks++;
            if (winner.Key != new CandidateKey(prediction.ToString()) || winner.Due.ModelTime != prediction.Due || prediction.Due < world.Time ||
                !world.Forecasts.Predictions.Any(p => p.ToString() == prediction.ToString()) || Predict(world, prediction.OwnerId)?.ToString() != prediction.ToString()) {
                throw new InvalidOperationException("A stale or invalid prediction cannot execute.");
            }
            CycleFact fact;
            if (prediction.Kind == CyclePredictionKind.RequestDecision) {
                if (decisionSource is null) { throw new InvalidOperationException("A playable world requires an ICycleDecisionSource."); }
                CycleCommand command = await decisionSource.ChooseAsync(world, prediction.TargetId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                string? rejection = CycleActions.Check(world, prediction.TargetId, command);
                if (rejection is not null) { throw new InvalidOperationException(rejection); }
                fact = CyclePlanning.Decision(world, prediction.TargetId, command);
            } else { fact = CyclePlanning.Occurrence(world, prediction); }
            return new([fact]);
        }
        private static OccurrenceCandidate<CyclePrediction> Candidate(CyclePrediction prediction) =>
            new(new CandidateKey(prediction.ToString()), new CandidateDue(prediction.Due), prediction);
    }
}
