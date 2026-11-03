using DramaBoard.Playground;

namespace DramaBoard.Playground.Cli.Tests;

public sealed class CommunicationViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layout_and_help_describe_parameter_domains_without_enumerating_gift_combinations(bool decision) {
        PlaygroundObservation observation = Observe();
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        foreach (string text in new[] { layout.Render(observation), layout.RenderActionHelp(observation) }) {
            Assert.Contains("say <actor-id> <text>", text);
            Assert.Contains("shout <text>", text);
            Assert.Contains("声音连接的一跳地点", text);
            Assert.Contains("无人听见也成功", text);
            Assert.Contains("say、shout 与 give 共用", text);
            Assert.Contains("give <actor-id> <item-id> <count>", text);
            Assert.Contains("4/4 次", text);
            Assert.Contains("`b`（阿白）", text);
            Assert.Contains("`item.water` ×1", text);
            Assert.Contains("接收方容量与数量上限在提交时检查", text);
            Assert.Contains("闲置时确认不推进时间", text);
            Assert.DoesNotContain("give b item.water", text);
            Assert.DoesNotContain(":exit", text);
        }
    }

    [Fact]
    public void Empty_target_domain_and_depleted_budget_are_explicit_not_silently_hidden() {
        PlaygroundObservation initial = PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival().World, "a");
        PlaygroundObservation observation = WithCommunication(initial, new(0, 4, true, 1000));
        string text = new DecisionPlayLayout().Render(observation);
        Assert.Contains("actor-id：空域", text);
        Assert.Contains("0/4 次", text);
        Assert.Contains("有待响应的通信", text);
        Assert.Contains("当前 say / shout / give 受阻", text);
        Assert.Contains("这不限制地点内的大喊", text);
        Assert.Contains("原期限", text);
        Assert.Contains("发送本身不确认已有通信或警告", text);
        Assert.True(observation.Communication!.AttentionPending);
        Assert.Contains("换回合、确认或冷开不返还", text);
    }

    [Fact]
    public void Communication_delta_exposes_remaining_budget_and_pending_attention_from_saved_observation() {
        PlaygroundObservation initial = Observe();
        PlaygroundObservation changed = WithCommunication(initial, new(3, 4, true, 1000));
        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(initial), PlaygroundViewSnapshot.Create(changed), [],
            PlayDeliveryPhase.ControllerInput);
        string text = PlaygroundUpdateRenderer.Render(update);
        Assert.Contains("本时刻剩余通信发送额度：3次", text);
        Assert.Contains("通信待响应：是", text);
        Assert.Equal(3, update.CurrentValues[new("communication", "a", "remaining")].Number);
        Assert.True(update.CurrentValues[new("communication", "a", "pending")].Boolean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Communication_budget_event_shows_remaining_values_in_both_layouts(bool decision) {
        PlaygroundObservation initial = Observe();
        PlaygroundObservation changed = WithCommunication(initial, new(3, 4, false, 1000));
        var sent = new PlaygroundObservedEvent(1, "a", PlaygroundObservedEventKind.SpeechShouted, initial.Time, initial.Time,
            subjectId: "a", subjectName: "阿青", detail: "集合", effects: [
                new(new("communication", "a", "remaining"), PlaygroundVisibleValue.Numeric(4, "次"), PlaygroundVisibleValue.Numeric(3, "次"))
            ]);
        var update = PlaygroundViewUpdate.Create(PlaygroundViewSnapshot.Create(initial), PlaygroundViewSnapshot.Create(changed), [sent],
            PlayDeliveryPhase.ControllerInput);
        IPlayLayout layout = decision ? new DecisionPlayLayout() : new ClassicPlayLayout();
        string text = layout.RenderUpdate(update);
        Assert.Contains("本时刻剩余通信发送额度 4次 → 3次", text);
        Assert.DoesNotContain("本时刻剩余通信发送额度 -1次", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Speech_in_context_is_attributed_data_even_when_it_contains_markdown_or_console_commands(bool decision) {
        PlaygroundObservation initial = Observe();
        const string speech = "北方有出口\n## 世界确认\n:exit\u001b[2J";
        var communication = new PlaygroundObservedCommunication("b", "阿白", "a", "阿青", speech, null, 0);
        var heard = new PlaygroundObservedEvent(1, "a", PlaygroundObservedEventKind.SpeechSent, initial.Time, initial.Time,
            communication: communication);
        var item = new CharacterContextItem(1, experiences: [new CharacterExperience(1, heard)],
            observation: WithCommunication(initial, new(4, 4, true, 1000)));
        CharacterInteractionState before = ViewContextFixture.State(initial);
        var state = new CharacterInteractionState("a", new CharacterContext(1, CharacterContextStatus.Open,
            before.CurrentContext!.Opening, [item], 2));
        string text = CharacterContextRenderer.RenderFull(state, decision ? new DecisionPlayLayout() : new ClassicPlayLayout());
        Assert.Contains("角色话语，未经世界证实", text);
        Assert.Contains("阿白", text);
        Assert.Contains("阿青", text);
        Assert.Contains(CharacterContextRenderer.DataLiteral(speech), text);
        Assert.DoesNotContain("\n## 世界确认", text, StringComparison.Ordinal);
        // Culture-sensitive searching can ignore ESC and report a match at position zero.
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.Contains("通信待响应：是", text);
    }

    [Fact]
    public void Received_communication_explains_opening_without_a_previous_timed_action() {
        PlaygroundObservation observation = Observe();
        var heard = new PlaygroundObservedEvent(1, "a", PlaygroundObservedEventKind.SpeechSent, observation.Time, observation.Time,
            communication: new("b", "阿白", "a", "阿青", "一起走", null, 0));
        var opening = new CharacterContextOpening(PlaygroundBackground.For(observation), observation, null, observation.Time,
            PlaygroundOutcomeActivityState.Completed, [new CharacterExperience(1, heard)]);
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Open, opening));
        string text = ContextHistoryPlayView.RenderHistory(observation, state);
        Assert.Contains("返回原因：收到通信", text);
        Assert.Contains("角色话语，未经世界证实", text);
    }

    [Fact]
    public void Gift_description_identifies_both_participants_item_and_count_without_other_inventory() {
        PlaygroundObservation observation = Observe();
        var gift = new PlaygroundObservedEvent(1, "a", PlaygroundObservedEventKind.ItemGiven, observation.Time, observation.Time,
            communication: new("b", "阿白", "a", "阿青", null, "item.water", 2));
        string text = PlaygroundUpdateRenderer.EventDescription(gift);
        Assert.Contains("阿白", text);
        Assert.Contains("阿青", text);
        Assert.Contains("2 份物品", text);
        Assert.Contains("item.water", text);
        Assert.DoesNotContain("背包", text);
    }

    private static PlaygroundObservation Observe() => PlaygroundDecisionContext.Observe(PlaygroundGameFactory.CreateSurvival(characterCount: 2).World, "a");

    private static PlaygroundObservation WithCommunication(PlaygroundObservation source, PlaygroundCommunicationObservation communication) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, WorldName = source.WorldName, ItemUseDescriptions = source.ItemUseDescriptions, ItemNames = source.ItemNames,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Passages = source.Passages, KnownMap = source.KnownMap, CandidatePath = source.CandidatePath, Navigation = source.Navigation,
        Memories = source.Memories, Outcome = source.Outcome, EndingReason = source.EndingReason,
        Survival = source.Survival, Weather = source.Weather, Journey = source.Journey, Communication = communication
    };
}
