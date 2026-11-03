using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.Playground;

/// <summary>One frozen decision's observation and scoped, side-effect-free command validation.</summary>
/// <remarks>The game owns construction and authoritative validation. The controller does not receive
/// the world; invalid or guessed distant IDs cannot use rejection details to query hidden state.</remarks>
public sealed class PlaygroundDecisionContext {
    private readonly Func<PlaygroundCommand?, string?> _check;
    private readonly Func<PlaygroundCommand, string?> _warning;
    public PlaygroundObservation Observation { get; }
    public CharacterInteractionState InteractionState { get; }
    public CharacterContext Context => InteractionState.CurrentContext!;
    private PlaygroundDecisionContext(PlaygroundObservation observation, CharacterInteractionState interactionState,
        Func<PlaygroundCommand?, string?> check, Func<PlaygroundCommand, string?> warning) {
        Observation = observation;
        InteractionState = interactionState;
        _check = check;
        _warning = warning;
    }
    public string? Check(PlaygroundCommand? command) => _check(command);
    /// <summary>Known survival risk for a visible, legal action; does not reserve or execute it.</summary>
    public string? Warning(PlaygroundCommand command) => _warning(command);

    public static PlaygroundDecisionContext Create(PlaygroundWorld world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        CharacterContext context = state.CurrentContext ?? throw new InvalidOperationException("The character has no committed context.");
        PlaygroundObservation observation = context.LatestObservation;
        string? Check(PlaygroundCommand? command) {
            if (command?.Kind == PlaygroundCommandKind.Gather && !observation.Resources.Any(resource => resource.Id == command.ResourceId)) {
                return "当前不可见或不可用的资源点。";
            }
            if (command?.Kind == PlaygroundCommandKind.Go && !observation.Passages.Any(passage => passage.Id == command.EntranceId)) {
                return "当前没有可用的这个入口；请使用当前入口列表中的编号。";
            }
            return PlaygroundActions.Check(world, actorId, command, null, visibleOnly: true);
        }
        return new(observation, state, Check,
            command => Check(command) is null ? PlaygroundSurvivalRules.ActionWarning(world, actorId, command) : null);
    }

    /// <summary>Builds the same visible snapshot without retaining decision history or query delegates.</summary>
    public static PlaygroundObservation Observe(PlaygroundWorld world, string actorId) {
        // A retired character never acquires a fresh view of the continuing world.
        if (!world.GetCharacter(actorId).IsActive && world.Business.InteractionStates[actorId].CurrentContext is
            { Status: CharacterContextStatus.Terminal } terminal) { return terminal.LatestObservation; }
        return Project(world, actorId).Observation;
    }
    internal static PlaygroundObservation ObserveOpening(PlaygroundWorld world, string actorId, long contextSequence) => Project(world, actorId, contextSequence).Observation;

    private static (PlaygroundObservation Observation, Func<PlaygroundCommand?, string?> Check, Func<PlaygroundCommand, string?> Warning)
        Project(PlaygroundWorld world, string actorId, long? contextSequence = null) {
        ArgumentNullException.ThrowIfNull(world);
        PlaygroundCharacter actor = world.GetCharacter(actorId);
        PlaygroundExpedition? expedition = world.Expedition;
        SpatialLocation? location = PlaygroundActions.Location(world, actorId);
        string? place = (location as AtPlaceLocation)?.PlaceId.Value;
        PlaygroundResource[] resources = world.Resources.Where(resource => expedition is null ||
            place is not null && world.Definition.GetResourcePlace(resource.Id) == place).ToArray();
        PlaygroundPassageDefinition[] passages = expedition is not null && place is not null
            ? [.. world.Definition.GetEntrances(place)] : [];
        string? Check(PlaygroundCommand? command) {
            if (command?.Kind == PlaygroundCommandKind.Gather && !resources.Any(resource => resource.Id == command.ResourceId)) {
                return "当前不可见或不可用的资源点。";
            }
            if (command?.Kind == PlaygroundCommandKind.Go && !passages.Any(passage => passage.EntranceId == command.EntranceId)) {
                return "当前没有可用的这个入口；请使用当前入口列表中的编号。";
            }
            return PlaygroundActions.Check(world, actorId, command, contextSequence);
        }
        string? Warning(PlaygroundCommand command) => Check(command) is null
            ? PlaygroundSurvivalRules.ActionWarning(world, actorId, command) : null;
        PlaygroundOtherCharacterObservation[] others = world.Characters.Where(other => other.IsActive && other.Id != actorId &&
            (expedition is null || place is not null && PlaygroundActions.CurrentPlace(world, other.Id) == place))
            .Select(other => new PlaygroundOtherCharacterObservation(other.Id, other.Name,
                other.Activity is { Kind: PlaygroundActivityKind.Gather } gathering ? gathering.ResourceId : null,
                other.Activity?.Kind == PlaygroundActivityKind.Wait, other.FollowingLeaderId, other.Activity?.Production?.RecipeId)).ToArray();
        PlaygroundNoticeObservation[] notices = actor.Notices.Select(notice => {
            PlaygroundResource? visible = resources.SingleOrDefault(resource => resource.Id == notice.ResourceId);
            PlaygroundResource? remembered = expedition?.Visits.Where(visit => visit.ActorId == actorId)
                .OrderByDescending(visit => visit.At).SelectMany(visit => visit.Resources).FirstOrDefault(resource => resource.Id == notice.ResourceId);
            PlaygroundOtherCharacterObservation? other = others.SingleOrDefault(character => character.Id == notice.ActorId);
            string who = notice.ActorId == actorId ? $"{actor.Name}（{actor.Id}）" :
                other is not null ? $"{other.Name}（{other.Id}）" : notice.ActorId ?? "环境或未知原因";
            bool? stillGathering = visible is null ? null : notice.ActorId == actorId
                ? actor.Activity is { Kind: PlaygroundActivityKind.Gather } gathering && gathering.ResourceId == notice.ResourceId
                : other?.GatheringResourceId == notice.ResourceId;
            return new PlaygroundNoticeObservation(notice, visible?.Name ?? remembered?.Name ?? notice.ResourceId,
                who, stillGathering, visible?.Stock, visible?.Capacity);
        }).ToArray();
        IReadOnlyList<PlaygroundMarketQuote> quotes = PlaygroundEconomyObservation.Quotes(world.Business, place);
        IReadOnlyList<PlaygroundRecipe> recipes = PlaygroundEconomyObservation.Recipes(world.Business, place);
        IReadOnlyList<PlaygroundTradeOffer> trades = PlaygroundEconomyObservation.Trades(world.Business, actorId);
        List<PlaygroundCommand> commands = [PlaygroundCommand.Continue(), PlaygroundCommand.Cancel()];
        if (PlaygroundActions.SupportsHandoff(world)) { commands.Add(PlaygroundCommand.Handoff(null)); }
        foreach (PlaygroundResource resource in resources) {
            commands.Add(PlaygroundCommand.Gather(resource.Id));
            commands.Add(PlaygroundCommand.GatherAll(resource.Id));
        }
        if (expedition is not null) {
            commands.Add(PlaygroundCommand.Map());
            if (expedition.CandidatePath(actorId) is { } candidate) { commands.Add(PlaygroundCommand.Navigate(candidate.Id)); }
            commands.AddRange(PlaygroundKnownMap.Observe(world.Business, actorId).Select(p => PlaygroundCommand.Path(p.Id)));
        }
        commands.AddRange(Enumerable.Range(1, 24).Select(PlaygroundCommand.Wait));
        commands.AddRange(passages.Select(passage => PlaygroundCommand.Go(passage.EntranceId)));
        if (expedition is not null) {
            commands.AddRange(others.Select(other => PlaygroundCommand.Follow(other.Id)));
            commands.Add(PlaygroundCommand.Unfollow());
            commands.Add(PlaygroundCommand.DismissFollowers());
        }
        if (actor.Survival is not null) {
            commands.AddRange(actor.Backpack.Items.Keys.Concat(resources.Select(resource => resource.ItemId))
                .Distinct(StringComparer.Ordinal).Where(id => PlaygroundSurvivalRules.UseDescription(world.Definition, id) is not null)
                .Order(StringComparer.Ordinal).Select(PlaygroundCommand.Use));
        }
        commands.AddRange(quotes.Where(quote => quote.BuyPrice > 0).Select(quote => PlaygroundCommand.Buy(quote.ItemId)));
        commands.AddRange(quotes.Where(quote => quote.SellPrice > 0).Select(quote => PlaygroundCommand.Sell(quote.ItemId)));
        commands.AddRange(recipes.Select(recipe => PlaygroundCommand.Produce(recipe.Id)));
        if (world.Business.Economy is not null) {
            // Starter offers use only the observer's inventory and known item identities, never a neighbour's holdings.
            string[] knownItems = actor.Backpack.Items.Keys.Concat(resources.Select(resource => resource.ItemId))
                .Concat(quotes.Select(quote => quote.ItemId)).Concat(recipes.SelectMany(recipe => new[] { recipe.InputItem, recipe.OutputItem }))
                .Concat(trades.SelectMany(offer => new[] { offer.Give, offer.Want }).Where(asset => !asset.IsMoney).Select(asset => asset.AssetId))
                .Where(world.Definition.Items.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (PlaygroundOtherCharacterObservation other in others) {
                foreach (string item in knownItems) {
                    if (actor.Money > 0) { commands.Add(PlaygroundCommand.Offer(other.Id, new("money", 1), new(item, 1))); }
                    if (actor.Backpack.Count(item) > 0) { commands.Add(PlaygroundCommand.Offer(other.Id, new(item, 1), new("money", 1))); }
                }
            }
            foreach (PlaygroundTradeOffer offer in trades.Where(offer => offer.Status == PlaygroundTradeStatus.Active)) {
                if (offer.ProposerId == actorId) { commands.Add(PlaygroundCommand.Withdraw(offer.Id)); }
                else { commands.Add(PlaygroundCommand.Accept(offer.Id)); commands.Add(PlaygroundCommand.Reject(offer.Id)); }
            }
        }
        // Candidate acceptance is provisional; submission still uses the authoritative full Check above.
        string? DisplayCheck(PlaygroundCommand command) => PlaygroundTradeRules.IsTradeCommand(command)
            ? PlaygroundActions.Check(world, actorId, command, contextSequence, visibleOnly: true) : Check(command);
        PlaygroundVisit[] visits = expedition?.Visits.Where(visit => visit.ActorId == actorId).ToArray() ?? [];
        ModelTime? arrival = location is TraversingLocation moving ? moving.ArrivalDue : null;
        var itemNames = actor.Backpack.Items.Keys.Concat(resources.Select(resource => resource.ItemId))
            .Concat(quotes.Select(quote => quote.ItemId)).Concat(recipes.SelectMany(recipe => new[] { recipe.InputItem, recipe.OutputItem }))
            .Concat(trades.SelectMany(offer => new[] { offer.Give, offer.Want }).Where(asset => !asset.IsMoney).Select(asset => asset.AssetId))
            .Concat(actor.Activity?.Production is { } production ? new[] { production.InputItem, production.OutputItem } : [])
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => PlaygroundEconomyObservation.ItemName(world.Definition, id), StringComparer.Ordinal);
        var useDescriptions = itemNames.Keys.Select(item => (Item: item, Description: PlaygroundSurvivalRules.UseDescription(world.Definition, item)))
            .Where(pair => pair.Description is not null).ToDictionary(pair => pair.Item, pair => pair.Description!, StringComparer.Ordinal);
        var observation = new PlaygroundObservation {
            WorldName = world.Definition.Name,
            ItemUseDescriptions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(useDescriptions),
            Time = actor.Ending?.At ?? world.Time, IsDay = world.Light.IsDay, NextLightChange = world.Light.NextChange,
            Goals = expedition is null ? null : world.Business.InteractionStates[actorId].Goals,
            FollowingLeaderId = actor.FollowingLeaderId,
            FollowingLeaderName = actor.FollowingLeaderId is { } leader ? world.GetCharacter(leader).Name : null,
            Followers = world.Characters.Where(other => other.FollowingLeaderId == actorId)
                .ToDictionary(other => other.Id, other => other.Name, StringComparer.Ordinal),
            Actor = new(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack, actor.Activity,
                Array.AsReadOnly(actor.Notices.ToArray()), actor.LastResult, actor.NextActivityId, actor.Money),
            Resources = Array.AsReadOnly(resources),
            MarketQuotes = quotes, Recipes = recipes, Trades = trades,
            MarketRefreshAt = quotes.Count == 0 ? null : world.Business.Economy!.Market.NextRefresh,
            HarvestDurations = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(resources.ToDictionary(
                resource => resource.Id, resource => PlaygroundActions.HarvestDuration(resource, actor), StringComparer.Ordinal)),
            ItemNames = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(itemNames),
            OtherCharacters = Array.AsReadOnly(others), Notices = Array.AsReadOnly(notices),
            Commands = Array.AsReadOnly(commands.Select(command => new PlaygroundCommandCheck(command, DisplayCheck(command), PlaygroundTradeRules.IsTradeCommand(command) ? null : Warning(command))).ToArray()),
            IsExpedition = expedition is not null, PlaceId = place,
            PlaceName = expedition is not null && place is not null ? world.Definition.GetPlace(place).Name : null,
            PlaceDescription = expedition is not null && place is not null ? world.Definition.GetPlace(place).Description : null,
            ArrivalDue = arrival,
            Navigation = expedition?.Navigation(actorId),
            CandidatePath = expedition?.CandidatePath(actorId),
            KnownMap = PlaygroundKnownMap.Observe(world.Business, actorId),
            Journey = PlaygroundJourneyObservation.Observe(world.Definition, expedition, actorId, location),
            Passages = Array.AsReadOnly(passages.Select(passage => new PlaygroundPassageObservation(passage.EntranceId, passage.Description,
                passage.DurationTicks, expedition!.KnowsPassage(actorId, passage.Id) ? world.Definition.GetPlace(passage.To).Name : null)).ToArray()),
            Memories = Array.AsReadOnly(visits.Where(visit => visit.PlaceId != place).Select(visit =>
                new PlaygroundPlaceMemory(world.Definition.GetPlace(visit.PlaceId).Name, visit.At,
                    Array.AsReadOnly(visit.Resources.ToArray()), visit.PlaceId)).ToArray()),
            Outcome = actor.Ending?.Outcome ?? PlaygroundExpeditionOutcome.Exploring, EndingReason = actor.Ending?.Reason,
            Survival = PlaygroundSurvivalRules.Observe(world, actorId),
            Weather = PlaygroundEnvironmentRules.Observe(world, actorId),
            Communication = expedition is null ? null : new(PlaygroundCommunicationRules.Remaining(actor, world.Time),
                PlaygroundCommunicationRules.SendLimit, actor.Communication.AttentionPending, PlaygroundCommunicationRules.MaximumSpeechScalars)
        };
        return (observation, Check, Warning);
    }
}
