using System.Globalization;
using System.Text;
using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Renders one self-contained, deterministic prompt for an LLM Player.</summary>
public static class PromptRenderer
{
    /// <summary>Renders the stable system prompt and request-specific user prompt.</summary>
    public static LlmChatRequest Render(
        CharacterCard characterCard,
        string memory,
        DecisionRequest request,
        IReadOnlyList<KnownFact> previousKnownFacts)
    {
        ArgumentNullException.ThrowIfNull(characterCard);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(previousKnownFacts);

        var system = new StringBuilder()
            .AppendLine("[角色卡]")
            .Append("名字: ").AppendLine(characterCard.Name)
            .Append("性格: ").AppendLine(characterCard.Personality)
            .Append("目标: ").AppendLine(characterCard.Goal)
            .Append("说话风格: ").AppendLine(characterCard.SpeakingStyle)
            .AppendLine()
            .AppendLine("[世界规则]")
            .AppendLine("你只能选择决策请求中列出的动作和候选目标。行动会由世界规则校验，不要虚构不可用的能力。")
            .AppendLine("回复必须使用以下四个分节标记；【行动】中只放一个 JSON 对象，字段与 Intent 对齐:")
            .AppendLine("【独白】一段内心想法")
            .AppendLine("【行动】{\"action\":\"action.wait\",\"targetActor\":null,\"targetObject\":null,\"destination\":null,\"freeText\":null,\"durationMs\":null,\"untilModelTimeMs\":null}")
            .AppendLine("【台词】可选，说出口的话")
            .AppendLine("【记忆】更新后的完整内心状态文档，整体替换旧文档")
            .ToString();

        var user = new StringBuilder()
            .AppendLine("[内心状态]")
            .AppendLine(string.IsNullOrWhiteSpace(memory) ? "（暂无）" : memory)
            .AppendLine()
            .AppendLine("[当前观察]")
            .Append("时间: ").Append(request.ModelTimeMs.ToString(CultureInfo.InvariantCulture))
            .Append("ms，微步: ").AppendLine(request.Microstep.ToString(CultureInfo.InvariantCulture))
            .Append("位置: ").AppendLine(request.Observation.LocationId)
            .Append("在场角色: ").AppendLine(RenderIds(request.Observation.VisibleActorIds))
            .Append("可见物品: ").AppendLine(RenderIds(request.Observation.VisibleObjectIds))
            .AppendLine("已知事实:");
        AppendFacts(user, request.Observation.KnownFacts, "- ");

        user.AppendLine()
            .AppendLine("[新近变化]");
        var previous = new HashSet<KnownFact>(previousKnownFacts);
        KnownFact[] addedFacts =
        [
            .. request.Observation.KnownFacts.Where(fact => !previous.Contains(fact)),
        ];
        if (addedFacts.Length == 0)
        {
            user.AppendLine("- 无新增事实");
        }
        else
        {
            AppendFacts(user, addedFacts, "- 新增事实: ");
        }

        if (request.RejectedIntent is not null)
        {
            user.Append("- 上次尝试被拒绝: ").AppendLine(RenderIntent(request.RejectedIntent));
        }

        user.AppendLine()
            .AppendLine("[决策请求]")
            .Append("请求原因: ").AppendLine(request.Reason.Id)
            .AppendLine("可用动作:");
        foreach (AvailableAction action in request.AvailableActions)
        {
            user.Append("- ").Append(action.ActionKind.Id)
                .Append("; actorCandidates=").Append(RenderIds(action.CandidateActorIds))
                .Append("; objectCandidates=").Append(RenderIds(action.CandidateObjectIds))
                .Append("; destinationCandidates=").AppendLine(RenderIds(action.CandidateDestinationIds));
        }

        user.Append("请按 system 中约定的四个分节作答。");
        return new LlmChatRequest(system, user.ToString());
    }

    private static void AppendFacts(
        StringBuilder text,
        IReadOnlyList<KnownFact> facts,
        string prefix)
    {
        if (facts.Count == 0)
        {
            text.AppendLine("- 无");
            return;
        }

        foreach (KnownFact fact in facts)
        {
            text.Append(prefix)
                .Append("kind=").Append(fact.FactKind.Id)
                .Append(", related=").Append(fact.RelatedId ?? "-")
                .Append(", text=").AppendLine(fact.Text);
        }
    }

    private static string RenderIds(IReadOnlyList<string>? ids) =>
        ids is { Count: > 0 }
            ? $"[{string.Join(", ", ids)}]"
            : "[]";

    private static string RenderIntent(Intent intent) =>
        $"action={intent.ActionKind.Id}, targetActor={intent.TargetActorId ?? "-"}, " +
        $"targetObject={intent.TargetObjectId ?? "-"}, destination={intent.DestinationId ?? "-"}, " +
        $"freeText={intent.FreeText ?? "-"}, durationMs={intent.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
        $"untilModelTimeMs={intent.UntilModelTimeMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
}
