using System.Text;
using DramaBoard.ResourceCycle;

/// <summary>Parameterized communication help from frozen, actor-scoped material; never a Cartesian action list.</summary>
internal static class CommunicationView {
    public static void Render(StringBuilder text, CycleObservation observation) {
        if (observation.Communication is not { } communication || observation.Outcome != CycleExpeditionOutcome.Exploring) { return; }
        text.AppendLine("### 同地交流与赠与");
        text.AppendLine($"本时刻剩余发送额度：{communication.RemainingSends}/{communication.SendLimit} 次（say 与 give 共用）。成功才扣额；换回合、确认或冷开不返还，模型时间前进后恢复。");
        text.AppendLine(communication.AttentionPending
            ? "**有待响应的通信。** 可先回复、赠与或使用物品，再 `continue` 确认。"
            : "当前没有待响应的通信。");
        text.AppendLine("`continue` 确认通信、通知与历史警告，保留原活动及截止时刻；跟随时确认后继续同行；闲置时确认不推进时间，仍须选择耗时行动。");
        text.AppendLine($"- `say <actor-id> <text>`：发送署名话语，不耗时间；text 为非空文字，最多 {communication.MaximumSpeechScalars} 个 Unicode 字符，可含空格。话语是角色的说法，不代表世界已证实。");
        text.AppendLine("- `give <actor-id> <item-id> <count>`：不耗时间；count 为正整数且不超过自己持有量。接收方容量与数量上限在提交时检查；拒绝不扣物、不转移、不扣发送额度。");
        text.AppendLine(observation.OtherCharacters.Count == 0
            ? "actor-id：空域；当前没有可交流的同地角色。途中、远处或已结束角色不可交互。"
            : "actor-id：" + string.Join("、", observation.OtherCharacters.Select(other => $"`{other.Id}`（{other.Name}）")) + "；仅当前同地活跃角色。");
        text.AppendLine(observation.Actor.Backpack.Items.Count == 0
            ? "item-id：空域；当前背包没有可赠与物品。"
            : "item-id 与自己的持有量：" + string.Join("、", observation.Actor.Backpack.Items.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"`{item.Key}` ×{item.Value}")) + "。");
        if (communication.RemainingSends == 0) { text.AppendLine("当前 say / give 受阻：本时刻发送额度已用完；可确认通信、使用物品或安排耗时行动。"); }
        text.AppendLine("收到通信会请求响应，但不取消活动；发送本身不确认已有通信或警告。模型的说明文字不会自动成为对白，须显式使用 say。");
        text.AppendLine();
    }
}
