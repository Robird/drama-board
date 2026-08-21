using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DramaBoard.Kernel.Time;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard;

public sealed record ScenarioPlaceDefinition(string Id);

public sealed record ScenarioPassageDefinition(
    string Id,
    string EndpointAId,
    string EndpointBId,
    long Length,
    bool EnterableFromA,
    bool EnterableFromB,
    string? RequiredTicketObjectId = null);

public sealed record ScenarioReferenceMaterialDefinition(
    string Id,
    string Source,
    string Content);

public sealed record ScenarioMemoryShardDefinition(
    string Key,
    string Title,
    string MaintenanceInstructions,
    string InitialContent);

public sealed record ScenarioRoleDefinition(
    string Name,
    string Traits,
    string Goal,
    string Voice,
    IReadOnlyList<ScenarioReferenceMaterialDefinition> ReferenceMaterials,
    IReadOnlyList<ScenarioMemoryShardDefinition> InitialMemoryShards);

public sealed record ScenarioActorDefinition(
    string Id,
    string InitialPlaceId,
    ScenarioRoleDefinition Role);

/// <summary>An object starts public at one Place, carried by one actor, or hidden when both are null.</summary>
public sealed record ScenarioObjectDefinition(
    string Id,
    string? InitialPlaceId,
    string? InitialOwnerActorId);

/// <summary>Contains immutable FirstBoard content and adapts its spatial portion to GraphDefinition.</summary>
public sealed record ScenarioDefinition(
    string Id,
    int Revision,
    string RulesetId,
    long CellarDeadlineMs,
    IReadOnlyList<ScenarioPlaceDefinition> Places,
    IReadOnlyList<ScenarioPassageDefinition> Passages,
    IReadOnlyList<ScenarioActorDefinition> Actors,
    IReadOnlyList<ScenarioObjectDefinition> Objects)
{
    public const string FirstBoardRuleset = "firstboard.duchess-letter/2";

    public static ScenarioDefinition Default { get; } = CreateDefault();

    public ScenarioActorDefinition Actor(string actorId) =>
        Actors.Single(actor => actor.Id == actorId);

    public GraphDefinition CreateGraphDefinition()
    {
        Validate();
        return GraphDefinition.Create(
            Places.Select(place => new PlaceId(place.Id)),
            Passages.Select(passage => new PassageDefinition(
                new PassageId(passage.Id),
                new PlaceId(passage.EndpointAId),
                new PlaceId(passage.EndpointBId),
                passage.Length,
                new PassageEntryAccess(
                    passage.EnterableFromA,
                    passage.EnterableFromB))));
    }

    public string? RequiredTicket(PassageId passageId) =>
        Passages.Single(passage => passage.Id == passageId.Value).RequiredTicketObjectId;

    public FirstBoardWorld CreateInitialWorld(ulong worldSeed)
    {
        GraphDefinition graph = CreateGraphDefinition();
        long nextId = 1;
        BoardActor[] actors =
        [
            .. Actors
                .OrderBy(actor => actor.Id, StringComparer.Ordinal)
                .Select(actor => NewActor(nextId++, actor.Id)),
        ];
        IReadOnlyDictionary<string, long> actorIds = actors.ToDictionary(
            actor => actor.Key,
            actor => actor.Id,
            StringComparer.Ordinal);
        BoardObject[] objects =
        [
            .. Objects
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new BoardObject(
                    nextId++,
                    item.Id,
                    item.InitialOwnerActorId is null ? null : actorIds[item.InitialOwnerActorId])),
        ];

        EntityPlacement[] placements =
        [
            .. Actors.Select(actor =>
                new EntityPlacement(
                    new EntityId(actor.Id),
                    new PlaceId(actor.InitialPlaceId))),
            .. Objects
                .Where(item => item.InitialPlaceId is not null)
                .Select(item =>
                    new EntityPlacement(
                        new EntityId(item.Id),
                        new PlaceId(item.InitialPlaceId!))),
            new(
                new EntityId(BoardIds.LockedChest),
                new PlaceId(BoardIds.Cellar)),
        ];
        var game = new FirstBoardGameState(
            worldSeed,
            nextId,
            ModelTime.Zero,
            Array.AsReadOnly(actors),
            Array.AsReadOnly(objects),
            CellarSealed: false,
            ChestOpened: false);
        return new FirstBoardWorld(
            game,
            GraphSpatialState.Create(graph, placements));
    }

    public byte[] ToCanonicalJsonUtf8()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "dramaboard.scenario-definition/2");
            writer.WriteString("id", Id);
            writer.WriteNumber("revision", Revision);
            writer.WriteString("rulesetId", RulesetId);
            writer.WriteNumber("cellarDeadlineMs", CellarDeadlineMs);
            WritePlaces(writer);
            WritePassages(writer);
            WriteActors(writer);
            WriteObjects(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public string ComputeSha256() =>
        Convert.ToHexString(SHA256.HashData(ToCanonicalJsonUtf8())).ToLowerInvariant();

    public ScenarioDefinition Freeze()
    {
        Validate();
        return this with
        {
            Places = Array.AsReadOnly(Places
                .OrderBy(place => place.Id, StringComparer.Ordinal)
                .ToArray()),
            Passages = Array.AsReadOnly(Passages
                .OrderBy(passage => passage.Id, StringComparer.Ordinal)
                .ToArray()),
            Actors = Array.AsReadOnly(Actors
                .OrderBy(actor => actor.Id, StringComparer.Ordinal)
                .Select(actor => actor with
                {
                    Role = actor.Role with
                    {
                        ReferenceMaterials = Array.AsReadOnly(actor.Role.ReferenceMaterials
                            .OrderBy(material => material.Id, StringComparer.Ordinal)
                            .ToArray()),
                        InitialMemoryShards = Array.AsReadOnly(actor.Role.InitialMemoryShards
                            .OrderBy(shard => shard.Key, StringComparer.Ordinal)
                            .ToArray()),
                    },
                })
                .ToArray()),
            Objects = Array.AsReadOnly(Objects
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()),
        };
    }

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
        ArgumentNullException.ThrowIfNull(Passages);
        ArgumentNullException.ThrowIfNull(Actors);
        ArgumentNullException.ThrowIfNull(Objects);
        HashSet<string> placeIds = UniqueIds(Places.Select(place => place.Id), "place");
        HashSet<string> passageIds = UniqueIds(Passages.Select(passage => passage.Id), "passage");
        HashSet<string> actorIds = UniqueIds(Actors.Select(actor => actor.Id), "actor");
        HashSet<string> objectIds = UniqueIds(Objects.Select(item => item.Id), "object");

        string[] sharedEntityIds =
        [
            .. actorIds.Intersect(objectIds, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        if (sharedEntityIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"Actor and object identifiers share the Spatial entity namespace: " +
                $"{string.Join(", ", sharedEntityIds)}.");
        }

        if (actorIds.Contains(BoardIds.LockedChest) || objectIds.Contains(BoardIds.LockedChest))
        {
            throw new InvalidOperationException(
                $"Spatial entity identifier '{BoardIds.LockedChest}' is reserved for the synthetic chest.");
        }

        RequireIds(
            placeIds,
            "place",
            BoardIds.Tavern,
            BoardIds.Market,
            BoardIds.CellarGate,
            BoardIds.Cellar);
        RequireIds(actorIds, "actor", BoardIds.Alice, BoardIds.Bob);
        RequireIds(
            objectIds,
            "object",
            BoardIds.BrassKey,
            BoardIds.DuchessLetter,
            BoardIds.SilverCoinOne,
            BoardIds.SilverCoinTwo);
        RequireIds(passageIds, "passage", BoardIds.CellarGatePassage);

        ScenarioPassageDefinition cellarGate = Passages.Single(
            passage => passage.Id == BoardIds.CellarGatePassage);
        if (cellarGate.EndpointAId != BoardIds.CellarGate ||
            cellarGate.EndpointBId != BoardIds.Cellar)
        {
            throw new InvalidOperationException(
                $"Passage '{BoardIds.CellarGatePassage}' must run from " +
                $"'{BoardIds.CellarGate}' (A) to '{BoardIds.Cellar}' (B).");
        }

        foreach (ScenarioPassageDefinition passage in Passages)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(passage.EndpointAId);
            ArgumentException.ThrowIfNullOrWhiteSpace(passage.EndpointBId);
            if (!placeIds.Contains(passage.EndpointAId) ||
                !placeIds.Contains(passage.EndpointBId))
            {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' references an unknown endpoint.");
            }

            if (passage.EndpointAId == passage.EndpointBId)
            {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' endpoints must differ.");
            }

            if (passage.Length <= 0)
            {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' length must be positive.");
            }

            if (passage.RequiredTicketObjectId is string ticketId &&
                !objectIds.Contains(ticketId))
            {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' requires unknown ticket '{ticketId}'.");
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
            if (item.InitialPlaceId is not null && item.InitialOwnerActorId is not null)
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' cannot start both placed and owned.");
            }

            if (item.InitialPlaceId is string placeId && !placeIds.Contains(placeId))
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' starts at unknown place '{placeId}'.");
            }

            if (item.InitialOwnerActorId is string actorId && !actorIds.Contains(actorId))
            {
                throw new InvalidOperationException(
                    $"Object '{item.Id}' starts with unknown owner '{actorId}'.");
            }
        }
    }

    private static ScenarioDefinition CreateDefault() =>
        new(
            "firstboard.duchess-letter-market",
            Revision: 2,
            FirstBoardRuleset,
            BoardTiming.DeadlineTicks,
            Array.AsReadOnly<ScenarioPlaceDefinition>(
            [
                new(BoardIds.Tavern),
                new(BoardIds.Market),
                new(BoardIds.CellarGate),
                new(BoardIds.Cellar),
            ]),
            Array.AsReadOnly<ScenarioPassageDefinition>(
            [
                new(
                    BoardIds.TavernMarketRoad,
                    BoardIds.Tavern,
                    BoardIds.Market,
                    Length: 300_000,
                    EnterableFromA: true,
                    EnterableFromB: true),
                new(
                    BoardIds.TavernMarketFerry,
                    BoardIds.Tavern,
                    BoardIds.Market,
                    Length: 180_000,
                    EnterableFromA: true,
                    EnterableFromB: true,
                    RequiredTicketObjectId: BoardIds.SilverCoinOne),
                new(
                    BoardIds.MarketTavernCart,
                    BoardIds.Market,
                    BoardIds.Tavern,
                    Length: 240_000,
                    EnterableFromA: true,
                    EnterableFromB: false),
                new(
                    BoardIds.MarketCellarApproach,
                    BoardIds.Market,
                    BoardIds.CellarGate,
                    Length: 180_000,
                    EnterableFromA: true,
                    EnterableFromB: true),
                new(
                    BoardIds.CellarGatePassage,
                    BoardIds.CellarGate,
                    BoardIds.Cellar,
                    Length: 120_000,
                    EnterableFromA: true,
                    EnterableFromB: true),
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
                                "公爵夫人的密信可能锁在地窖箱中；黄铜钥匙最近在集市出现；地窖门口公告称一小时后封闭。"),
                            new(
                                "alice.meeting-note",
                                "爱丽丝昨夜与鲍勃谈过后留下的会面备忘",
                                "先去集市会面；若钥匙和鲍勃都不在，至少等待五分钟。"),
                        ]),
                        DefaultMemory(
                            "我在酒馆，准备去集市寻找钥匙和鲍勃。",
                            "若在集市与鲍勃错身，至少等五分钟；情况变化时可修改计划。",
                            "钥匙很可能与地窖箱有关，但传闻尚未核实。",
                            "我对鲍勃保持戒备，但仍把他视为潜在合作对象。"))),
                new(
                    BoardIds.Bob,
                    BoardIds.Market,
                    new ScenarioRoleDefinition(
                        "鲍勃",
                        "务实、机会主义，但并非冷酷；喜欢掌握谈判筹码",
                        "利用黄铜钥匙和密信线索取得收益，同时避免无法兑现的交易",
                        "直率，偶尔讥讽，谈条件时毫不含糊",
                        Array.AsReadOnly<ScenarioReferenceMaterialDefinition>(
                        [
                            new(
                                "bob.lead-ledger",
                                "鲍勃自己的生意账本边角记录",
                                "摊位附近可能有一把黄铜钥匙；爱丽丝正在追查地窖中的密信；地窖一小时后封闭。"),
                            new(
                                "bob.meeting-note",
                                "鲍勃记下的昨夜会面安排",
                                "若先拿钥匙去地窖，开箱后回集市至少等待十分钟。"),
                        ]),
                        DefaultMemory(
                            "我在集市，准备先确认钥匙是否还在。",
                            "若先去地窖开箱，之后回集市至少等爱丽丝十分钟。",
                            "钥匙可能让我取得密信筹码，但拖得太久可能一无所获。",
                            "我把爱丽丝视为潜在交易对象而不是同盟。"))),
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
            new("working_context", "当前处境与未决线索", "维护当前处境与近期变化。", working),
            new("commitments", "承诺与计划", "维护未完成约定、期限和多步计划。", commitments),
            new("beliefs", "判断与假说", "区分材料、他人说法和自己的判断。", beliefs),
            new("relationships", "关系与社会账本", "缓慢维护信任、戒备和债务。", relationships),
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
        foreach (ScenarioPlaceDefinition place in Places.OrderBy(
                     place => place.Id,
                     StringComparer.Ordinal))
        {
            writer.WriteStringValue(place.Id);
        }

        writer.WriteEndArray();
    }

    private void WritePassages(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("passages");
        foreach (ScenarioPassageDefinition passage in Passages.OrderBy(
                     passage => passage.Id,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", passage.Id);
            writer.WriteString("endpointAId", passage.EndpointAId);
            writer.WriteString("endpointBId", passage.EndpointBId);
            writer.WriteNumber("length", passage.Length);
            writer.WriteBoolean("enterableFromA", passage.EnterableFromA);
            writer.WriteBoolean("enterableFromB", passage.EnterableFromB);
            if (passage.RequiredTicketObjectId is null)
            {
                writer.WriteNull("requiredTicketObjectId");
            }
            else
            {
                writer.WriteString("requiredTicketObjectId", passage.RequiredTicketObjectId);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private void WriteActors(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("actors");
        foreach (ScenarioActorDefinition actor in Actors.OrderBy(
                     actor => actor.Id,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", actor.Id);
            writer.WriteString("initialPlaceId", actor.InitialPlaceId);
            writer.WriteString("name", actor.Role.Name);
            writer.WriteString("traits", actor.Role.Traits);
            writer.WriteString("goal", actor.Role.Goal);
            writer.WriteString("voice", actor.Role.Voice);
            writer.WriteStartArray("referenceMaterials");
            foreach (ScenarioReferenceMaterialDefinition material in actor.Role.ReferenceMaterials
                         .OrderBy(material => material.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", material.Id);
                writer.WriteString("source", material.Source);
                writer.WriteString("content", material.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("initialMemoryShards");
            foreach (ScenarioMemoryShardDefinition shard in actor.Role.InitialMemoryShards
                         .OrderBy(shard => shard.Key, StringComparer.Ordinal))
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
        }

        writer.WriteEndArray();
    }

    private void WriteObjects(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("objects");
        foreach (ScenarioObjectDefinition item in Objects.OrderBy(
                     item => item.Id,
                     StringComparer.Ordinal))
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

    private static BoardActor NewActor(long id, string key) =>
        new(
            id,
            key,
            Generation: 0,
            DecisionSequence: 0,
            Activity: null,
            KnownFacts: []);
}

public sealed record ScenarioInstance
{
    public ScenarioInstance(ScenarioDefinition definition, ulong worldSeed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition.Freeze();
        Graph = Definition.CreateGraphDefinition();
        WorldSeed = worldSeed;
        DefinitionSha256 = Definition.ComputeSha256();
        InstanceSha256 = ComputeInstanceSha256(DefinitionSha256, worldSeed);
    }

    public ScenarioDefinition Definition { get; }
    public GraphDefinition Graph { get; }
    public ulong WorldSeed { get; }
    public string DefinitionSha256 { get; }
    public string InstanceSha256 { get; }

    public string Id => FormattableString.Invariant(
        $"{Definition.Id}@{Definition.Revision}/{DefinitionSha256[..12]}/seed-{WorldSeed}");

    public static ScenarioInstance CreateDefault(ulong worldSeed) =>
        new(ScenarioDefinition.Default, worldSeed);

    public FirstBoardWorld CreateInitialWorld() =>
        Definition.CreateInitialWorld(WorldSeed);

    private static string ComputeInstanceSha256(string definitionSha256, ulong worldSeed)
    {
        string canonical = FormattableString.Invariant(
            $"dramaboard.scenario-instance/2\n{definitionSha256}\n{worldSeed}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
