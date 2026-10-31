using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public enum TownPredictionKind { Failure, MarketRefresh, Spatial, Activity, End, Decision }
public sealed record TownPrediction(TownPredictionKind Kind, ModelTime Due, string? ActorId = null,
    OccurrenceCandidate<SpatialOccurrenceData>? SpatialCandidate = null);

/// <summary>One Kernel owns all town and spatial occurrences. Controllers run only after an open-decision State has completed.</summary>
public static class TownEngine {
    public static SimulationKernel<TownWorld, TownPrediction, TownFact> CreateKernel(
        IOccurrenceHistory<TownWorld, TownFact> history,
        Func<TownDecision, CancellationToken, ValueTask<TownCommand>> controller) =>
        new(history, new SimulationRules(0, 1024), [new TownRule(controller)], Fold, Complete, Validate);

    public static TownWorld Rebuild(TownBusinessState business) {
        ArgumentNullException.ThrowIfNull(business);
        var world = new TownWorld(business, TownContent.Simulation.Rebuild(business.Spatial, business.Time));
        Validate(world, business.Time);
        return world;
    }

    public static TownWorld Fold(TownWorld world, LogicalInstant instant, TownFact fact) {
        if (fact.PreviousGeneration != world.Business.Generation || fact.At != instant.ModelTime ||
            fact.NextBusiness.Generation <= fact.PreviousGeneration) {
            throw new InvalidOperationException("Town fact does not continue this business boundary.");
        }
        return new(fact.NextBusiness, new(fact.NextBusiness.Spatial, world.Spatial.Contacts));
    }

    public static TownWorld Complete(TownWorld before, LogicalInstant instant, IReadOnlyList<TownFact> facts, TownWorld folded) {
        GraphSpatialFact[] spatial = facts.SelectMany(fact => fact.SpatialFacts).ToArray();
        SpatialSimulationState prepared = TownContent.Simulation.Complete(before.Spatial, instant, spatial, folded.Spatial);
        return new(folded.Business, prepared);
    }

    public static void Validate(TownWorld world, ModelTime at) {
        world.Business.Validate();
        if (world.Time != at || !ReferenceEquals(world.Business.Spatial, world.Spatial.Business)) {
            throw new InvalidOperationException("Town runtime and publication boundary must share business state and time.");
        }
        TownContent.Simulation.Validate(world.Spatial, at);
    }

    public static string? NextDecisionActor(TownWorld world) {
        TownBusinessState business = world.Business;
        if (business.Ended) { return null; }
        if (business.OpenDecisionActorId is { } open) { return open; }
        int last = business.Characters.ToList().FindIndex(actor => actor.Id == business.LastServedActorId);
        for (int offset = 1; offset <= business.Characters.Count; offset++) {
            TownCharacter actor = business.Characters[(last + offset) % business.Characters.Count];
            if (!actor.Failed && actor.Activity is null && business.Place(actor.Id) is not null) { return actor.Id; }
        }
        return null;
    }

    private sealed class TownRule(Func<TownDecision, CancellationToken, ValueTask<TownCommand>> controller)
        : IOccurrenceRule<TownWorld, TownPrediction, TownFact> {
        private readonly SpatialOccurrenceRule _spatial = new(TownContent.Graph);
        private readonly SpatialContactOccurrenceRule _contacts = new(TownContent.Graph);
        public IReadOnlyList<OccurrenceCandidate<TownPrediction>> Forecast(TownWorld world, SimulationRules rules) {
            TownBusinessState business = world.Business;
            if (business.Ended) { return []; }
            var predictions = new List<TownPrediction> {
                new(TownPredictionKind.End, business.EndTime),
                new(TownPredictionKind.MarketRefresh, business.Market.NextRefresh)
            };
            foreach (TownCharacter actor in business.Characters.Where(actor => !actor.Failed)) {
                long remaining = checked((actor.FoodReserve + TownContent.FoodDecayPerHour - 1) / TownContent.FoodDecayPerHour);
                predictions.Add(new(TownPredictionKind.Failure, new(checked(business.Time.Ticks + remaining)), actor.Id));
                if (actor.Activity is { } activity) { predictions.Add(new(TownPredictionKind.Activity, activity.Due, actor.Id)); }
            }
            predictions.AddRange(_spatial.Forecast(world.Spatial, rules).Concat(_contacts.Forecast(world.Spatial, rules)).Select(candidate =>
                new TownPrediction(TownPredictionKind.Spatial, candidate.Due.ModelTime, SpatialCandidate: candidate)));
            if (NextDecisionActor(world) is { } deciding) { predictions.Add(new(TownPredictionKind.Decision, business.Time, deciding)); }
            ModelTime due = predictions.Min(prediction => prediction.Due);
            TownPredictionKind kind = predictions.Where(prediction => prediction.Due == due).Min(prediction => prediction.Kind);
            return predictions.Where(prediction => prediction.Due == due && prediction.Kind == kind)
                .Select(prediction => new OccurrenceCandidate<TownPrediction>(
                    new CandidateKey($"town:{business.Generation}:{prediction.Kind}:{prediction.ActorId}:{prediction.SpatialCandidate?.Key}"),
                    new(prediction.Due), prediction)).ToArray();
        }

        public async ValueTask<TransitionDraft<TownFact>> PlanSelectedAsync(TownWorld world,
            OccurrenceCandidate<TownPrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            TownBusinessState before = world.Business;
            TownPrediction prediction = winner.Data;
            if (prediction.Kind == TownPredictionKind.Decision) {
                string id = prediction.ActorId!;
                if (before.OpenDecisionActorId is null) {
                    TownCharacter actor = before.Character(id);
                    TownCharacter opened = actor with { DecisionSequence = checked(actor.DecisionSequence + 1) };
                    return new([new(TownEventKind.DecisionOpened, before,
                        TownPlanning.Replace(before, opened, id), id, "Decision boundary opened.")]);
                }
                TownCommand command = await controller(new(world, id), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A controller must return a command.");
                cancellationToken.ThrowIfCancellationRequested();
                return new([TownPlanning.Plan(world, id, command)]);
            }

            TownBusinessState at = TownPlanning.At(before, prediction.Due);
            TownFact fact;
            switch (prediction.Kind) {
                case TownPredictionKind.End:
                    fact = new(TownEventKind.Ended, before, at.Change(ended: true), result: "Finite experiment ended; work in progress remains accounted for.");
                    break;
                case TownPredictionKind.MarketRefresh:
                    fact = new(TownEventKind.MarketRefreshed, before, at.Change(market: TownMarket.Fresh(
                        new(checked(prediction.Due.Ticks + TownContent.Day)), checked(at.Market.Cycle + 1))), result: "External stock and purchase quotas refreshed.");
                    break;
                case TownPredictionKind.Failure: {
                    TownCharacter actor = at.Character(prediction.ActorId!);
                    TownCharacter failed = actor with {
                        Failed = true, FoodReserve = 0, MinimumFoodReserve = 0, Activity = null,
                        Inventory = actor.Activity?.InputItem is { } input ? actor.Inventory.Add(input, actor.Activity.InputCount) : actor.Inventory
                    };
                    GraphSpatialFact[] spatialFacts = [new EntityRemovedFact(new(actor.Id))];
                    GraphSpatialState spatial = TownPlanning.ApplySpatial(at.Spatial, prediction.Due, spatialFacts);
                    fact = new(TownEventKind.Failed, before, at.Change(at.Characters.Select(current => current.Id == actor.Id ? failed : current), spatial: spatial),
                        actor.Id, "Food exhausted; unused production inputs remain in the resident's estate.", spatialFacts: spatialFacts);
                    break;
                }
                case TownPredictionKind.Activity: {
                    TownCharacter actor = at.Character(prediction.ActorId!);
                    TownActivity activity = actor.Activity!;
                    TownCharacter changed = actor with { Activity = null };
                    TownEconomicTotals economy = at.Economy;
                    bool produced = activity.Kind == TownActivityKind.Produce;
                    if (produced) {
                        TownRecipe recipe = TownContent.Recipe(activity.RecipeId!)!;
                        changed = changed with { Inventory = actor.Inventory.Add(recipe.Output, checked(recipe.OutputCount * activity.Batches)) };
                        economy = economy.Produce(recipe, activity.Batches);
                    }
                    fact = new(produced ? TownEventKind.ProductionCompleted : TownEventKind.WaitCompleted, before,
                        at.Change(at.Characters.Select(current => current.Id == actor.Id ? changed : current), economy: economy), actor.Id,
                        produced ? "Production completed." : "Waiting completed.");
                    break;
                }
                case TownPredictionKind.Spatial: {
                    bool contact = prediction.SpatialCandidate!.Data is PassageContactOccurrenceData;
                    TransitionDraft<GraphSpatialFact> draft = contact
                        ? await _contacts.PlanSelectedAsync(world.Spatial, prediction.SpatialCandidate, cancellationToken).ConfigureAwait(false)
                        : await _spatial.PlanSelectedAsync(world.Spatial, prediction.SpatialCandidate, cancellationToken).ConfigureAwait(false);
                    GraphSpatialState spatial = TownPlanning.ApplySpatial(at.Spatial, prediction.Due, draft.Facts);
                    string? actor = draft.Facts.OfType<TraversalArrivedFact>().FirstOrDefault()?.EntityId.Value;
                    fact = new(contact ? TownEventKind.ContactOccurred : TownEventKind.Arrived, before, at.Change(spatial: spatial), actor,
                        contact ? "Passage contact consumed; residents continue their journeys." : "Movement completed.", spatialFacts: draft.Facts);
                    break;
                }
                default: throw new InvalidOperationException("Unknown prediction.");
            }
            return new([fact]);
        }
    }
}
