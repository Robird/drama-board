using System.Globalization;
using System.Text;
using System.Text.Json;
using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Provides one cognitive turn to every private memory shard maintainer.</summary>
public sealed record MemoryMaintenanceContext(
    CharacterCard CharacterCard,
    IReadOnlyList<ReferenceMaterial> ReferenceMaterials,
    MemoryBank PreviousMemory,
    DecisionRequest Request,
    string Monologue,
    Intent Intent,
    string? Dialogue,
    string MemoryProposal);

/// <summary>Represents either an explicit unchanged shortcut or replacement content for one shard.</summary>
public sealed record MemoryShardUpdate(
    string ShardKey,
    bool IsReplacement,
    string? Content)
{
    /// <summary>Creates an explicit keep-unchanged update.</summary>
    public static MemoryShardUpdate Keep(string shardKey) => new(shardKey, false, null);

    /// <summary>Creates a replacement update.</summary>
    public static MemoryShardUpdate Replace(string shardKey, string content) =>
        new(shardKey, true, content);
}

/// <summary>Maintains exactly one private memory shard after an actor turn.</summary>
public interface IMemoryShardMaintainer
{
    /// <summary>Gets the stable key of the only shard this instance may update.</summary>
    string ShardKey { get; }

    /// <summary>Returns keep or replacement content for the owned shard.</summary>
    Task<MemoryShardUpdate> MaintainAsync(
        MemoryMaintenanceContext context,
        CancellationToken cancellationToken);
}

/// <summary>Uses a focused LLM call to independently maintain one memory shard.</summary>
public sealed class LlmMemoryShardMaintainer : IMemoryShardMaintainer
{
    private readonly ILlmChatBackend _backend;

    /// <summary>Creates a maintainer for one stable shard key.</summary>
    public LlmMemoryShardMaintainer(string shardKey, ILlmChatBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardKey);
        ArgumentNullException.ThrowIfNull(backend);
        ShardKey = shardKey;
        _backend = backend;
    }

    /// <inheritdoc />
    public string ShardKey { get; }

    /// <inheritdoc />
    public async Task<MemoryShardUpdate> MaintainAsync(
        MemoryMaintenanceContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        MemoryShard target = context.PreviousMemory[ShardKey];
        LlmChatRequest prompt = RenderPrompt(context, target);
        string response = await _backend.CompleteAsync(prompt, cancellationToken);
        return ParseUpdate(response, target);
    }

    private LlmChatRequest RenderPrompt(MemoryMaintenanceContext context, MemoryShard target)
    {
        string system = new StringBuilder()
            .AppendLine("[角色认知维护]")
            .Append("你是角色“").Append(context.CharacterCard.Name)
            .AppendLine("”内部的记忆整理环节，不是客观世界记录器。")
            .Append("你只维护分块“").Append(target.Title).Append("”[")
            .Append(target.Key).AppendLine("]，不得输出或改写其他分块。")
            .Append("维护原则: ").AppendLine(target.MaintenanceInstructions)
            .AppendLine("你可以保留角色的猜想、误解、怀疑与情绪；不要把材料原文或他人说法自动升级成真相。")
            .AppendLine("本轮行动只是角色刚选择的意图，尚未被世界确认成功；只有后续 Observation/行动回执才能证明结果。")
            .AppendLine("若本轮没有足以改变该分块的信息，优先 keep。replace 必须返回更新后的完整分块，而非增量补丁。")
            .AppendLine("replace 的 content 只写正文，不要重复分块标题、key 或 Markdown 标题。")
            .AppendLine("只输出单个 JSON 对象，不要使用 Markdown：")
            .AppendLine("{\"operation\":\"keep\"}")
            .AppendLine("或 {\"operation\":\"replace\",\"content\":\"更新后的完整分块\"}")
            .ToString();

        var user = new StringBuilder()
            .AppendLine("[旧的完整 MemoryBank]")
            .AppendLine(context.PreviousMemory.Render())
            .AppendLine()
            .AppendLine("[本轮前可查阅材料]")
            .AppendLine(PromptRenderer.RenderReferenceMaterials(context.ReferenceMaterials))
            .AppendLine("[本轮观察]")
            .Append("时间: ").Append(context.Request.ModelTimeMs.ToString(CultureInfo.InvariantCulture))
            .Append("ms; 位置: ").AppendLine(context.Request.Observation.LocationId)
            .Append("请求原因: ").AppendLine(context.Request.Reason.Id)
            .Append("在场角色: ").AppendLine(RenderIds(context.Request.Observation.VisibleActorIds))
            .Append("可见物品: ").AppendLine(RenderIds(context.Request.Observation.VisibleObjectIds))
            .AppendLine("已知事实:");
        if (context.Request.Observation.KnownFacts.Count == 0)
        {
            user.AppendLine("- 无");
        }
        else
        {
            foreach (KnownFact fact in context.Request.Observation.KnownFacts)
            {
                user.Append("- kind=").Append(fact.FactKind.Id)
                    .Append(", related=").Append(fact.RelatedId ?? "-")
                    .Append(", text=").AppendLine(fact.Text);
            }
        }

        if (context.Request.RejectedIntent is not null)
        {
            user.Append("- 上次尝试被拒绝: ")
                .AppendLine(PromptRenderer.RenderIntent(context.Request.RejectedIntent));
        }

        user.AppendLine()
            .AppendLine("[角色本轮心智活动]")
            .Append("独白: ").AppendLine(context.Monologue)
            .Append("本轮刚选择、尚未确认成功的行动: ")
            .AppendLine(PromptRenderer.RenderIntent(context.Intent))
            .Append("台词: ").AppendLine(context.Dialogue ?? "（无）")
            .Append("角色希望记住的要点: ").AppendLine(
                string.IsNullOrWhiteSpace(context.MemoryProposal) ? "（无）" : context.MemoryProposal)
            .AppendLine()
            .Append("请只决定分块 [").Append(target.Key).AppendLine("] 的 keep 或 replace。");
        return new LlmChatRequest(system, user.ToString());
    }

    private MemoryShardUpdate ParseUpdate(string response, MemoryShard target)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new FormatException("Memory maintainer returned an empty response.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("operation", out JsonElement operationElement) ||
                operationElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Memory maintainer response lacks a string operation.");
            }

            string? operation = operationElement.GetString();
            if (string.Equals(operation, "keep", StringComparison.OrdinalIgnoreCase))
            {
                return MemoryShardUpdate.Keep(ShardKey);
            }

            if (!string.Equals(operation, "replace", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("content", out JsonElement contentElement) ||
                contentElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(contentElement.GetString()))
            {
                throw new FormatException("Memory maintainer response is neither keep nor a non-blank replacement.");
            }

            string content = StripRepeatedHeading(contentElement.GetString()!.Trim(), target);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new FormatException("Memory maintainer replacement contains only a repeated heading.");
            }

            return MemoryShardUpdate.Replace(ShardKey, content);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Memory maintainer response is not valid JSON.", exception);
        }
    }

    private static string StripRepeatedHeading(string content, MemoryShard target)
    {
        string heading = $"## {target.Title} [{target.Key}]";
        if (!content.StartsWith(heading, StringComparison.Ordinal))
        {
            return content;
        }

        int firstLineEnd = content.IndexOf('\n');
        return firstLineEnd < 0 ? string.Empty : content[(firstLineEnd + 1)..].TrimStart();
    }

    private static string RenderIds(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "[]" : $"[{string.Join(", ", ids)}]";
}
