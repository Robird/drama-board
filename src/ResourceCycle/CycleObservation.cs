using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.ResourceCycle;

/// <summary>One frozen decision's observation and scoped, side-effect-free command validation.</summary>
/// <remarks>The game owns construction and authoritative validation. The controller does not receive
/// the world; invalid or guessed distant IDs cannot use rejection details to query hidden state.</remarks>
public sealed class CycleDecisionContext {
    private readonly Func<CycleCommand?, string?> _check;
    private readonly Func<CycleCommand, string?> _warning;
    public CycleObservation Observation { get; }
    public CharacterInteractionState InteractionState { get; }
    public CharacterContext Context => InteractionState.CurrentContext!;
    private CycleDecisionContext(CycleObservation observation, CharacterInteractionState interactionState,
        Func<CycleCommand?, string?> check, Func<CycleCommand, string?> warning) {
        Observation = observation;
        InteractionState = interactionState;
        _check = check;
        _warning = warning;
    }
    public string? Check(CycleCommand? command) => _check(command);
    /// <summary>Known survival risk for a visible, legal action; does not reserve or execute it.</summary>
    public string? Warning(CycleCommand command) => _warning(command);

    public static CycleDecisionContext Create(CycleWorld world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        CharacterInteractionState state = world.Business.InteractionStates[actorId];
        CharacterContext context = state.CurrentContext ?? throw new InvalidOperationException("The character has no committed context.");
        CycleObservation observation = context.LatestObservation;
        string? Check(CycleCommand? command) {
            if (command?.Kind == CycleCommandKind.Gather && !observation.Resources.Any(resource => resource.Id == command.ResourceId)) {
                return "当前不可见或不可用的资源点。";
            }
            if (command?.Kind == CycleCommandKind.Go && !observation.Passages.Any(passage => passage.Id == command.PassageId)) {
                return "当前不可见或不可用的通路。";
            }
            return CycleActions.Check(world, actorId, command);
        }
        return new(observation, state, Check,
            command => Check(command) is null ? CycleSurvivalRules.ActionWarning(world, actorId, command) : null);
    }

    /// <summary>Builds the same visible snapshot without retaining decision history or query delegates.</summary>
    public static CycleObservation Observe(CycleWorld world, string actorId) {
        // A retired character never acquires a fresh view of the continuing world.
        if (!world.GetCharacter(actorId).IsActive && world.Business.InteractionStates[actorId].CurrentContext is
            { Status: CharacterContextStatus.Terminal } terminal) { return terminal.LatestObservation; }
        return Project(world, actorId).Observation;
    }
    internal static CycleObservation ObserveOpening(CycleWorld world, string actorId, long contextSequence) => Project(world, actorId, contextSequence).Observation;

    private static (CycleObservation Observation, Func<CycleCommand?, string?> Check, Func<CycleCommand, string?> Warning)
        Project(CycleWorld world, string actorId, long? contextSequence = null) {
        ArgumentNullException.ThrowIfNull(world);
        CycleCharacter actor = world.GetCharacter(actorId);
        CycleExpedition? expedition = world.Expedition;
        SpatialLocation? location = CycleActions.Location(world, actorId);
        string? place = (location as AtPlaceLocation)?.PlaceId.Value;
        CycleResource[] resources = world.Resources.Where(resource => expedition is null ||
            place is not null && CycleExpeditionMap.GetResourcePlace(resource.Id) == place).ToArray();
        CyclePassageDefinition[] passages = expedition is not null && place is not null
            ? [.. CycleExpeditionMap.GetPassages(place)] : [];
        string? Check(CycleCommand? command) {
            if (command?.Kind == CycleCommandKind.Gather && !resources.Any(resource => resource.Id == command.ResourceId)) {
                return "当前不可见或不可用的资源点。";
            }
            if (command?.Kind == CycleCommandKind.Go && !passages.Any(passage => passage.Id == command.PassageId)) {
                return "当前不可见或不可用的通路。";
            }
            return CycleActions.Check(world, actorId, command, contextSequence);
        }
        string? Warning(CycleCommand command) => Check(command) is null
            ? CycleSurvivalRules.ActionWarning(world, actorId, command) : null;
        CycleOtherCharacterObservation[] others = world.Characters.Where(other => other.IsActive && other.Id != actorId &&
            (expedition is null || place is not null && CycleActions.CurrentPlace(world, other.Id) == place))
            .Select(other => new CycleOtherCharacterObservation(other.Id, other.Name,
                other.Activity is { Kind: CycleActivityKind.Gather } gathering ? gathering.ResourceId : null,
                other.Activity?.Kind == CycleActivityKind.Wait)).ToArray();
        CycleNoticeObservation[] notices = actor.Notices.Select(notice => {
            CycleResource? visible = resources.SingleOrDefault(resource => resource.Id == notice.ResourceId);
            CycleResource? remembered = expedition?.Visits.Where(visit => visit.ActorId == actorId)
                .OrderByDescending(visit => visit.At).SelectMany(visit => visit.Resources).FirstOrDefault(resource => resource.Id == notice.ResourceId);
            CycleOtherCharacterObservation? other = others.SingleOrDefault(character => character.Id == notice.ActorId);
            string who = notice.ActorId == actorId ? $"{actor.Name}（{actor.Id}）" :
                other is not null ? $"{other.Name}（{other.Id}）" : notice.ActorId ?? "环境或未知原因";
            bool? stillGathering = visible is null ? null : notice.ActorId == actorId
                ? actor.Activity is { Kind: CycleActivityKind.Gather } gathering && gathering.ResourceId == notice.ResourceId
                : other?.GatheringResourceId == notice.ResourceId;
            return new CycleNoticeObservation(notice, visible?.Name ?? remembered?.Name ?? notice.ResourceId,
                who, stillGathering, visible?.Stock, visible?.Capacity);
        }).ToArray();
        List<CycleCommand> commands = [CycleCommand.Continue(), CycleCommand.Cancel()];
        if (CycleActions.SupportsHandoff(world)) { commands.Add(CycleCommand.Handoff(null)); }
        foreach (CycleResource resource in resources) {
            commands.Add(CycleCommand.Gather(resource.Id));
            commands.Add(CycleCommand.GatherAll(resource.Id));
        }
        commands.AddRange(Enumerable.Range(1, 24).Select(CycleCommand.Wait));
        commands.AddRange(passages.Select(passage => CycleCommand.Go(passage.Id)));
        if (actor.Survival is not null) {
            commands.AddRange(actor.Backpack.Items.Keys.Concat(resources.Select(resource => resource.ItemId))
                .Distinct(StringComparer.Ordinal).Where(id => CycleSurvivalRules.UseDescription(id) is not null)
                .Order(StringComparer.Ordinal).Select(CycleCommand.Use));
        }
        CycleVisit[] visits = expedition?.Visits.Where(visit => visit.ActorId == actorId).ToArray() ?? [];
        ModelTime? arrival = location is TraversingLocation moving ? moving.ArrivalDue : null;
        var itemNames = actor.Backpack.Items.Keys.Concat(resources.Select(resource => resource.ItemId))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => expedition is null
                ? world.Resources.FirstOrDefault(resource => resource.ItemId == id)?.Name ?? id
                : id switch { "item.food" => "食物", "item.water" => "饮水", "item.wood" => "干木", _ => id }, StringComparer.Ordinal);
        var observation = new CycleObservation {
            Time = actor.Ending?.At ?? world.Time, IsDay = world.Light.IsDay, NextLightChange = world.Light.NextChange,
            Actor = new(actor.Id, actor.Name, actor.SkillLevel, actor.Backpack, actor.Activity,
                Array.AsReadOnly(actor.Notices.ToArray()), actor.LastResult, actor.NextActivityId),
            Resources = Array.AsReadOnly(resources),
            HarvestDurations = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(resources.ToDictionary(
                resource => resource.Id, resource => CycleActions.HarvestDuration(resource, actor), StringComparer.Ordinal)),
            ItemNames = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(itemNames),
            OtherCharacters = Array.AsReadOnly(others), Notices = Array.AsReadOnly(notices),
            Commands = Array.AsReadOnly(commands.Select(command => new CycleCommandCheck(command, Check(command), Warning(command))).ToArray()),
            IsExpedition = expedition is not null, PlaceId = place,
            PlaceName = expedition is not null && place is not null ? CycleExpeditionMap.GetPlace(place).Name : null,
            PlaceDescription = expedition is not null && place is not null ? CycleExpeditionMap.GetPlace(place).Description : null,
            ArrivalDue = arrival,
            Journey = CycleJourneyObservation.Observe(expedition, actorId, location),
            Passages = Array.AsReadOnly(passages.Select(passage => new CyclePassageObservation(passage.Id, passage.Description,
                passage.DurationTicks, visits.Any(visit => visit.PlaceId == passage.To) ? CycleExpeditionMap.GetPlace(passage.To).Name : null)).ToArray()),
            Memories = Array.AsReadOnly(visits.Where(visit => visit.PlaceId != place).Select(visit =>
                new CyclePlaceMemory(CycleExpeditionMap.GetPlace(visit.PlaceId).Name, visit.At,
                    Array.AsReadOnly(visit.Resources.ToArray()), visit.PlaceId)).ToArray()),
            Outcome = actor.Ending?.Outcome ?? CycleExpeditionOutcome.Exploring, EndingReason = actor.Ending?.Reason,
            Survival = CycleSurvivalRules.Observe(world, actorId),
            Weather = CycleEnvironmentRules.Observe(world, actorId),
            Communication = expedition is null ? null : new(CycleCommunicationRules.Remaining(actor, world.Time),
                CycleCommunicationRules.SendLimit, actor.Communication.AttentionPending, CycleCommunicationRules.MaximumSpeechScalars)
        };
        return (observation, Check, Warning);
    }
}
