using System.Globalization;
using System.Text;
using DramaBoard.Goals;

namespace DramaBoard.Microtown;

/// <summary>Renders only the supplied frozen observation. It neither queries nor advances a world.</summary>
public static class TownTextRenderer {
    public static string Render(TownObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);
        var text = new StringBuilder();
        text.AppendLine($"# Microtown — {Data(observation.Name)} ({observation.ActorId})");
        text.AppendLine($"Observed at hour {Hours(observation.Time.Ticks)}; experiment ends at hour {Hours(observation.EndTime.Ticks)}. Decision {observation.DecisionSequence}; operation {observation.OperationSequence}.");
        text.AppendLine(observation.WorldEnded ? "The finite experiment has ended. No further actions are accepted."
            : observation.Failed ? "Your food was exhausted. This resident can no longer act."
            : observation.IsDecisionOpen ? "The world is paused for your decision. Choose one command; legality below is for this observation."
            : "State preview; this is not an open command prompt.");
        text.AppendLine();
        text.AppendLine("## You");
        text.AppendLine($"Money: {observation.Money}. Food: {Hours(observation.FoodReserve)}/100; personal minimum: {Hours(observation.MinimumFoodReserve)}.");
        if (!observation.Failed && !observation.WorldEnded) {
            long starvationTicks = (observation.FoodReserve + TownContent.FoodDecayPerHour - 1) / TownContent.FoodDecayPerHour;
            text.AppendLine($"Without eating, food exhaustion is due at hour {Hours(checked(observation.Time.Ticks + starvationTicks))} (about {Hours(starvationTicks)} hours from now).");
        }
        text.AppendLine(observation.PlaceId is { } place ? $"Location: {place}."
            : observation.PassageId is { } passage ? $"Travelling on {passage} toward {observation.Destination}; arrival at hour {Hours(observation.ArrivalDue!.Value.Ticks)}."
            : "No active location.");
        if (observation.Activity is { } activity) {
            text.AppendLine($"Activity: {activity.Kind}; due at hour {Hours(activity.Due.Ticks)}.");
            if (activity.RecipeId is { } recipe) {
                text.AppendLine($"Recipe {recipe}, {activity.Batches} batches; {activity.InputItem} x{activity.InputCount} is reserved as work in progress, outside your inventory.");
            }
        } else if (observation.PassageId is null) { text.AppendLine("Activity: none."); }
        text.AppendLine($"Inventory ({observation.Inventory.TotalCount} units; capacity {(observation.Inventory.UnitCapacity is { } capacity ? capacity.ToString(CultureInfo.InvariantCulture) : "unlimited")}):");
        foreach (var item in observation.Inventory.Items.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { text.AppendLine($"- {item.Key}: {item.Value}"); }
        if (observation.Inventory.TotalCount == 0) { text.AppendLine("- Empty."); }
        text.AppendLine();

        text.AppendLine("## Your latest results");
        if (observation.LastOperationReceipt is { } receipt) {
            text.AppendLine($"- Operation {receipt.OperationSequence}, decision {receipt.DecisionSequence}, hour {Hours(receipt.At.Ticks)}: {(receipt.Accepted ? "accepted" : "rejected")}.");
            text.AppendLine($"  Command: {Data(receipt.Operation.RawInput)}; result: {Data(receipt.Result)}");
            if (receipt.GoalReceipt is { } goal) { text.AppendLine($"  Goal result: {Data(goal.Message)}; goal {goal.GoalId ?? "none"}; link {goal.LinkId ?? "none"}."); }
        } else { text.AppendLine("- No recorded command result yet."); }
        if (observation.LastExperience is { } experience) {
            text.AppendLine($"- Personal event at hour {Hours(experience.At.Ticks)} ({experience.Kind}): {Data(experience.Text)}");
        }
        text.AppendLine("Only the latest command result and personal event are retained here; this is not a complete history. A successful result has already happened: do not repeat it merely because it is displayed again.");
        text.AppendLine();

        text.AppendLine("## Nearby residents");
        foreach (TownVisibleResident resident in observation.Residents) { text.AppendLine($"- {resident.Id}: {Data(resident.Name)} — {resident.Activity}."); }
        if (observation.Residents.Count == 0) { text.AppendLine("- No other resident is visible at your location."); }
        text.AppendLine("Their money, inventory, food and private goals are not shown.");
        text.AppendLine();

        text.AppendLine("## Public recipes and places");
        text.AppendLine("market: external trade; workshop: production; home: a place to wait, with no free food or recovery bonus.");
        foreach (TownRecipe recipe in observation.Recipes) {
            text.AppendLine($"- {recipe.Id}: {recipe.Input} x{recipe.InputCount} → {recipe.Output} x{recipe.OutputCount}; {Hours(recipe.DurationTicks)} hours per batch at workshop.");
        }
        text.AppendLine("Production reserves all ingredients at the start and delivers all outputs when the entire batch completes; batches multiply time and quantities. No partial outputs or cancellation command are currently available.");
        text.AppendLine("Adjacent routes from your current place:");
        foreach (TownVisibleRoute route in observation.Routes) { text.AppendLine($"- {route.PassageId} → {route.Destination}; {Hours(route.DurationTicks)} hours."); }
        if (observation.Routes.Count == 0) { text.AppendLine("- None at this boundary."); }
        text.AppendLine();

        text.AppendLine("## External market notice");
        text.AppendLine($"Fixed public prices. Stock and buying quotas reset every 24 hours; next reset at hour {Hours(observation.NextMarketRefresh.Ticks)}. Unused amounts do not accumulate.");
        text.AppendLine(observation.PlaceId == "market"
            ? $"Remaining stock and quotas were observed locally at hour {Hours(observation.Time.Ticks)}; another resident's trade may change them before your next decision."
            : "Live remaining stock and buying quotas are unknown here. Visit market to observe them; displayed prices are public rules, not a remote stock observation.");
        foreach (TownVisibleMarketItem item in observation.Market) {
            if (item.BuyPrice > 0) { text.AppendLine($"- Buy {item.ItemId}: {item.BuyPrice} money/unit; stock {Known(item.AvailableStock)}."); }
            if (item.SellPrice > 0) { text.AppendLine($"- Sell {item.ItemId}: {item.SellPrice} money/unit; remaining buying quota {Known(item.RemainingDemand)}."); }
        }
        text.AppendLine();

        text.AppendLine("## Living and action rules");
        text.AppendLine($"Food decreases by {TownContent.FoodDecayPerHour} per game hour during every activity, including movement and waiting. Eating consumes one meal and restores {TownContent.MealFood}, capped at 100; excess restoration is lost. Food reaching zero causes failure, including a tie with production completion.");
        text.AppendLine("Money is earned by selling products, not automatically by waiting. One timed activity at a time. Starting wait/production/movement lets world time advance once all residents are busy; buying, selling, eating and goal edits take no game time and yield the next decision opportunity to another available resident.");
        text.AppendLine("Accepted does not mean prudent: a long legal activity may outlast your food or the experiment. You are normally called again after your timed activity completes; do not plan to eat midway without a decision opportunity.");
        text.AppendLine();

        text.AppendLine("## Commands at this observation");
        text.AppendLine("Use the exact IDs shown. Counted commands take a whole integer; each advertised range is inclusive and reflects current rules. Blocked commands are shown with their current reason.");
        foreach (TownVisibleAction action in observation.Actions) {
            text.AppendLine($"- `{action.Syntax}` — {(action.Available ? "available" : "blocked: " + Data(action.BlockedReason!))}. {action.Description}");
            if (action.Available) { text.AppendLine($"  Example: `{action.Example}`.{(action.HasQuantity ? $" Valid quantity: 1..{action.MaximumQuantity}." : "")}"); }
        }
        text.AppendLine();
        AppendGoals(text, observation);
        return text.ToString();
    }

    private static void AppendGoals(StringBuilder text, TownObservation observation) {
        GoalSet goals = observation.Goals;
        text.AppendLine("## Your private goals");
        text.AppendLine("These are your intentions and judgments, not world facts. Focus guides attention; finishing or pausing a goal does not change the world or cancel an activity.");
        text.AppendLine($"Unfinished: {goals.UnfinishedCount}/{GoalLimits.MaximumUnfinished}; retained: {goals.Goals.Count}/{GoalLimits.MaximumGoals}; links: {goals.Links.Count}/{GoalLimits.MaximumLinks}. Focus: {(goals.Focus.Count == 0 ? "none" : string.Join(" → ", goals.Focus))}.");
        foreach (Goal goal in goals.Goals) {
            text.AppendLine($"- {goal.Id} [{goal.Status}] {Data(goal.Title)}; criterion: {Data(goal.Criteria)}");
            foreach (var field in new[] { ("Progress", goal.Progress), ("Next", goal.NextStep), ("Revisit", goal.Revisit), ("Pause reason", goal.PauseReason), ("Conclusion", goal.EndNote) }) {
                if (field.Item2 is { } value) { text.AppendLine($"  {field.Item1}: {Data(value)}"); }
            }
            foreach (GoalRevision revision in goal.Revisions) {
                text.AppendLine($"  Retained correction ({revision.ContextSequence}/{revision.OperationSequence}): {Data(revision.Reason)}; previously [{revision.OldStatus}] {Data(revision.OldTitle)}; criterion {Data(revision.OldCriteria)}; conclusion {Data(revision.OldEndNote ?? "none")}.");
            }
        }
        if (goals.Goals.Count == 0) { text.AppendLine("- None."); }
        foreach (GoalLink link in goals.Links) { text.AppendLine($"- {link.Id}: {link.ParentId} → {link.ChildId}; reason: {Data(link.Reason)}"); }
        if (observation.WorldEnded || observation.Failed) { return; }
        text.AppendLine("### Goal commands (instant)");
        text.AppendLine("One command plus one strict JSON object. Replace example IDs with your own. A new goal needs title and criteria; parent and linkReason must be supplied together. Edit existing records instead of recreating them each decision.");
        text.AppendLine("```text");
        text.AppendLine("create-goal {\"title\":\"Maintain food supply\",\"criteria\":\"Keep two meals in reserve\",\"focus\":true}");
        text.AppendLine("create-goal {\"title\":\"Buy grain\",\"criteria\":\"Own four grain\",\"parent\":\"G1\",\"linkReason\":\"Prepare meals\"}");
        text.AppendLine("update-goal {\"id\":\"G1\",\"progress\":\"Have one meal\",\"nextStep\":\"Produce meals\",\"revisit\":null}");
        text.AppendLine("update-goal {\"id\":\"G1\",\"criteria\":\"Keep three meals in reserve\",\"reason\":\"Longer production planned\"}");
        text.AppendLine("link-goal {\"parent\":\"G1\",\"child\":\"G2\",\"reason\":\"Shared preparation\"}");
        text.AppendLine("update-goal-link {\"id\":\"L1\",\"reason\":\"Updated dependency\"}");
        text.AppendLine("unlink-goal {\"id\":\"L1\"}");
        text.AppendLine("focus-goal {\"id\":\"G2\",\"via\":[\"G1\",\"G2\"]}");
        text.AppendLine("focus-goal {\"id\":null}");
        text.AppendLine("pause-goal {\"id\":\"G1\",\"reason\":\"No market stock\",\"revisit\":\"After market reset\"}");
        text.AppendLine("resume-goal {\"id\":\"G1\"}");
        text.AppendLine("finish-goal {\"id\":\"G1\",\"outcome\":\"completed\",\"note\":\"Two meals obtained\"}");
        text.AppendLine("reopen-goal {\"id\":\"G1\",\"reason\":\"Earlier completion judgment was mistaken\"}");
        text.AppendLine("forget-goal {\"id\":\"G1\"}");
        text.AppendLine("show-goal {}\nshow-goal {\"id\":\"G1\"}");
        text.AppendLine("```");
        text.AppendLine("pause requires Open; resume requires Paused; finish accepts Open/Paused and outcome completed or abandoned; reopen requires Completed/Abandoned. Focus must end on Open; via follows existing links. Forget requires a finished goal with no links. Title/criteria changes and correction of a finished endNote require reason; reopen before other edits to finished goals. Optional progress/nextStep/revisit accept null to clear. A completed goal later becoming relevant again is normally a new need, not a mistaken earlier completion.");
        text.AppendLine($"Text limits (Unicode scalars): title {GoalLimits.TitleScalars}, criteria {GoalLimits.CriteriaScalars}, progress {GoalLimits.ProgressScalars}, nextStep/revisit {GoalLimits.NextStepScalars}, pause/end notes {GoalLimits.StateNoteScalars}, link/correction reason {GoalLimits.ReasonScalars}. Goal commands remain subject to these limits and graph/state checks; examples are syntax, not guaranteed valid operations for the current IDs.");
    }

    private static string Known(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
    private static string Hours(long ticks) => ((decimal)ticks / TownContent.Hour).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Data(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
