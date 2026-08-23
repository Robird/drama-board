# Build Log 0004：编译式 Game Content 与可继续 Save 的目标边界

> 状态：**Target design re-adjudicated; implementation consolidated**
>
> 记录日期：2026-08-23
>
> 代码基线：`994eeb4 docs(content): define minimal content and save boundary`
>
> 上位设计：[Design Note 008：Graph Spatial World](../开放世界棋盘游戏设计_008_Graph_Spatial_World.md)
>
> 核心裁决：**C# Content 构造封闭 Game Definition；Ruleset 独占行为与因果；Runner 一次只加载一个 Content Module；Save 从终止并 join 的 Session 捕获 Definition、完整 Journal 与 closed Player composition。每次 new run 使用 fresh root lineage，每次 resume 使用 fresh child lineage。**

## 1. 当前产品决定

DramaBoard 下一阶段需要让同一 Engine、Ruleset 与美术资产运行多个具体游戏原型。当前冻结：

- C# 是唯一 Content 作者语言，直接复用编译器、IDE、重构、测试与静态分析工具链；
- 每个 Prototype 编译为独立 DLL，但不能注册或替换 Ruleset Law；
- 一个公共 Runner 接收 Content Pack 目录并启动目标游戏；
- 至少用两个共享同一 Ruleset、作者 ID 完全不相交的 Prototype 验伪；
- Content Pack 与 Save 是独立目录；resume 不再访问原 Pack；
- 当前部署模型是可信、本地、单进程、一次只加载一个 Pack；
- 当前 live Session 继续使用 `InMemoryJournal`。只有终止并 join 后的 immutable capture 才能写 Save；
- Save 是可复制、可多次恢复的 immutable checkpoint。每次恢复都产生新的 child lineage 和 successor Save；
- V1 只承诺 fail-closed publication：旧 Save 永不修改，新目标要么完整可验证，要么被拒绝。不承诺断电后的最新进度或跨文件 fsync durability；
- Runner 保留不同 Actor 使用不同 decision backend/model 的现有能力，只把 Alice/Bob 固定槽改成 ActorId-keyed override；
- 不要求第三方 Mod 沙箱、热加载、卸载、同进程换包、私有依赖隔离、旧格式迁移或独立 Objective Replay 产品格式。

目标进程形态是：

```text
dramaboard run --content <pack-dir> --save <new-save-dir>

               one Runner EXE
                      │
          load exactly one Content Module
              ┌───────┴───────┐
              │               │
       Prototype A DLL  Prototype B DLL
              └──── same compiled Ruleset ────┘
```

“两个 Prototype DLL”不表示进程中只有两个 DLL；Kernel、Spatial、Player、Ruleset 等共享程序集仍正常存在。它表示具体 Prototype 不再编译进 Runner，也不要求修改 Runner 或 Ruleset 才能被选择。

## 2. 统一术语

| 术语 | 精确定义 |
|---|---|
| **Engine** | Kernel、Spatial、Protocol、Player、Journal 与 Host primitives 等跨游戏基础 Law。 |
| **Ruleset** | 受信任的编译期 C# 游戏规则：World state、action、fact、Forecast、reducer、validator、observation 与 codec。 |
| **Prototype** | 一个具体可玩的角色与戏剧结构；它是产品概念，不是 Runtime 接口。 |
| **Content Source** | 构造 Prototype 的 C# 源码及其测试。 |
| **Content Module** | Content Source 编译得到的入口 DLL；只负责物化 Game Definition。 |
| **Content Pack** | Runner 接收的部署目录，包含 bootstrap manifest、一个 Content Module 及可选 Pack-owned assets。 |
| **Content Provider** | Content Module 中唯一的短命入口对象；只返回 Game Definition。 |
| **Game Definition** | 经验证、冻结并 canonicalize 的完整语义对象图；描述新 lineage 的初始世界与角色前提。 |
| **Game Instance** | `Game Definition + WorldSeed` 的不可变 genesis identity。 |
| **Game Session** | 一次正在运行的 Instance：Objective World、Journal、Players、Presentation 与资源生命周期。 |
| **Runner** | 唯一 EXE / composition root；加载 Pack、组合 Players、启动 Session、写 Save、恢复 Save。 |
| **Objective component** | Save 内部的 Definition + sealed Journal 组件及其 production verifier；不是独立产品格式。 |
| **Resumable Save** | 能恢复客观世界、完整 Player composition 与下一次输入，并创建 successor Session branch 的 immutable checkpoint。 |

“Scenario”仍可表示某个 Game 内的局部剧本或开局方案。当前 Pack 定义整个 Prototype；`GameDefinition` 是目标术语，但 CLR/project rename 不阻塞功能路线。

## 3. 权威与依赖方向

```text
Kernel / Spatial / Player / Host / Journal
                    ↑
        DramaBoard.Rulesets.FirstBoard
        state / laws / Definition / codecs
                    ↑
      ┌─────────────┴──────────────┐
      │                            │
Content Module DLL    DramaBoard.FirstBoard.Runner EXE
      │                            │
      └──── runtime load once ─────┘
```

依赖保持单向：

- Runner 与 Content Module 都编译依赖 Ruleset；
- Runner 不 `ProjectReference` 具体 Content Module；
- Content Module 不引用 Runner；
- Engine / Ruleset 不反向引用具体 Content；
- Content Module 不能注册 reducer、codec、Player factory、service 或 Kernel rule。

V1 只有一个真实 Ruleset，Content contract 直接属于 `Rulesets.FirstBoard`。不建立 Engine-level `IGamePlugin`、通用 Ruleset registry 或 `Content.Abstractions` 项目。

Authority 分层：

- **Engine**：Kernel winner、LogicalInstant、有序 atomic batch、Replay / Fork、Graph Spatial、Player / Protocol 端口、Journal 与 Host primitives；
- **Ruleset**：action/fact vocabulary、occurrence law、reducer、validator、Game + Spatial 原子规划、observation、production codec，以及 typed Ruleset binding 的解释；
- **Content**：实体与初态、stable author IDs、显示文本、角色材料、初始 Memory，以及 Ruleset 已声明的 typed bindings；
- **Save lineage**：frozen Definition、selected committed Journal chain、closed Player composition 与精确 continuation frontier。它不拥有并列可写 World snapshot。

## 4. Content Pack 是部署包装，不是语义格式

普通 `dotnet build` 输出目录可直接作为 Pack：

```text
ContentPack/
  content-pack.json
  DramaBoard.Content.DuchessLetterMarket.dll
  assets/                                      # 有真实消费者后才出现
```

`content-pack.json` 只有：

```json
{
  "format": "dramaboard.content-pack/1",
  "entryAssembly": "DramaBoard.Content.DuchessLetterMarket.dll"
}
```

它只解决入口定位。`contentId / revision / rulesetId / semantic hash / entities / bindings / Save` 都不进入 bootstrap manifest。manifest 指向不同 DLL，而两个 Provider 构造出相同 canonical Definition 时，语义身份相同。

Runner 在装载代码前验证：

- `format` 精确匹配；
- `entryAssembly` 是 non-rooted、无 `..`、无目录分隔符的 `.dll` basename；
- regular file 精确位于 Pack 根目录；
- 未知属性、overlay、环境替换、递归 discovery 均拒绝。

V1 不支持 include、patch、merge order、platform variants、signature、DLL catalog 或 Pack 组合。

## 5. C# Content authoring contract

Ruleset 暴露窄接口：

```csharp
public interface IFirstBoardContentProvider
{
    ScenarioDefinition CreateDefinition(); // CLR rename 延期；语义上是 Game Definition
}
```

Content Module 恰好导出一个 public、concrete、closed、具有 public parameterless constructor 的 Provider。Runner 创建一次、调用一次，随后丢弃 Provider；它不进入 Game Instance、Session、Kernel、Player 或 Save。

返回值是 sealed、closed-data Definition。对象图禁止包含 delegate、strategy、callback、Ruleset/codec、Player factory、service registry、时钟、随机源、句柄、网络客户端或 mutable Runtime service。

Content C# 是可信 authoring executable，不是安全沙箱。作者约定 `CreateDefinition()` context-free、deterministic、无可观察副作用；Content 项目用两次独立构造的 canonical bytes/hash exact equality 做 smoke test。production loader 仍只调用一次。该测试不是纯度证明。

## 6. Runtime loader 的最小边界

V1 使用 `AssemblyLoadContext.Default.LoadFromAssemblyPath`：

1. Runner 已加载 Ruleset 与 Provider contract；
2. 读取并验证 bootstrap manifest；
3. 将入口 DLL 加入 Default ALC；
4. 找到恰好一个有效 Provider；
5. 对 0/2 Provider、构造或调用失败、null Definition 稳定拒绝；
6. 立即 `Validate → Freeze → canonicalize → SHA-256`；
7. 此后 Runtime 不再访问 Pack 或 Provider。

部署约束：

- 一个进程只加载一个 Pack；A/B 验收使用不同子进程；
- Content Module assembly simple name 唯一；
- Pack 只依赖 BCL 与 Runner 已拥有的共享 Ruleset assemblies；
- Pack 不携带第二份 contract/Ruleset，也没有私有 managed/native dependencies；
- 不 unload、不 hot reload、不在同进程切换 Pack；
- stale binary 统一要求用当前 Runner/Ruleset rebuild，不承诺 binary compatibility。

只有 Pack-private dependencies、同进程多 Pack 或独立二进制兼容成为真实需求后，才重开 custom ALC + `AssemblyDependencyResolver`。untrusted Content 必须使用进程隔离；ALC 不是安全边界。

所有实际执行 `LoadFromAssemblyPath` 或 Provider 的测试都必须在 fresh one-shot child process；纯 manifest/path parser 才留在 xUnit 进程。

## 7. Definition、identity 与 compatibility

新 run：

```text
C# Content Source
→ build Pack
→ CreateDefinition once
→ Validate / Freeze / canonical order
→ exact canonical game-definition.json
→ DefinitionSha256
→ fresh root LineageId + WorldSeed
→ Game Session
```

Definition hash 覆盖所有会改变世界或 Player 决策的 **Content-owned** 材料：Graph、初态、typed bindings、Role、ReferenceMaterial、initial Memory，以及 Content 提供给 Observation、Prompt 或 Presentation 的语义文本。Ruleset-owned template 与解释行为由 `RulesetId` 绑定，不假装进入 Definition SHA。纯贴图、立绘、音频不进入；会改变 visibility、affordance 或信息获得的 asset metadata 必须进入。

DLL bytes、MVID、assembly version、source commit 只作 provenance，不能成为 Save 的语义身份。

各兼容 ID 只有一个 owner：

| 边界 | Authority | 不兼容时变化 |
|---|---|---|
| Content Pack bootstrap | Runner 的 `dramaboard.content-pack/1` 常量 | manifest/入口/path 解释 |
| Definition wire | Ruleset codec；root `schema` 就是 codec ID | 字段、canonical encoding、restore mapping |
| Fact payload wire | FirstBoard fact codec；Save 声明 expected ID，每个 envelope 镜像 | closed union/kind/payload mapping |
| Journal envelope | Atelia physical format + versioned opaque frame kind | envelope 或 lineage metadata shape |
| Ruleset semantics | Definition 的 `RulesetId` | Genesis、fact meaning、fold、validation、Forecast、Plan、Observation 或续局未来行为 |
| Player checkpoint wire | checkpoint codec constant | private state wire/restore mapping |
| Player composition behavior | versioned `playerCompositionId` | 相同 request/config/state 的 next request、Prompt 或 wrapper behavior |
| Save package | Runner 的 `dramaboard.save/1` | manifest、目录、publication/restore contract |

Definition `Id/Revision` 是作者 metadata，不是 codec 或 Ruleset gate。software/git version 只供审计，不替代 compatibility ID。历史 build log 0001/0002 的“不因运行时能力自动 bump”属于 production Save 出现前的 current-build-only 阶段；本节从首个 resumable Save contract 起前瞻生效。V1 不迁移，只接受当前明确支持的 ID。

## 8. Typed Ruleset bindings 是 Content 化核心

必须区分：

```text
Ruleset-owned kinds
    action / fact / event / internal state concept

Author-owned identities
    ActorId / PlaceId / PassageId / ObjectId

Typed Ruleset bindings
    compiled Ruleset 所需的封闭语义槽位
```

首版不建立 predicate/effect DSL、ECS 或 property bag。一个 sealed `FirstBoardRulesetConfig` 至少拥有：

```text
DeadlineMs
DeadlinePassageId
LockedContainerObjectId
UnlockingObjectId
RevealedObjectId
RevealedObjectAuthenticityText
RevealedObjectContentsText
```

最后两个文本分别供“真伪知识”和“内容知识”两个独立 Ruleset facts 使用；普通可见、开箱、deadline、拒绝与 report 文本由实体 label/description 加 Ruleset template 生成。Content 不能注册 fact kind 或任意 prose bag。

synthetic locked chest 必须进入普通 Object definitions 与 placement。Ruleset 不再从 `alice / cellar / brass-key / duchess-letter` 等字面值推断语义。内部状态可暂时叫 `ChestOpened / CellarSealed`，因为它们是 Ruleset concept；结构化 fact kind 应改成 content-neutral Ruleset 名称。

Place、Passage、Actor、Object 拥有 Content-defined display name 与必要 description。所有 Player-visible semantic text 都进入 canonical Definition。

## 9. Content-neutral Runner 与 Player composition

Runner 仍只跨 Content Pack，不跨 Ruleset。它直接了解 FirstBoard World、Fact、Presentation projection 与 codecs，不建立泛型 `IGameRuleset` framework。

Options 两阶段绑定：

```text
parse syntax/defaults/repeated overrides
→ load Definition
→ ordinal-exact bind ActorId
→ reject duplicate/unknown/Human-slot override
→ resolve every slot before backend creation
```

最小 CLI：

```text
dramaboard content validate --content <pack-dir>
dramaboard run --content <pack-dir> --save <new-save-dir>
    [--human <actorId>]
    [--backend <backend> --model <model>]
    [--actor-llm <actorId> <backend> <model>]...
dramaboard resume --save <source> --out-save <successor> --until-ms <absolute-boundary>
```

公共 backend/model 是默认值；`--actor-llm` 保留当前 mixed-model 能力。Memory backend/model 仍是进程级 resolved config。manifest/Save 记录最终每个 Actor 的 closed composition，不记录“来自 default 还是 override”。resume 的 `--until-ms` 是本次 invocation 的绝对 ModelTime 停止边界，必须不早于 restored current ModelTime；它不改变保存的 Player composition。

生产 CLI 暂不新增 random mode。A/B session smoke 由 one-shot test probe 复用 production Runner pipeline，并在唯一 composition seam 注入完整的 roster-keyed `ResolvedPlayerSlot` plan：descriptor 与 factory 都声明 deterministic `Random(seed)`。不能只把已解析为 LLM 的 slot 偷换成 Random factory，否则 manifest/report/后续 checkpoint 会谎报实际执行者；probe 仍不得绕过 manifest、report、Presentation 或 cleanup surface。

Runner Presentation/report 可以保持 FirstBoard-specific，但必须从 Definition 枚举 roster 与 labels，不认识 Pack A author IDs。

## 10. Naming cleanup 不阻塞功能

目标词汇仍是：

```text
ScenarioDefinition    → GameDefinition
ScenarioInstance      → GameInstance
FirstBoardScenario    → FirstBoardRuleset
FirstBoard.Demo       → FirstBoard.Runner
```

但 CLR、project、folder、assembly rename 没有运行时消费者，不能作为 codec、checkpoint 或 Save 的 gate。`scenario-definition.json → game-definition.json` 属于 0012 的 artifact contract，不伪装成机械 rename。其余 rename 是可延期 leaf，最迟在首个外部 API/格式冻结前或第二 Ruleset 出现前完成。

wire/codec/composition IDs 必须是显式稳定字符串，不得来自 CLR type、namespace、assembly-qualified name 或项目路径。

## 11. 当前代码与目标差距

已有：

- C# frozen Definition、canonical bytes/hash 与 seed identity；
- Kernel complete ordered batches、Replay/Fork 与 `WorldVersion`；
- production `InMemoryJournal` live path；
- Atelia adapter 及 persistence tests；
- LLM Memory、previous-known-facts 与 pipelined maintenance；
- mixed decision backends、Memory backend、Presentation frontier 与取消语义。

真实缺口：

1. Ruleset、Runner、Presentation/report 仍直接消费 Pack A IDs 与文本；
2. 尚无 Content Provider、真实 Pack 与 runtime loader；
3. Definition/fact production codecs 与 strict reader 尚未形成；
4. Atelia 默认依赖仓库外 sibling，且没有 strict read-only Save reader；
5. capture 仍暴露 live `InMemoryJournal`，尚无 terminal immutable snapshot/export；
6. Player checkpoint 与 Runner-owned slot guard 尚未形成；
7. production lineage 仍是固定常量，新 run 与 resume 都没有 fresh allocation；
8. Live Session、coordination 与 Presentation 尚不能从非零 verified frontier 启动。

live 使用 InMemory 不是缺口。只有出现 crash recovery/autosave、实时 tail、超长局内存压力或 O(n) final export 成本不可接受时，才重开 live durable sink。

## 12. Save 的唯一 authority

### 12.1 目录与最小 manifest

```text
Save/
  save.json                  # recognition marker; written last
  game-definition.json       # exact canonical bytes
  journal/                   # sealed package-local main
  players/0001.json          # opaque safe filenames
  reports/                   # optional, non-authoritative
```

`save.json` 最小拥有：

```text
format = dramaboard.save/1
definitionSha256
worldSeed
selectedJournalChainSha256
payloadCodecId
playerCompositionId
players[]:
  actorId
  closed driver union + canonical non-secret resolved config
  capturedDecisionSequence
  checkpoint? = checkpointCodecId / opaqueFilename / checkpointFileSha256
```

Definition canonical SHA 同时是 exact file digest 与 semantic identity。Player checkpoint 是 canonical closed bytes，其 SHA 同时承担 file integrity 与 exact state comparison；不再持久化第二个 `PlayerStateFingerprint`。roster/composition fingerprint 都是可派生诊断，不是 authority。

`contentId/revision/rulesetId/definition codec` 从 Definition 派生；`lineageId/parent frontier/transitionCount/last instant` 从 checked Journal chain 派生；driver variant/stateful 由 `playerCompositionId` 所定义的 closed union 与 checkpoint presence 表达。固定 branch `main`、physical EventAddress head、batchCount、whole-directory digest 都不进入 manifest。

### 12.2 selected Journal chain

Atelia `EventAddress` 是物理地址，不是 content hash。Save 的 Journal frontier 是：

```text
fixed package-local main
+ checked chronological traversal from its actual ref head
+ selectedJournalChainSha256
```

chain digest 采用 versioned、domain-separated、length-prefixed 编码，按顺序覆盖每一帧的 opaque kind 与 exact logical payload bytes，包括 lineage metadata 和完整 batch envelope。它不覆盖 orphan、inactive ref、reflog、cache、压缩或物理 layout。

因此 selected tail、截断、重排、metadata/envelope replacement 都改变 digest；合法 physical rewrite 不改变语义 identity。batch 顺序、`LogicalInstant`、`CandidateKey` 与 `Facts[]` 原始数组顺序必须逐层保留，绝不能 canonical-sort Journal 因果顺序。

V1 Journal 恰有一个 active `main`。selected chain 必须以 batch boundary 0 的唯一 root `LineageCreatedV1` 开始，其 `ParentWorldVersion=null`；每次 resume 在 exact inherited prefix boundary 插入 fresh child metadata，显式携带 `ParentLineageId + ParentTransitionCount`。parent count 使用与 `WorldVersion.TransitionCount` 一致的 `long`，相邻 lineage metadata 必须验证 parent ID 与 prefix count。

Save reader 必须 strict read-only existing：不创建 Journal、branch、metadata 或 orphan，不 advance ref，不返回 writable sink；成功与失败都不修改 source bytes。当前 `OpenOrCreate` sink 不能冒充 verifier。

### 12.3 Restore 顺序

1. strict parse `save.json`，验证固定路径、Definition/Player raw SHA、known `playerCompositionId` 与 checkpoint codec；
2. peek Definition root schema，strict decode、Validate、Freeze、canonical re-encode，验证 exact SHA 与 supported `RulesetId`；
3. read-only inspect actual `main` checked chain，验证 frame kinds、lineage ancestry、expected payload codec 与 chain SHA；derive active lineage、transition count 和 last instant；
4. 用 decoded Definition 验证 exact Actor slot set、resolved config、slot binding 与 Player payload；
5. 使用 production codec decode ordered batches，并通过 `SimulationReplay` 或等价完整 batch law fold；
6. 以 folded World 的 `Actor.DecisionSequence` 作为唯一 authority，验证 slot 的 captured binding；
7. import Players，返回 verified capture。Provider、Pack、backend request 与 Session 启动次数均为 0。

envelope codec mismatch 必须在把该 payload 交给 `TFact` decoder 前拒绝。整个 restore 是否完全零 decoder call 只是内部 layering test，不是产品语义；所有结构/身份错误仍必须在 fold、Player import 或可变状态发布前拒绝。

### 12.4 Player checkpoint 与 clean frontier

LLM checkpoint 的 persistent mutable closure 只有：

```text
Memory shard contents
previous-known-facts frontier
```

CharacterCard、ReferenceMaterial 与 Memory schema 来自 frozen Definition；Actor、resolved backend/model/maintenance config 与 wrapper order 来自 slot descriptor。payload 用派生 `slotBindingSha256` 防串槽，不复制这些 authority。

closed composition 是 versioned union：`Human | Random(seed) | Null | Llm(config, checkpoint)` 加 ordered wrappers。`ScriptedPlayerDriver` 是 test-only，不属于 resumable union。`DecisionBudget` 在 V1 从 committed World decision sequence 与 `maxTurns` 派生并与 live counters交叉验证；未来允许动态装卸 wrapper 时才持久化独立 counters。

只检查 `pending maintenance == null` 不够。当前 Kernel 可能发生：

```text
Player 已返回并更新私有状态
→ cancellation / publication failure 发生在 Journal commit 前
→ World.DecisionSequence 未前进
```

Runner-owned actor-slot guard 必须 single-flight，记录已返回 decision frontier，并在 capture 时与 folded World sequence 对齐。任一 Player、maintenance 或 trace fault 后不得发布 Save。

V1 只从 terminal immutable capture 保存：

```text
Authority 停止并 join；无 in-flight Step / Player request
→ Presentation drain 并 join；P == C
→ Flush 全部 Player maintenance
→ 验证 slot/world frontier、wrapper invariant 与 healthy state
→ 冻结 World、ordered batches 与 Player payloads
```

### 12.5 Fail-closed publication

```text
要求 final path 不存在
→ 在同卷唯一 sibling staging 写 components
→ close writers，计算 SHA/digest
→ 最后写并关闭 save.json
→ 用正式 read-only reader self-verify staging
→ rename staging 到 final path
```

`save.json` 是 recognition marker，不是多文件 filesystem transaction。final rename 前故障只留下 unpublished staging；source/已有 Save 永不 writable。final 存在但验证失败时 reader fail-closed。应以 writer subprocess 在各 barrier 被 kill 的测试证明 process-failure contract，而不只做异常注入。

V1 不承诺 successful return 后立即断电仍保留 Save-B，也不承诺 latest-progress recovery。跨组件 `Flush(true)`、目录 fsync、平台 filesystem 语义与 power-cut harness 等有真实需求后再设计。

### 12.6 Resume 与 successor lineage

Save 可复制、可多次 resume，因此每次恢复必须分配 fresh child lineage。不能让两个不同 suffix 都拥有相同 `(LineageId, TransitionCount)`。

```text
Save-A active version = (L0, N)
resume A → B: child initial version = (L1, N)
resume A → C: child initial version = (L2, N)
L0 != L1 != L2
```

child ID 使用不可由 parent frontier 唯一决定的 collision-resistant fresh `long`；测试可注入固定 allocator。产品命令仍叫 `resume`，但 identity 语义是从 saved frontier 创建 successor branch。lineage metadata 不是 Objective transition，count 仍为 N。

resume-aware Session 初始化：

```text
Kernel = folded World + child WorldVersion(Lchild,N)
       + inherited ordered batches + inherited last LogicalInstant
Coordination C = P = (Lchild,N)
Presentation replay baseline = folded World + inherited last instant
channel only carries suffix N+1...
```

旧 Presentation cue 不重播。第一项新 commit 必须是 child `(Lchild,N+1)`。Session 继续使用 InMemory Journal；终止并 join 后再把 inherited logical chain、child metadata 与新 suffix 导出成 self-contained successor Save。Save-A 与原 Pack 均不修改。

uninterrupted control 与 save/reopen/continue 比较 Definition、seed、inherited batch envelopes、folded/final World、last instant、Player state、exact next request/Prompt 和 deterministic fake-backend suffix；明确不比较 lineage、physical EventAddress/head、raw journal bytes、staging path或真实 LLM 后续输出。

## 13. 双 Prototype 防伪验收

Pack B 复用完全相同的 FirstBoard Ruleset 能力骨架，但 Actor/Place/Passage/Object IDs 全部与 Pack A 不相交。它不能增加 action、fact、第三名角色、脚本、第二 Ruleset 或新世界 Law。

两层验收：

1. 内存态 B Definition 在第一个 Ruleset vertical 中完成 decision、travel、deadline、Use/container/reveal、inspect、Journal/replay exact trace；
2. 真实 A/B Content Module 在不同 one-shot 子进程通过同一个 production loader；test probe 复用 production post-bind Runner pipeline，注入 deterministic Random Players，跑 manifest、Presentation、report 与 cleanup。

结构化 author-ID fields 对另一 Pack 的 ID 集零命中；自然语言不做 raw substring 禁令，但所有 Player-visible文本必须来自 Pack B Definition 或 content-neutral Ruleset template。删除或替换 Pack B DLL 后，Save restore 对 Pack/Provider访问为 0。

## 14. 可证伪验收矩阵

| ID | 必须证明 |
|---|---|
| CNT-1 | typed config + Genesis + Ruleset + Observation + reducer 在一个 vertical 中移除 Pack A identity；内存 B exact trace 通过。 |
| CNT-2 | 真伪/内容两个 typed prose slot 独立进入 fact、canonical bytes 与 hash。 |
| PAK-1 | manifest/path 在 load 前拒绝；所有 assembly activation case 使用 fresh process。 |
| PAK-2 | 真实 Pack A build output 被 production CLI 激活，Runner 与 Pack 无静态反向引用。 |
| RUN-1 | Runner 从 Definition 枚举 roster/labels，ActorId override 保留 mixed-model 能力。 |
| RUN-2 | 真实 Pack B 经 production loader 和 post-bind pipeline 推进，结构化 author IDs 对 A 零命中。 |
| DEF-1 | Definition keyed collection reorder 不改 canonical bytes/hash；semantic field mutation 必改。 |
| COD-1 | Definition/fact/checkpoint codecs strict self-describing；wire 与 semantic compatibility IDs 各自唯一。 |
| OBJ-1 | sealed main checked chain 与 chain SHA 恢复 exact ordered batches/World；source bytes不变。 |
| OBJ-2 | Pack/Provider/Player/backend 调用为 0；wrong codec/digest/lineage ancestry 在 fold/import 前拒绝。 |
| PLY-1 | Memory + previous-known-facts round-trip；same supplied request 的 Prompt exact。 |
| PLY-2 | driver-returned-before-Journal-cancel、pending maintenance、faulted slot 或 world/slot sequence mismatch 均拒绝 capture。 |
| SAV-1 | terminal/joined capture 经 sibling staging、manifest-last、self-verify、rename 后 valid-or-reject；旧 Save不变。 |
| SAV-2 | 同一 Save 两次 resume 得到不同 sibling lineage；nonzero C=P baseline 和 suffix-only Presentation 正确。 |
| SAV-3 | deterministic fake backend 下 uninterrupted 与 resume 的 logical suffix、World、Player next input exact；不要求真实 LLM 输出相同。 |

## 15. 实施路线

路线保留可审阅 commit 边界，但以四个可证伪里程碑组织，不再让未消费 scaffold 或机械 rename 成为全局 gate。

### Milestone A：Ruleset 真正 content-neutral

1. [Build Log 0005：Game Definition、typed bindings 与 Ruleset 中立化](0005-game-definition-and-ruleset-bindings.md)
2. [Build Log 0007：真实 Pack A 与 one-shot loader](0007-content-pack-contract-and-loader-probe.md)
3. [Build Log 0009：Content-neutral Runner 与 Pack B](0009-content-neutral-runner.md)

### Milestone B：Objective Save component

4. [Build Log 0012：Game Definition production codec](0012-game-definition-codec.md)
5. [Build Log 0013：FirstBoard Fact production codec](0013-firstboard-fact-codec.md)
6. [Build Log 0014：Journal-neutral immutable capture](0014-journal-neutral-capture.md)
7. [Build Log 0015：Hermetic Atelia 与 sealed Journal export](0015-durable-atelia-journal.md)
8. [Build Log 0016：Objective Save component 与 verifier](0016-objective-replay-package.md)

### Milestone C：Closed Player continuation

9. [Build Log 0017：LLM Player checkpoint](0017-llm-player-checkpoint.md)
10. [Build Log 0018：Runner Player composition checkpoint](0018-player-composition-checkpoint.md)

### Milestone D：Composite Save 与 successor

11. [Build Log 0019：Composite Save fail-closed publication](0019-composite-save-package.md)
12. [Build Log 0020：Runner resume 与 child-lineage successor](0020-runner-resume-successor.md)

[Build Log 0011：命名清理](0011-mechanical-naming-cleanup.md) 是 non-blocking leaf，不进入主 DAG。

```text
0005 → 0007 → 0009 ───→ 0012 ──────────────────────┐
  └────→ 0013 → 0014 → 0015 ───────────────────────┴→ 0016 ─┐
0009 ────────────────┐
0017 ────────────────┴→ 0018 ─────────────────────────┤
                                                     ↓
                                                    0019 → 0020

0009 ──→ 0011a/0011b                         # optional/deferred leaf
```

0007/0009 内可以保留两个提交边界，但不能把“production 尚未消费”的中间态称为已完成 vertical。0015 的 hermetic pin 是该 slice 的硬 entry gate；失败时不得用本机 sibling checkout 冒充完成。

## 16. 最终裁决表

| Verdict | 项目 | 理由 |
|---|---|---|
| **keep** | C# Content + Ruleset-specific Provider | 最小 authoring/loader contract。 |
| **keep** | typed config + disjoint-ID B | 当前硬编码有真实 failure trace。 |
| **keep** | mixed per-Actor LLM composition | 已有代码、测试和研究消费者。 |
| **keep** | canonical Definition + complete ordered Journal + closed Player composition | 分别拥有 genesis、Objective history 与角色连续性。 |
| **keep** | fresh root/child lineage + parent metadata | 防止不同 committed prefix 得到相同 WorldVersion。 |
| **simplify** | Save manifest | 只存不可派生 binding；Definition/Player SHA 与 selected-chain SHA 各有唯一职责。 |
| **simplify** | Objective Replay | 保留 production verifier，删除第二产品格式。 |
| **merge** | 0005+0006、0007+0008、0009+0010 | 更早抵达可证伪 vertical。 |
| **defer** | live Atelia injection | 当前无 crash/autosave/tail/memory-pressure consumer。 |
| **defer** | CLR/project rename | 无运行失败，不能阻塞 codec/checkpoint/Save。 |
| **defer** | custom ALC、private dependencies、hot reload、sandbox | 当前部署模型没有消费者。 |
| **delete** | physical head/count/whole-dir digest 作为 Save authority | 物理地址不绑定逻辑 history；count 可派生。 |
| **delete** | 独立 ReplayPackage/replay.json | 只有 Composite Save 一个真实产品 consumer。 |
| **delete** | writable World snapshot、generic participant registry、Content behavior registration | 会制造第二 authority 或 speculative framework。 |

## 17. 一句话结论

> **Content 定义戏剧前提；Ruleset 定义允许的因果；Runner 组合角色并托管一次 Session；Save 在终止边界冻结 Definition、selected Journal chain 与 closed Player composition；每次 resume 从该 frontier 创建新的 child lineage，而不是伪装成同一条可唯一续写的历史。**
