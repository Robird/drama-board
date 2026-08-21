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
        MemoryBank memory,
        DecisionRequest request,
        IReadOnlyList<KnownFact> previousKnownFacts,
        IReadOnlyList<ReferenceMaterial>? referenceMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(characterCard);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(previousKnownFacts);
        referenceMaterials ??= [];

        var system = new StringBuilder()
            .AppendLine("[角色卡]")
            .Append("名字: ").AppendLine(characterCard.Name)
            .Append("性格: ").AppendLine(characterCard.Personality)
            .Append("目标: ").AppendLine(characterCard.Goal)
            .Append("说话风格: ").AppendLine(characterCard.SpeakingStyle)
            .AppendLine()
            .AppendLine("[世界规则]")
            .AppendLine("你只能选择决策请求中列出的动作和候选目标。行动会由世界规则校验，不要虚构不可用的能力。")
            .AppendLine("可反复查阅的材料只保证其来源和原文稳定，不保证内容真实，也不代表你必须相信它；判断权属于你。")
            .AppendLine("台词和口头承诺本身不会转移物品；give 会转移所有权，show 只展示而不转移。put 会把自持物放到当前公共环境并失去所有权，随后同地角色可检查或拿走；带物品目标的 observe 用于仔细检查候选物品。")
            .AppendLine("观察结果会完整进入当前观察/已知事实；环境与事实没有变化时，重复观察不会发现暗格或更深线索。")
            .AppendLine("把已知事实当作权威结果，不要反复计划动作列表中不存在的后续操作；若已无可推进之事，可长时间等待。")
            .AppendLine("回复必须使用以下四个分节标记；【行动】中只放一个 JSON 对象，字段与 Intent 对齐:")
            .AppendLine("【独白】一段内心想法")
            .AppendLine("【行动】{\"action\":\"action.wait\",\"targetActor\":null,\"targetObject\":null,\"destination\":null,\"freeText\":null,\"durationMs\":null,\"untilModelTimeMs\":null}")
            .AppendLine("【台词】可选，说出口的话")
            .AppendLine("【记忆】本轮希望各记忆分块吸收、修正或忘却的要点；这不是完整记忆，可以写（无）")
            .ToString();

        var user = new StringBuilder();
        AppendReferenceMaterials(user, referenceMaterials);
        user
            .AppendLine("[内心状态]")
            .AppendLine(memory.Render())
            .AppendLine()
            .AppendLine("[当前观察]")
            .Append("时间: ").Append(request.ModelTimeMs.ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms")
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

        user.AppendLine()
            .AppendLine("[决策请求]")
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

    private static void AppendReferenceMaterials(
        StringBuilder text,
        IReadOnlyList<ReferenceMaterial> materials)
    {
        text.AppendLine("[可反复查阅的材料]")
            .AppendLine(RenderReferenceMaterials(materials))
            .AppendLine();
    }

    internal static string RenderReferenceMaterials(IReadOnlyList<ReferenceMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var text = new StringBuilder();
        if (materials.Count == 0)
        {
            return "（无）";
        }

        foreach (ReferenceMaterial material in materials)
        {
            text.Append("- [").Append(material.Id).Append("] 来源: ")
                .AppendLine(material.Source)
                .Append("  原文: ").AppendLine(material.Content);
        }

        text.Append("以上是材料记载的内容，不是对其真伪的裁决；你可以怀疑、重新解释或不再采信。");
        return text.ToString();
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

    internal static string RenderIntent(Intent intent) =>
        $"action={intent.ActionKind.Id}, targetActor={intent.TargetActorId ?? "-"}, " +
        $"targetObject={intent.TargetObjectId ?? "-"}, destination={intent.DestinationId ?? "-"}, " +
        $"freeText={intent.FreeText ?? "-"}, durationMs={intent.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
        $"untilModelTimeMs={intent.UntilModelTimeMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
}
