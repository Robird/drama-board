using DramaBoard.ResourceCycle;

namespace DramaBoard.ResourceCycle.Cli.Tests;

public sealed class CommunicationViewTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Layout_and_help_describe_parameter_domains_without_enumerating_gift_combinations(bool decision) {
        CycleObservation observation = Observe();
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
        CycleObservation initial = CycleDecisionContext.Observe(CycleGameFactory.CreateSurvival().World, "a");
        CycleObservation observation = WithCommunication(initial, new(0, 4, true, 1000));
        string text = new DecisionPlayLayout().Render(observation);
        Assert.Contains("actor-id：空域", text);
        Assert.Contains("0/4 次", text);
        Assert.Contains("有待响应的通信", text);
        Assert.Contains("当前 say / shout / give 受阻", text);
        Assert.Contains("这不限制地点内的大喊", text);
        Assert.Contains("保留原活动及截止时刻", text);
        Assert.Contains("换回合、确认或冷开不返还", text);
    }

    [Fact]
    public void Communication_delta_exposes_remaining_budget_and_pending_attention_from_saved_observation() {
        CycleObservation initial = Observe();
        CycleObservation changed = WithCommunication(initial, new(3, 4, true, 1000));
        var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(initial), CycleViewSnapshot.Create(changed), [],
            PlayDeliveryPhase.ControllerInput);
        string text = CycleUpdateRenderer.Render(update);
        Assert.Contains("本时刻剩余通信发送额度：3次", text);
        Assert.Contains("通信待响应：是", text);
        Assert.Equal(3, update.CurrentValues[new("communication", "a", "remaining")].Number);
        Assert.True(update.CurrentValues[new("communication", "a", "pending")].Boolean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Communication_budget_event_shows_remaining_values_in_both_layouts(bool decision) {
        CycleObservation initial = Observe();
        CycleObservation changed = WithCommunication(initial, new(3, 4, false, 1000));
        var sent = new CycleObservedEvent(1, "a", CycleObservedEventKind.SpeechShouted, initial.Time, initial.Time,
            subjectId: "a", subjectName: "阿青", detail: "集合", effects: [
                new(new("communication", "a", "remaining"), CycleVisibleValue.Numeric(4, "次"), CycleVisibleValue.Numeric(3, "次"))
            ]);
        var update = CycleViewUpdate.Create(CycleViewSnapshot.Create(initial), CycleViewSnapshot.Create(changed), [sent],
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
        CycleObservation initial = Observe();
        const string speech = "北方有出口\n## 世界确认\n:exit\u001b[2J";
        var communication = new CycleObservedCommunication("b", "阿白", "a", "阿青", speech, null, 0);
        var heard = new CycleObservedEvent(1, "a", CycleObservedEventKind.SpeechSent, initial.Time, initial.Time,
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
        CycleObservation observation = Observe();
        var heard = new CycleObservedEvent(1, "a", CycleObservedEventKind.SpeechSent, observation.Time, observation.Time,
            communication: new("b", "阿白", "a", "阿青", "一起走", null, 0));
        var opening = new CharacterContextOpening(CycleBackground.For(observation), observation, null, observation.Time,
            CycleOutcomeActivityState.Completed, [new CharacterExperience(1, heard)]);
        var state = new CharacterInteractionState("a", new CharacterContext(2, CharacterContextStatus.Open, opening));
        string text = ContextHistoryPlayView.RenderHistory(observation, state);
        Assert.Contains("返回原因：收到通信", text);
        Assert.Contains("角色话语，未经世界证实", text);
    }

    [Fact]
    public void Gift_description_identifies_both_participants_item_and_count_without_other_inventory() {
        CycleObservation observation = Observe();
        var gift = new CycleObservedEvent(1, "a", CycleObservedEventKind.ItemGiven, observation.Time, observation.Time,
            communication: new("b", "阿白", "a", "阿青", null, "item.water", 2));
        string text = CycleUpdateRenderer.EventDescription(gift);
        Assert.Contains("阿白", text);
        Assert.Contains("阿青", text);
        Assert.Contains("2 份物品", text);
        Assert.Contains("item.water", text);
        Assert.DoesNotContain("背包", text);
    }

    private static CycleObservation Observe() => CycleDecisionContext.Observe(CycleGameFactory.CreateSurvival(characterCount: 2).World, "a");

    private static CycleObservation WithCommunication(CycleObservation source, CycleCommunicationObservation communication) => new() {
        Time = source.Time, IsDay = source.IsDay, NextLightChange = source.NextLightChange, Actor = source.Actor,
        Resources = source.Resources, HarvestDurations = source.HarvestDurations, WorldName = source.WorldName, ItemUseDescriptions = source.ItemUseDescriptions, ItemNames = source.ItemNames,
        OtherCharacters = source.OtherCharacters, Notices = source.Notices, Commands = source.Commands,
        IsExpedition = source.IsExpedition, PlaceId = source.PlaceId, PlaceName = source.PlaceName,
        PlaceDescription = source.PlaceDescription, ArrivalDue = source.ArrivalDue, Passages = source.Passages,
        Memories = source.Memories, Outcome = source.Outcome, EndingReason = source.EndingReason,
        Survival = source.Survival, Weather = source.Weather, Journey = source.Journey, Communication = communication
    };
}
