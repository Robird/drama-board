using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Microtown;

public enum TownPredictionKind { Failure, TradeExpiry, MarketRefresh, Spatial, Activity, End, Decision }
public sealed record TownPrediction(TownPredictionKind Kind, ModelTime Due, string? ActorId = null,
    OccurrenceCandidate<SpatialOccurrenceData>? SpatialCandidate = null, string? TradeId = null);

/// <summary>One Kernel owns all town and spatial occurrences. Controllers run only after an open-decision State has completed.</summary>
public static partial class TownEngine {
    public static SimulationKernel<TownWorld, TownPrediction, TownFact> CreateKernel(
        IOccurrenceHistory<TownWorld, TownFact> history,
        Func<TownDecision, CancellationToken, ValueTask<TownCommand>> controller) {
        ArgumentNullException.ThrowIfNull(controller);
        return CreateCore(history, async (world, actorId, cancellationToken) => {
            TownCommand command = await controller(new(world, actorId), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A script must return a command.");
            cancellationToken.ThrowIfCancellationRequested();
            return TownPlanning.Plan(world, actorId, command);
        });
    }

    /// <summary>Production controller seam: only detached actor-visible material is supplied, and raw output is captured before parsing.</summary>
    public static SimulationKernel<TownWorld, TownPrediction, TownFact> CreateControlledKernel(
        IOccurrenceHistory<TownWorld, TownFact> history,
        Func<TownObservation, CancellationToken, ValueTask<TownControllerOperation>> controller) {
        ArgumentNullException.ThrowIfNull(controller);
        return CreateCore(history, async (world, actorId, cancellationToken) => {
            TownObservation observation = TownObservation.Capture(world, actorId);
            TownControllerOperation operation = await controller(observation, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A controller must return captured operation provenance.");
            cancellationToken.ThrowIfCancellationRequested();
            return TownPlanning.Plan(world, actorId, operation);
        });
    }

    private static SimulationKernel<TownWorld, TownPrediction, TownFact> CreateCore(
        IOccurrenceHistory<TownWorld, TownFact> history,
        Func<TownWorld, string, CancellationToken, ValueTask<TownFact>> planDecision) =>
        new(history, new SimulationRules(0, 1024), [new TownRule(planDecision)], Fold, Complete, Validate);

    public static TownWorld Rebuild(TownBusinessState business) {
        ArgumentNullException.ThrowIfNull(business);
        business.Validate();
        var world = new TownWorld(business, TownContent.Simulation.Rebuild(business.Spatial, business.Time), TownPredictionMaintenance.Rebuild(business));
        Validate(world, business.Time);
        return world;
    }

    public static TownWorld Fold(TownWorld world, LogicalInstant instant, TownFact fact) {
        if (fact.PreviousGeneration != world.Business.Generation || fact.At != instant.ModelTime ||
            fact.NextBusiness.Generation <= fact.PreviousGeneration) {
            throw new InvalidOperationException("Town fact does not continue this business boundary.");
        }
        if (fact.OperationReceipt is { } receipt) {
            receipt.Validate();
            TownCharacter actor = fact.NextBusiness.Character(receipt.ActorId);
            if (fact.Kind != TownEventKind.Operation || fact.ActorId != receipt.ActorId || fact.Accepted != receipt.Accepted ||
                fact.Result != receipt.Result || fact.At != receipt.At || actor.DecisionSequence != receipt.DecisionSequence ||
                actor.OperationSequence != receipt.OperationSequence || actor.LastOperationReceipt != receipt ||
                (fact.GoalReceipt is null ? receipt.GoalReceipt is not null : !fact.GoalReceipt.SameAs(receipt.GoalReceipt))) {
                throw new InvalidOperationException("Town operation fact and resident receipt disagree.");
            }
        }
        return new(fact.NextBusiness, new(fact.NextBusiness.Spatial, world.Spatial.Contacts), world.Forecasts);
    }

    public static TownWorld Complete(TownWorld before, LogicalInstant instant, IReadOnlyList<TownFact> facts, TownWorld folded) {
        GraphSpatialFact[] spatial = facts.SelectMany(fact => fact.SpatialFacts).ToArray();
        SpatialSimulationState prepared = TownContent.Simulation.Complete(before.Spatial, instant, spatial, folded.Spatial);
        return new(folded.Business, prepared, TownPredictionMaintenance.Complete(before.Business, folded.Business, before.Forecasts));
    }

    public static void Validate(TownWorld world, ModelTime at) {
        world.Business.Validate();
        if (world.Time != at || !ReferenceEquals(world.Business.Spatial, world.Spatial.Business)) {
            throw new InvalidOperationException("Town runtime and publication boundary must share business state and time.");
        }
        TownContent.Simulation.Validate(world.Spatial, at);
        world.Forecasts.Validate(world.Business);
    }

    public static string? NextDecisionActor(TownWorld world) => NextDecisionActor(world.Business);

    internal static string? NextDecisionActor(TownBusinessState business) {
        if (business.Ended) { return null; }
        if (business.OpenDecisionActorId is { } open) { return open; }
        int last = business.Characters.ToList().FindIndex(actor => actor.Id == business.LastServedActorId);
        for (int offset = 1; offset <= business.Characters.Count; offset++) {
            TownCharacter actor = business.Characters[(last + offset) % business.Characters.Count];
            if (!actor.Failed && (actor.Activity is null || actor.Interaction.NeedsResponse) && business.Place(actor.Id) is not null) { return actor.Id; }
        }
        return null;
    }

    private sealed class TownRule(Func<TownWorld, string, CancellationToken, ValueTask<TownFact>> planDecision)
        : IOccurrenceRule<TownWorld, TownPrediction, TownFact> {
        private readonly SpatialOccurrenceRule _spatial = new(TownContent.Graph);
        private readonly SpatialContactOccurrenceRule _contacts = new(TownContent.Graph);
        public IReadOnlyList<OccurrenceCandidate<TownPrediction>> Forecast(TownWorld world, SimulationRules rules) => TownEngine.Forecast(world, rules);

        public async ValueTask<TransitionDraft<TownFact>> PlanSelectedAsync(TownWorld world,
            OccurrenceCandidate<TownPrediction> winner, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            TownBusinessState before = world.Business;
            TownPrediction prediction = winner.Data;
            if (prediction.Kind == TownPredictionKind.Decision) {
                string id = prediction.ActorId!;
                if (before.OpenDecisionActorId is null) {
                    TownCharacter actor = before.Character(id);
                    TownCharacter opened = actor with { DecisionSequence = checked(actor.DecisionSequence + 1), Interaction = actor.Interaction.OpenDecision() };
                    TownTradeState trades = before.Trades.OpenDecision(id, opened.DecisionSequence,
                        before.Characters.Where(resident => resident.Failed).Select(resident => resident.Id).ToArray());
                    return new([new(TownEventKind.DecisionOpened, before,
                        before.Change(before.Characters.Select(current => current.Id == id ? opened : current), openDecision: id, trades: trades),
                        id, "Decision boundary opened.")]);
                }
                return new([await planDecision(world, id, cancellationToken).ConfigureAwait(false)]);
            }

            TownBusinessState at = TownPlanning.At(before, prediction.Due);
            TownFact fact;
            switch (prediction.Kind) {
                case TownPredictionKind.End:
                    fact = new(TownEventKind.Ended, before, TownTradeRules.CloseForEnd(at.Change(ended: true)), result: "Finite experiment ended; work in progress remains accounted for and pending offers are closed.");
                    break;
                case TownPredictionKind.TradeExpiry:
                    fact = new(TownEventKind.TradeExpired, before, TownTradeRules.Expire(at, prediction.TradeId!),
                        result: $"Offer {prediction.TradeId} expired without transferring assets.");
                    break;
                case TownPredictionKind.MarketRefresh:
                    fact = new(TownEventKind.MarketRefreshed, before, at.Change(market: TownMarket.Fresh(
                        new(checked(prediction.Due.Ticks + TownContent.Day)), checked(at.Market.Cycle + 1))), result: "External stock and purchase quotas refreshed.");
                    break;
                case TownPredictionKind.Failure: {
                    TownCharacter actor = at.Character(prediction.ActorId!);
                    TownCharacter failed = actor with {
                        Failed = true, FoodReserve = 0, MinimumFoodReserve = 0, Activity = null,
                        Interaction = actor.Interaction.ClearResponse(),
                        LastExperience = new(TownEventKind.Failed, prediction.Due, "Food exhausted; unused production inputs remain in your estate."),
                        Inventory = TownPlanning.ReturnActivityInputs(actor)
                    };
                    GraphSpatialFact[] spatialFacts = [new EntityRemovedFact(new(actor.Id))];
                    GraphSpatialState spatial = TownPlanning.ApplySpatial(at.Spatial, prediction.Due, spatialFacts);
                    TownBusinessState failedState = TownTradeRules.CloseForFailure(
                        at.Change(at.Characters.Select(current => current.Id == actor.Id ? failed : current), spatial: spatial), actor.Id);
                    fact = new(TownEventKind.Failed, before, failedState,
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
                    string outcome = produced ? $"Produced {TownContent.Recipe(activity.RecipeId!)!.OutputCount * activity.Batches} {TownContent.Recipe(activity.RecipeId!)!.Output}." : "Waiting completed.";
                    changed = changed with { LastExperience = new(produced ? TownEventKind.ProductionCompleted : TownEventKind.WaitCompleted, prediction.Due, outcome) };
                    fact = new(produced ? TownEventKind.ProductionCompleted : TownEventKind.WaitCompleted, before,
                        at.Change(at.Characters.Select(current => current.Id == actor.Id ? changed : current), economy: economy), actor.Id,
                        outcome);
                    break;
                }
                case TownPredictionKind.Spatial: {
                    bool contact = prediction.SpatialCandidate!.Data is PassageContactOccurrenceData;
                    TransitionDraft<GraphSpatialFact> draft = contact
                        ? await _contacts.PlanSelectedAsync(world.Spatial, prediction.SpatialCandidate, cancellationToken).ConfigureAwait(false)
                        : await _spatial.PlanSelectedAsync(world.Spatial, prediction.SpatialCandidate, cancellationToken).ConfigureAwait(false);
                    GraphSpatialState spatial = TownPlanning.ApplySpatial(at.Spatial, prediction.Due, draft.Facts);
                    string? actor = draft.Facts.OfType<TraversalArrivedFact>().FirstOrDefault()?.EntityId.Value;
                    IEnumerable<TownCharacter> characters = at.Characters;
                    if (actor is not null) {
                        string destination = ((AtPlaceLocation)spatial.Entities.Single(entity => entity.Id.Value == actor).Location).PlaceId.Value;
                        characters = characters.Select(current => current.Id == actor ? current with {
                            LastExperience = new(TownEventKind.Arrived, prediction.Due, $"Arrived at {destination}.")
                        } : current);
                    }
                    fact = new(contact ? TownEventKind.ContactOccurred : TownEventKind.Arrived, before, at.Change(characters, spatial: spatial), actor,
                        contact ? "Passage contact consumed; residents continue their journeys." : "Movement completed.", spatialFacts: draft.Facts);
                    break;
                }
                default: throw new InvalidOperationException("Unknown prediction.");
            }
            return new([fact]);
        }
    }
}
