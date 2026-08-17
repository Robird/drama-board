using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DramaBoard.FirstBoard;

/// <summary>Defines one place and its directed travel connections.</summary>
public sealed record ScenarioPlaceDefinition(
    string Id,
    IReadOnlyList<string> AdjacentPlaceIds);

/// <summary>Defines one private source that an actor may repeatedly consult.</summary>
public sealed record ScenarioReferenceMaterialDefinition(
    string Id,
    string Source,
    string Content);

/// <summary>Defines one initial private-memory shard without depending on an LLM runtime.</summary>
public sealed record ScenarioMemoryShardDefinition(
    string Key,
    string Title,
    string MaintenanceInstructions,
    string InitialContent);

/// <summary>Defines the narrative role assigned to one scenario actor.</summary>
public sealed record ScenarioRoleDefinition(
    string Name,
    string Traits,
    string Goal,
    string Voice,
    IReadOnlyList<ScenarioReferenceMaterialDefinition> ReferenceMaterials,
    IReadOnlyList<ScenarioMemoryShardDefinition> InitialMemoryShards);

/// <summary>Defines one actor's stable identity, initial location, and private role material.</summary>
public sealed record ScenarioActorDefinition(
    string Id,
    string InitialPlaceId,
    ScenarioRoleDefinition Role);

/// <summary>Defines one world object's initial public location, owner, or hidden state.</summary>
public sealed record ScenarioObjectDefinition(
    string Id,
    string? InitialPlaceId,
    string? InitialOwnerActorId);

/// <summary>Contains the immutable, seed-independent content of a FirstBoard scenario.</summary>
public sealed record ScenarioDefinition(
    string Id,
    int Revision,
    string RulesetId,
    long CellarDeadlineMs,
    IReadOnlyList<ScenarioPlaceDefinition> Places,
    IReadOnlyList<ScenarioActorDefinition> Actors,
    IReadOnlyList<ScenarioObjectDefinition> Objects)
{
    public const string FirstBoardRuleset = "firstboard.duchess-letter/1";

    /// <summary>Gets the current hand-authored FirstBoard scenario as immutable data.</summary>
    public static ScenarioDefinition Default { get; } = CreateDefault();

    /// <summary>Returns the actor definition with the requested stable id.</summary>
    public ScenarioActorDefinition Actor(string actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    /// <summary>Builds the objective initial world for one seeded scenario instance.</summary>
    public FirstBoardWorld CreateInitialWorld(ulong worldSeed)
    {
        Validate();
        long nextId = 1;
        long worldRuleSourceId = nextId++;
        BoardPlace[] places =
        [
            .. Places.Select(place => new BoardPlace(
                nextId++,
                place.Id,
                Array.AsReadOnly(place.AdjacentPlaceIds.ToArray()))),
        ];
        BoardActor[] actors =
        [
            .. Actors.Select(actor => NewActor(nextId++, actor.Id, actor.InitialPlaceId)),
        ];
        IReadOnlyDictionary<string, long> actorIds = actors.ToDictionary(
            actor => actor.Key,
            actor => actor.Id,
            StringComparer.Ordinal);
        BoardObject[] objects =
        [
            .. Objects.Select(item => new BoardObject(
                nextId++,
                item.Id,
                item.InitialPlaceId,
                item.InitialOwnerActorId is null ? null : actorIds[item.InitialOwnerActorId],
                ContentionRound: 0)),
        ];

        return new FirstBoardWorld(
            worldSeed,
            worldRuleSourceId,
            nextId,
            Array.AsReadOnly(places),
            Array.AsReadOnly(actors),
            Array.AsReadOnly(objects),
            CellarSealed: false,
            ChestOpened: false);
    }

    /// <summary>Returns canonical UTF-8 JSON whose bytes define the scenario hash contract.</summary>
    public byte[] ToCanonicalJsonUtf8()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "dramaboard.scenario-definition/1");
            writer.WriteString("id", Id);
            writer.WriteNumber("revision", Revision);
            writer.WriteString("rulesetId", RulesetId);
            writer.WriteNumber("cellarDeadlineMs", CellarDeadlineMs);
            WritePlaces(writer);
            WriteActors(writer);
            WriteObjects(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Returns a stable SHA-256 hex digest of the canonical scenario definition.</summary>
    public string ComputeSha256() =>
        Convert.ToHexString(SHA256.HashData(ToCanonicalJsonUtf8())).ToLowerInvariant();

    /// <summary>Deep-copies all collections into read-only snapshots at an instance boundary.</summary>
    public ScenarioDefinition Freeze()
    {
        Validate();
        return this with
        {
            Places = Array.AsReadOnly(
                Places.Select(place => place with
                {
                    AdjacentPlaceIds = Array.AsReadOnly(place.AdjacentPlaceIds.ToArray()),
                }).ToArray()),
            Actors = Array.AsReadOnly(
                Actors.Select(actor => actor with
                {
                    Role = actor.Role with
                    {
                        ReferenceMaterials = Array.AsReadOnly(
                            actor.Role.ReferenceMaterials.ToArray()),
                        InitialMemoryShards = Array.AsReadOnly(
                            actor.Role.InitialMemoryShards.ToArray()),
                    },
                }).ToArray()),
            Objects = Array.AsReadOnly(Objects.ToArray()),
        };
    }

    /// <summary>Rejects broken references before a definition becomes a world or manifest.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(RulesetId);
        if (Revision <= 0)
        {
            throw new InvalidOperationException("Scenario revision must be positive.");
        }

        if (RulesetId != FirstBoardRuleset)
        {
            throw new InvalidOperationException(
                $"FirstBoard cannot run ruleset '{RulesetId}'.");
        }

        if (CellarDeadlineMs < 0)
        {
            throw new InvalidOperationException("The cellar deadline cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(Places);
        ArgumentNullException.ThrowIfNull(Actors);
        ArgumentNullException.ThrowIfNull(Objects);
        HashSet<string> placeIds = UniqueIds(Places.Select(place => place.Id), "place");
        HashSet<string> actorIds = UniqueIds(Actors.Select(actor => actor.Id), "actor");
        HashSet<string> objectIds = UniqueIds(Objects.Select(item => item.Id), "object");
        RequireIds(placeIds, "place", BoardIds.Tavern, BoardIds.Market, BoardIds.Cellar);
        RequireIds(actorIds, "actor", BoardIds.Alice, BoardIds.Bob);
        RequireIds(
            objectIds,
            "object",
            BoardIds.BrassKey,
            BoardIds.DuchessLetter,
            BoardIds.SilverCoinOne,
            BoardIds.SilverCoinTwo);

        foreach (ScenarioPlaceDefinition place in Places)
        {
            ArgumentNullException.ThrowIfNull(place.AdjacentPlaceIds);
            foreach (string adjacentId in place.AdjacentPlaceIds)
            {
                if (!placeIds.Contains(adjacentId))
                {
                    throw new InvalidOperationException(
                        $"Place '{place.Id}' references unknown place '{adjacentId}'.");
                }
            }
        }

        foreach (ScenarioActorDefinition actor in Actors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actor.InitialPlaceId);
            if (!placeIds.Contains(actor.InitialPlaceId))
            {
                throw new InvalidOperationException(
                    $"Actor '{actor.Id}' starts at unknown place '{actor.InitialPlaceId}'.");
            }

            ValidateRole(actor);
        }

        foreach (ScenarioObjectDefinition item in Objects)
        {
            if (item.InitialPlaceId is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.InitialPlaceId);
            }

            if (item.InitialOwnerActorId is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.InitialOwnerActorId);
            }

            if (item.InitialPlaceId is not null && item.InitialOwnerActorId is not null)
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' cannot start both placed and owned.");
            }

            if (item.InitialPlaceId is not null && !placeIds.Contains(item.InitialPlaceId))
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' starts at unknown place '{item.InitialPlaceId}'.");
            }

            if (item.InitialOwnerActorId is not null && !actorIds.Contains(item.InitialOwnerActorId))
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' starts with unknown owner '{item.InitialOwnerActorId}'.");
            }
        }
    }

    private static ScenarioDefinition CreateDefault() =>
        new(
            "firstboard.duchess-letter-market",
            Revision: 1,
            FirstBoardRuleset,
            BoardTiming.DeadlineTicks,
            Array.AsReadOnly<ScenarioPlaceDefinition>(
            [
                new(BoardIds.Tavern, [BoardIds.Market]),
                new(BoardIds.Market, [BoardIds.Tavern, BoardIds.Cellar]),
                new(BoardIds.Cellar, [BoardIds.Market]),
            ]),
            Array.AsReadOnly<ScenarioActorDefinition>(
            [
                new(
                    BoardIds.Alice,
                    BoardIds.Tavern,
                    new ScenarioRoleDefinition(
                        "爱丽丝",
                        "谨慎、敏锐，不轻易相信别人；在压力下仍保持克制",
                        "查明公爵夫人密信的下落与内容，并据此保护自己的长期利益",
                        "简短、克制，习惯用试探性问题",
                        Array.AsReadOnly<ScenarioReferenceMaterialDefinition>(
                        [
                            new(
                                "alice.case-notes",
                                "爱丽丝在酒馆根据零散传闻写下的案情笔记",
                                "公爵夫人的密信可能锁在地窖箱中；黄铜钥匙最近在集市出现；地窖门口公告称一小时后永久封闭。"),
                            new(
                                "alice.meeting-note",
                                "爱丽丝昨夜与鲍勃谈过后留下的会面备忘",
                                "先去集市摊位会面；若钥匙和鲍勃都不在，至少等待五分钟，避免与返回摊位的鲍勃错身。"),
                        ]),
                        DefaultMemory(
                            "我在酒馆，准备按案情笔记去集市寻找钥匙和鲍勃；当前尚未核实密信、钥匙或地窖传闻。",
                            "我目前打算遵守 alice.meeting-note：若在集市与鲍勃错身，至少等五分钟。若鲍勃持有密信，我会优先要求他把信放到公共环境供我亲自检查，再决定是否支付余款；我知道放下后在场者都能拿走。情况明显变化时可以明确修改计划。",
                            "案情笔记只是零散传闻。钥匙很可能与地窖箱有关；鲍勃可能帮忙，也可能借机要挟。",
                            "我对鲍勃保持戒备，但目前没有足够证据认定他会违约；双方尚无已履行的交易债务。"))),
                new(
                    BoardIds.Bob,
                    BoardIds.Market,
                    new ScenarioRoleDefinition(
                        "鲍勃",
                        "务实、机会主义，但并非冷酷；喜欢掌握谈判筹码",
                        "利用黄铜钥匙和密信线索取得收益，同时避免把自己困在无法兑现的交易中",
                        "直率，偶尔讥讽，谈条件时毫不含糊",
                        Array.AsReadOnly<ScenarioReferenceMaterialDefinition>(
                        [
                            new(
                                "bob.lead-ledger",
                                "鲍勃自己的生意账本边角记录",
                                "摊位附近可能有一把黄铜钥匙；爱丽丝正在追查地窖中的密信；地窖门口公告称一小时后封闭。"),
                            new(
                                "bob.meeting-note",
                                "鲍勃记下的昨夜会面安排",
                                "若先拿钥匙去地窖，开箱后回集市摊位至少等待十分钟；爱丽丝会先去摊位寻找鲍勃。"),
                        ]),
                        DefaultMemory(
                            "我在集市，准备先确认摊位附近的钥匙是否还在；当前尚未核实账本边角记录。",
                            "我目前愿意遵守 bob.meeting-note：若先去地窖开箱，之后回集市至少等爱丽丝十分钟。为促成交易，我可以把密信放到公共环境让爱丽丝亲自检查，但这会放弃控制并允许她直接拿走；若风险过高，可改用 show 或明确改主意。",
                            "钥匙可能让我取得密信筹码，但拖得太久可能一无所获。爱丽丝似乎很在意那封信。",
                            "我把爱丽丝视为潜在交易对象而不是同盟；是否履约取决于她实际展示或交付的筹码。"))),
            ]),
            Array.AsReadOnly<ScenarioObjectDefinition>(
            [
                new(BoardIds.BrassKey, BoardIds.Market, null),
                new(BoardIds.DuchessLetter, null, null),
                new(BoardIds.SilverCoinOne, null, BoardIds.Alice),
                new(BoardIds.SilverCoinTwo, null, BoardIds.Alice),
            ]));

    private static IReadOnlyList<ScenarioMemoryShardDefinition> DefaultMemory(
        string working,
        string commitments,
        string beliefs,
        string relationships) =>
        Array.AsReadOnly<ScenarioMemoryShardDefinition>(
        [
            new(
                "working_context",
                "当前处境与未决线索",
                "快速更新当前处境、近期经历和仍需处理的线索；无需重复 Observation 已直接给出的琐碎状态。",
                working),
            new(
                "commitments",
                "承诺与计划",
                "保存约定、承诺、期限和多步计划。未完成事项默认 keep；只有完成、明确放弃、已不可能或被新计划取代时才修改，并写明理由。",
                commitments),
            new(
                "beliefs",
                "判断与假说",
                "维护角色自己的猜想、证据来源、反证和置信变化；区分材料写了什么、他人说了什么与自己相信什么。",
                beliefs),
            new(
                "relationships",
                "关系与社会账本",
                "缓慢维护信任、戒备、情绪、恩怨和已履行或未履行的债务；中性事件通常 keep，变化应保留依据。",
                relationships),
        ]);

    private static void ValidateRole(ScenarioActorDefinition actor)
    {
        ArgumentNullException.ThrowIfNull(actor.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Role.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Role.Traits);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Role.Goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor.Role.Voice);
        ArgumentNullException.ThrowIfNull(actor.Role.ReferenceMaterials);
        ArgumentNullException.ThrowIfNull(actor.Role.InitialMemoryShards);
        UniqueIds(actor.Role.ReferenceMaterials.Select(material => material.Id), "reference material");
        UniqueIds(actor.Role.InitialMemoryShards.Select(shard => shard.Key), "memory shard");
        foreach (ScenarioReferenceMaterialDefinition material in actor.Role.ReferenceMaterials)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(material.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(material.Content);
        }

        foreach (ScenarioMemoryShardDefinition shard in actor.Role.InitialMemoryShards)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.Title);
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.MaintenanceInstructions);
            ArgumentException.ThrowIfNullOrWhiteSpace(shard.InitialContent);
        }

        if (actor.Role.InitialMemoryShards.Count == 0)
        {
            throw new InvalidOperationException(
                $"Actor '{actor.Id}' must have at least one initial memory shard.");
        }
    }

    private static HashSet<string> UniqueIds(IEnumerable<string> ids, string kind)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (!result.Add(id))
            {
                throw new InvalidOperationException($"Duplicate scenario {kind} id '{id}'.");
            }
        }

        return result;
    }

    private static void RequireIds(
        IReadOnlySet<string> actual,
        string kind,
        params string[] required)
    {
        foreach (string id in required)
        {
            if (!actual.Contains(id))
            {
                throw new InvalidOperationException(
                    $"The FirstBoard ruleset requires {kind} '{id}'.");
            }
        }
    }

    private void WritePlaces(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("places");
        foreach (ScenarioPlaceDefinition place in Places)
        {
            writer.WriteStartObject();
            writer.WriteString("id", place.Id);
            writer.WriteStartArray("adjacentPlaceIds");
            foreach (string adjacentId in place.AdjacentPlaceIds)
            {
                writer.WriteStringValue(adjacentId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteActors(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("actors");
        foreach (ScenarioActorDefinition actor in Actors)
        {
            writer.WriteStartObject();
            writer.WriteString("id", actor.Id);
            writer.WriteString("initialPlaceId", actor.InitialPlaceId);
            writer.WriteStartObject("role");
            writer.WriteString("name", actor.Role.Name);
            writer.WriteString("traits", actor.Role.Traits);
            writer.WriteString("goal", actor.Role.Goal);
            writer.WriteString("voice", actor.Role.Voice);
            writer.WriteStartArray("referenceMaterials");
            foreach (ScenarioReferenceMaterialDefinition material in actor.Role.ReferenceMaterials)
            {
                writer.WriteStartObject();
                writer.WriteString("id", material.Id);
                writer.WriteString("source", material.Source);
                writer.WriteString("content", material.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("initialMemoryShards");
            foreach (ScenarioMemoryShardDefinition shard in actor.Role.InitialMemoryShards)
            {
                writer.WriteStartObject();
                writer.WriteString("key", shard.Key);
                writer.WriteString("title", shard.Title);
                writer.WriteString("maintenanceInstructions", shard.MaintenanceInstructions);
                writer.WriteString("initialContent", shard.InitialContent);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteObjects(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("objects");
        foreach (ScenarioObjectDefinition item in Objects)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.Id);
            if (item.InitialPlaceId is null)
            {
                writer.WriteNull("initialPlaceId");
            }
            else
            {
                writer.WriteString("initialPlaceId", item.InitialPlaceId);
            }

            if (item.InitialOwnerActorId is null)
            {
                writer.WriteNull("initialOwnerActorId");
            }
            else
            {
                writer.WriteString("initialOwnerActorId", item.InitialOwnerActorId);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static BoardActor NewActor(long id, string key, string placeId) =>
        new(
            id,
            key,
            placeId,
            Generation: 0,
            DecisionSequence: 0,
            AwaitingDecision: false,
            OpenDecisionId: null,
            PendingAction: null,
            Activity: null,
            KnownFacts: [],
            LastRejectedIntent: null);
}

/// <summary>Binds an immutable scenario definition to the random seed of one instance.</summary>
public sealed record ScenarioInstance
{
    /// <summary>Freezes one definition and binds it to an objective-world random seed.</summary>
    public ScenarioInstance(ScenarioDefinition definition, ulong worldSeed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition.Freeze();
        WorldSeed = worldSeed;
        DefinitionSha256 = Definition.ComputeSha256();
        InstanceSha256 = ComputeInstanceSha256(DefinitionSha256, worldSeed);
    }

    /// <summary>Gets the frozen seed-independent scenario content.</summary>
    public ScenarioDefinition Definition { get; }

    /// <summary>Gets the deterministic random seed of the objective world.</summary>
    public ulong WorldSeed { get; }

    /// <summary>Gets the stable content hash shared by all seeds of this definition.</summary>
    public string DefinitionSha256 { get; }

    /// <summary>Gets the full stable identity of this frozen definition and seed.</summary>
    public string InstanceSha256 { get; }

    /// <summary>Gets a compact stable identity for this concrete seeded instance.</summary>
    public string Id => FormattableString.Invariant(
        $"{Definition.Id}@{Definition.Revision}/{DefinitionSha256[..12]}/seed-{WorldSeed}");

    /// <summary>Creates the current default scenario with the requested world seed.</summary>
    public static ScenarioInstance CreateDefault(ulong worldSeed) =>
        new(ScenarioDefinition.Default, worldSeed);

    /// <summary>Builds this instance's objective initial world.</summary>
    public FirstBoardWorld CreateInitialWorld() => Definition.CreateInitialWorld(WorldSeed);

    private static string ComputeInstanceSha256(string definitionSha256, ulong worldSeed)
    {
        string canonical = FormattableString.Invariant(
            $"dramaboard.scenario-instance/1\n{definitionSha256}\n{worldSeed}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
