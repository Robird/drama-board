# Build Log 0004：GameContent 与复合 Save 的最小边界

> 状态：**Design converged; implementation candidate**
>
> 记录日期：2026-08-23
>
> 代码基线：`7e2a7f4 docs(build-log): close live session playback slice`
>
> 上位设计：[Design Note 008：Graph Spatial World](../开放世界棋盘游戏设计_008_Graph_Spatial_World.md)
>
> 核心裁决：**Content 描述名词、初态、文本与关系结构；编译期 Ruleset 定义动词、因果与不变量；Save 绑定一个冻结的语义定义、世界 Journal 与 Player checkpoint。**

## 1. 为什么现在重开 Content 管线

DramaBoard 已经把客观空间收敛为 `Place + Passage`，并完成了移动、途中接触、AI/Human 决策与 current-format Replay 的真实竖切。下一项高价值能力不是继续扩张世界 Law，而是让同一套 Engine 与美术资产能够运行多个具体游戏原型：

```text
Shared Engine + Shared Ruleset + Shared Asset Library
                        │
                        ▼
                  GameContent/
                        │ compile / validate
                        ▼
             immutable GameDefinition
                        │ new lineage
                        ▼
                      Save/
```

用户当前的产品决定是：

- 游戏启动时加载一个 `GameContent` 目录；
- 运行状态写入另一个独立 `Save` 目录；
- 多个原型共享大体相同的世界能力与角色能力；
- 原型的核心差异来自角色塑造、情感联结，以及角色、秘密、资源与期限形成的戏剧结构；
- Content 必须容易被人和 Coding Agent 阅读、局部修改、验证与测试。

这正好满足 Design Note 008 §6.2 / §9 对内容身份的重开条件：一旦出现外部内容目录和需要继续的持久 run，就必须给 Definition 加入最小的加载、格式与绑定契约。

## 2. 当前代码已经拥有的半条管线

这不是从零建设 Content 平台。当前代码已经有很接近目标的内存模型：

- [`ScenarioDefinition`](../../src/FirstBoard/ScenarioDefinition.cs) 已包含 Graph、Actor 初始位置、Role、ReferenceMaterial、Memory shards、Object 初态与 deadline；
- `Validate → Freeze → ToCanonicalJsonUtf8 → ComputeSha256` 已形成强类型校验、冻结、canonical writer 与 Definition identity；
- [`ScenarioInstance`](../../src/FirstBoard/ScenarioDefinition.cs) 已把 Definition hash 与 WorldSeed 组合成 instance identity；
- [`DemoRunManifestWriter`](../../src/FirstBoard.Demo/DemoRunManifestWriter.cs) 已把 canonical scenario definition 和运行身份写入输出目录；
- [`AteliaJournalSink`](../../src/Journal.Atelia/AteliaJournalSink.cs) 已能持久化完整 Kernel batches 并 reopen / fork。

真正缺少的是：

1. 从目录读取 typed source 的 production loader；
2. 去掉规则、Demo roster 与叙事输出对 Alice、Bob、地窖、钥匙、锁箱和密信等作者 ID 的直接认识；
3. 把测试中的 FirstBoard fact codec 提升为 production codec；
4. 让 live run 使用 durable Journal，而不只是 `InMemoryJournal`；
5. 保存能够继续同一角色的完整 Player runtime checkpoint，而不只是人类可读的 Memory trace。

只添加一个 JSON reader、却保留上述硬编码，会得到“可调参数但不可换游戏结构”的假 Content 化。

## 3. 对 Coding Agent 的结论

Content 化本身不会增加 Coding Agent 的开发难度。Agent 真正偏好的是：

- 明确、封闭的 schema；
- stable ID 与可追踪的引用；
- path + JSON pointer 级错误；
- deterministic formatter / canonical output；
- 单命令 validation、route inspection 与 headless smoke test；
- 小而局部、不会意外修改公共 Law 的 diff。

Agent 不偏好的不是 JSON，而是三套同时存在的行为表达：

```text
C# rules
+ 配置中的任意 predicate/effect DSL
+ Content 目录中的热加载 DLL
```

一旦配置可以分支、调用、写 World 或绕过 typed planner，它就是一门缺少成熟类型系统、调试器和 Replay 约束的自制编程语言，通常比直接修改 C# 更难。

因此本轮守住一句边界：

> **Configuration becomes code when it controls execution.**
>
> **GameContent 只声明世界与戏剧前提；Ruleset 代码独占可执行因果。**

## 4. 四层 authority

### 4.1 Engine

Engine 拥有跨游戏稳定的基础 Law：

- Kernel winner、LogicalInstant、atomic batch、Replay / Fork；
- Graph Spatial 的位置、移动、接触、导航与动态入口；
- Player / Protocol 端口；
- Journal 与 Host composition primitives。

### 4.2 Compiled Ruleset

Ruleset 是受信任、随 Engine 编译和测试的 C# 代码。它拥有：

- action vocabulary 与合法性；
- occurrence rules、fact union、reducer 与 validator；
- Game + Spatial 的原子规划；
- observation / affordance projection；
- production fact codec；
- 对 Content 中 typed bindings 的解释。

V1 只有一个已知 `RulesetId`。未知 Ruleset 必须在启动时 fail-fast。现在不为未来第二个 Ruleset 抽取 registry、plugin ABI 或通用 composition framework；已有 `RulesetId` 已足够保留未来演化身份。

### 4.3 GameContent

GameContent 是新游戏 lineage 的作者源。它拥有：

- Place / Passage 与入口初值；
- Actor、Object 的 stable author-owned ID；
- Actor、Place、Passage、Object 的 Player-visible display name 与必要 description；
- 初始位置、持有关系与隐藏状态；
- 角色名、traits、goal、voice；
- 私有 ReferenceMaterial、initial Memory 与关系叙述；
- 当前 Ruleset 已支持的 typed parameters / bindings；

它描述的是“谁在哪里、拥有什么、知道什么、想要什么、受什么期限与依赖约束”，而不是一棵预写剧情树。DramaBoard 最重要的角色动力恰好可以主要由这些初态产生：

```text
目标不一致
+ 信息不对称
+ 资源与证据分散
+ 空间和期限约束
+ 可自由交谈、展示、交换、欺骗与反悔
```

V1 不先建立通用 Relationship 数值模型。信任、戒备、债务和情感可以继续以每角色私有材料与 Memory shard 表达，直到真实玩法证明至少两个规则消费者需要结构化关系数值。

### 4.4 Save lineage

Save 是某个已开始 lineage 的恢复 authority。它拥有：

- 本局冻结的 canonical semantic definition；
- committed Objective World Journal；
- stateful Player 的完整 checkpoint；
- 精确绑定这些部分的 manifest frontier。

Save 不拥有另一份可独立编辑的 World snapshot。Objective World 始终由：

```text
content.snapshot + committed journal prefix
```

重建。可选缓存或报告不是 authority。

## 5. V1 GameContent 目录

### 5.1 最小布局

当前 `ScenarioDefinition.Default` 的真正作者内容只有约百余行，且现有 canonical writer 本来就输出一个完整 JSON object。V1 因而先采用一个必需的 semantic source：

```text
GameContent/
  game.json
```

`game.json` 完整拥有：

```text
schema / contentId / revision / rulesetId
rulesetConfig
Places / Passages
Actors / Roles / private materials / initial Memory
Objects / initial placement / ownership
```

`rulesetConfig` 是所有跨实体因果参数的唯一 owner，例如 deadline target、ticket、key、container 与 revealed object。Place / Passage / Actor / Object records 只保存自身固有定义和初态，不重复声明 Ruleset binding。

V1 明确不支持：

- include 或外部 semantic text blob；
- overlay / inheritance；
- patch 文件；
- glob / recursive discovery；
- 环境特定覆盖；
- source 文件优先级或 merge order；
- per-Actor 自定义 schema。

Loader 读取 `game.json`，做全局引用校验，最终只产生一个 immutable compiled definition。Runtime reducer、Forecast、Navigator 与 Player driver 永远不读取 Content 文件。

使用目录作为 package identity，仍允许未来在真实角色规模、编辑冲突或 renderer 消费出现后增加 writer-managed source 分片与普通资产文件；这只改变 authoring loader，不改变 compiled model、Ruleset 或 Save。当前不冻结 `assets.json`、asset resolver 或 asset fingerprint。纯美术字节不属于 semantic content，但具体映射协议等第一个真实 renderer consumer 再设计。

### 5.2 Author-owned identity 与 Ruleset semantic binding

必须区分两种稳定名字：

```text
Author-owned identity
    ActorId / PlaceId / PassageId / ObjectId
    例：mara、old-archive、archive-door、seal-ring

Ruleset semantic binding key
    compiled Ruleset 需要的稳定槽位
    例：deadlineTargetPassage、unlockingObject、lockedContainer、revealedObject
```

Engine / Ruleset 不得从 `alice`、`cellar`、`brass-key` 等字面值推断意义。当前已经存在的锁箱、钥匙、隐藏内容、通行凭证与入口 deadline，不需要改写为通用 DSL；只需要由 `game.json.rulesetConfig` 中一个封闭的 typed binding 指向本 Content 的 author-owned ID。

示意：

```json
{
  "rulesetId": "firstboard.duchess-letter/2",
  "rulesetConfig": {
    "deadlineTargetPassage": "archive-door",
    "unlockingObject": "seal-ring",
    "lockedContainer": "document-case",
    "revealedObject": "private-ledger"
  }
}
```

最终字段名应在实施时跟随当前 Ruleset 的真实消费者裁决；这里冻结的是“一个唯一的 typed `rulesetConfig`”，不是这四个字符串的永久公共 API。

### 5.3 一个 compiled semantic snapshot

加载管线只有一条：

```text
game.json source DTO
→ parse with provenance
→ cross-file Validate
→ Freeze / canonical order
→ CompiledGameDefinition
→ canonical semantic JSON
→ SemanticContentSha256
```

`CompiledGameDefinition` 可以演进自当前 `ScenarioDefinition`；不要再建立一套 file-backed runtime Content model。每项错误必须保留 source path 与 JSON pointer，例如：

```text
GameContent/game.json#/actors/1/initialPlaceId
  references unknown PlaceId 'north-dock'
```

semantic hash 必须覆盖所有会改变后续世界或 Player 决策的材料：

- Graph 与初态；
- Ruleset parameters / bindings；
- Role、ReferenceMaterial、initial Memory；
- 会进入 Observation、Prompt 或 action description 的语义文本。

纯贴图、立绘、音频等表现字节不进入 semantic hash；换头像不应使 Objective Save 失效。V1 尚无真实美术加载器 consumer，因此不定义 asset mapping 或 fingerprint。若未来某项“美术元数据”会改变 visibility、affordance 或 AI/Human 获得的信息，它已经不是纯美术，必须进入 semantic snapshot。

## 6. V1 不引入脚本或 DLL

V1 不创建：

- `scripts/` 目录；
- `plugins/` 目录；
- `IScriptExtension`；
- `IGameRuleset` registry；
- expression / predicate / effect DSL；
- assembly discovery / hot reload hook；
- manifest 中的空 script / DLL 字段。

遇到一个新的真实世界 Law 时，默认流程是：

1. 在共享 C# Ruleset 中实现并测试；
2. 如果它需要作者参数，暴露一个窄的 typed Content record；
3. 用第二个真实消费者检验该 record 是否值得保留。

只有满足以下更强条件才重开：

| 能力 | 重开条件 |
|---|---|
| 受限脚本 | 多个真实 Content 反复需要一次性编排，继续加共享 C# Law 已明显阻碍创作 |
| 第二 compiled Ruleset | 一个 playable game 需要新的 state / fact / action Law，放入现有 Ruleset 会扭曲已有语义 |
| DLL plugin loader | Content 必须独立分发行为、不能重编 Engine，并且 codec / validator / reducer / version binding 能一起定义 |

未来 schema 变更的成本远小于现在维护一个没有消费者的执行 ABI。当前不留占位就是最小、最诚实的扩展策略。

## 7. V1 复合 Save

### 7.1 目录模型

V1 把一个已发布的 Save 目录视为 immutable checkpoint。续局不得在原目录或原 Journal branch 上追加，而必须产生一个新的 successor Save：

```text
Save/
  save.json
  content.snapshot.json
  journal/
  players/
    0001.json                 # safe opaque file name; actorId lives in manifest/payload
  reports/                  # optional; non-authoritative
```

```text
resume --save Save-A --out-save Save-B

Save-A 永不修改
Save-B 在 staging 中写完、关闭并校验后才发布 save.json
```

`save.json` 至少绑定：

```text
save schema
contentId / revision
SemanticContentSha256
rulesetId
worldSeed
lineageId / transitionCount
payloadCodecId
playerCheckpointCodecId
compiledSnapshotCodecId
每个 component 的 digest
每个 stateful Player 的 actorId / driver kind / config identity /
    decisionSequence / PlayerStateFingerprint / safe checkpoint file
```

Player checkpoint 文件名不得直接拼接 author-owned `ActorId`。Manifest 分配安全、不透明的文件名，并在 manifest 与 payload 内保存真实 `actorId` 和 digest；不要为了迁就 Windows 路径、保留名或大小写规则而收紧领域 ID 语义。

新局时：

```text
external GameContent → compile → new content.snapshot
```

续局时：

```text
Save/content.snapshot.json → exact definition authority
```

续局不再从外部 GameContent 按字段补全或 overlay。若调用方同时提供外部 Content，只允许 semantic hash 相同；不一致时在 fold Journal 之前拒绝。恢复还必须验证 snapshot codec、component digests、Journal branch head 与 batch count 精确等于 manifest frontier；额外 tail 不是“可忽略的更新”，而是损坏或错误的输入。

快照不是第二真理：GameContent 是创建新 run 的 authoring source；`content.snapshot.json` 是既有 run 的冻结 genesis source。二者不会同时参与一个 runtime definition。

### 7.2 为什么 Journal 不够

Objective Journal 可以恢复：

- Actor / Object 的客观状态；
- Graph movement 与 topology；
- Game facts 与当前活动。

它不能恢复 LLM Player 的全部运行时认知。当前 Memory trace 主要面向观察和诊断；只恢复 World Journal 会让角色回到 initial Memory，从而抹掉承诺、背叛、信任和戒备的连续性。这恰好会破坏产品最重视的情感联结。

可继续的 Save 因而必须把每一种 built-in stateful Player composition 定义为一个 closed production checkpoint contract，覆盖所有会改变下一次 request、prompt 或 driver 行为的状态，至少包括：

- Memory shards；
- 会影响下一次 prompt diff 的 previous-known-facts frontier；
- Actor / Decision sequence binding；
- `DecisionBudgetPlayerDriver` 等 wrapper 的 budget / turn counters；
- driver kind 与 non-secret runtime config identity；
- checkpoint codec identity；
- canonical `PlayerStateFingerprint` 或等价的逐字段校验；
- 实施发现的其它真实 Player state，且 pending maintenance 必须为空。

Human、Random、Null 等无状态 driver 不需要伪造 Memory 文件。Player checkpoint 继续属于 Player runtime，不混入 Objective World facts。

### 7.3 Clean-frontier save

V1 只承诺显式 Save / 正常退出后的 resume。安全边界必须同时满足：

```text
完整 Journal batch 已提交并安装
没有 in-flight Kernel Step / Player request
若有 Presentation，则 P == C
所有 pipelined Memory maintenance 已 Flush
每个 Player checkpoint 的 decisionSequence 与 committed World 匹配
```

写入 successor Save 时，先完成 content snapshot、封存的 exact Journal prefix、Player checkpoints 与全部校验，关闭 writer，最后发布 `save.json`。任何中途失败只留下一个未发布的新目录，不得覆盖上一个已发布 Save 的任何 component。恢复只接受完整 manifest 指向的 frontier。

V1 不承诺：

- mid-decision save；
- 保存未提交的 Player proposal；
- 每次 world commit 后 crash-zero-loss autosave；
- 在同一个已发布 Save 或 Journal branch 上原位续写；
- `P < C` 时强行截取 Human session；
- 通用 `ISaveParticipant` registry；
- 旧 Content / Save / fact codec migration；
- 可写 World snapshot 与 Journal 并列成为两份 authority。

若当前实现不愿导出 / 导入完整 Player checkpoint，只能把产物称为 **Objective Replay Package**，不能称为可继续的 Save。

## 8. 第二个 Content 包是防伪验收，不是第二套游戏 Law

必须提供第二个很小的 smoke package，使用同一个 compiled Ruleset 与相同能力骨架。它的目的不是证明 DramaBoard 已成为通用 RPG 引擎，而是证伪 `BoardIds` 和 Demo display switch 仍在暗中拥有作者身份。

作为测试策略，Pack B 的 author-owned Actor / Place / Passage / Object ID 集应与 FirstBoard 完全不相交；Ruleset semantic binding keys 保持相同。

Pack B 不必：

- 改变 cast 数量；
- 加入第三人；
- 增加新 action；
- 改变 encounter / travel Law；
- 替换锁—钥匙—容器—隐藏内容—deadline 这一编译期能力骨架；
- 引入脚本或第二 Ruleset。

最小可证伪 trace：

1. 以全新 Actor ID 枚举 roster，并为两名 Actor 生成首个 DecisionRequest；
2. 使用全新的 Place / Passage ID 完成一次 travel；
3. deadline 改变 binding 指向的新 Passage，而不是旧 `cellar-gate-passage`；
4. 新 Actor 使用 binding 指向的新 key 打开新 container，并取得新 revealed object；
5. manifest、World、Observation 与 Journal 中所有 author-ID fields 只引用 Pack B ID；Pack A author-owned ID 集零命中。`rulesetId`、fact kind 与 binding key 不参加这项 raw text 检查；
6. Observation、Presentation 与 report 精确使用 Pack B Content 提供的 display name / description，而不是 raw ID 或 Pack A label。

Pack B 的 save / reopen / continue 在 Save Slice 完成后再加入同一 fixture，不反向阻塞 Content identity Slice。

这只要求代码分离“作者身份”与“规则语义槽位”，不要求通用 cast、标签系统、ECS 或剧情 DSL。

## 9. 最小进程入口

V1 只把当前竖切确实需要的三个入口公开为 CLI：

```text
dramaboard content validate <dir>
dramaboard run --content <dir> --save <dir>
dramaboard resume --save <source> --out-save <successor>
```

最小要求：

- validator 给 path + JSON pointer 诊断；
- loader 内部 compile output 使用稳定顺序；
- `run` 只向尚未发布的新 Save 目录写入；
- `resume` 不修改 source Save；
- Replay 不重新调用 Player、Navigator 或 Content compiler；
- `git diff` 能直接审阅作者源，不要求打开二进制编辑器。

`content init / compile / inspect-graph / smoke`、MCP 与 GUI 都等真实创作摩擦出现后再评估。当前 smoke 由自动化测试承担；不得为了 Agent-first 的名义先建设完整 authoring platform。

## 10. 最小施工顺序

### Slice A：Content loader 等价性

1. 把当前 `ScenarioDefinition.Default` 搬到第一个 `GameContent/game.json`；
2. 实现 typed DTO loader、provenance、全局引用校验与 canonical compile；
3. 加 `--content`；
4. 证明 disk package 与旧内存定义得到相同 canonical hash、Graph、Genesis 与 scripted trace；
5. production 不再从 `CreateDefault()` 构造作者内容。

### Slice B：删除作者 ID 硬编码

1. roster / driver composition 改为读取 cast；
2. display name、Place / Object 文案改读 Content；
3. 当前 Ruleset 的 gate / key / container / reward 改读窄 typed bindings；
4. 添加 ID 完全不相交的 Pack B 与 §8 trace；
5. 不顺手抽取第二 Ruleset 或脚本宿主。

### Slice C：可继续的 clean Save

1. 将 FirstBoard fact codec 提升为 production；
2. live run 使用 durable Atelia Journal；
3. 为内建 LLM Player 实现完整 checkpoint export / import；
4. 实现 compiled snapshot 的 production read / write / codec verify；
5. 生成 immutable Save、canonical content snapshot 与最后发布的 `save.json`；
6. 用 deterministic stateful Scripted Player 验证 one-shot 与 save/reopen/continue 的 World、Journal 和 checkpoint frontier 等价；
7. 对内建 LLM Player 只验证 checkpoint round-trip 后，在不调用 backend 的情况下得到逐字段相同的 Player state、下一 `DecisionRequest` 与 Prompt；不要求重新调用 LLM 后的输出等价；
8. 在 Pack B fixture 上补 clean save → successor reopen → continue；
9. content hash、ruleset、codec、component digest 或 frontier mismatch 在 Replay 前稳定拒绝。

## 11. 可证伪验收矩阵

| ID | 必须证明 |
|---|---|
| CNT-1 | `game.json` 任意 JSON property / collection input order 经 compile 后得到相同 canonical snapshot 与 semantic hash。 |
| CNT-2 | duplicate ID、unknown reference、坏 binding、未知 Ruleset 或缺少 `game.json` 均以 source path + JSON pointer 在 Genesis 前拒绝。 |
| CNT-3 | Role、private material、initial Memory 或 display text 变化会改变 semantic hash。 |
| CNT-4 | Runtime reducer、Forecast、Navigator 与 Player driver 不读取 source file；加载后只消费 immutable compiled definition。 |
| BND-1 | Pack B 的全部 author-owned IDs 与 Pack A 不相交，仍由同一 build / Ruleset 完成 decision、travel、deadline 与 container trace。 |
| BND-2 | source actor 是集合；roster、Memory、Observation 与 Presentation 不引用 Alice / Bob 固定槽；结构化 author-ID fields 只出现 Pack B IDs。 |
| BND-3 | Pack B 的 Observation、Presentation 与 report 精确使用 Content label / description，而不是 raw ID 或 Pack A label。 |
| SAV-1 | `content.snapshot + journal prefix` 重建 exact committed Objective World；不存在并列可写 World snapshot。 |
| SAV-2 | 同一 clean frontier 的 uninterrupted control 与 reopen 在不调用 backend / Player 的情况下得到逐字段相同的 checkpoint fingerprint、下一 `DecisionRequest` 与 Prompt；Replay / import 调用 backend 次数为 0。 |
| SAV-3 | semantic content、ruleset、snapshot / payload / checkpoint codec、component digest 或 frontier mismatch 在 fold 前拒绝。 |
| SAV-4 | 外部 GameContent 被修改或删除后，Save 仍只用自己的 verified snapshot 续局；不 merge 外部字段；source Save 永不被 successor 修改。 |
| SAV-5 | 未完成 Player call、未 Flush maintenance 或 `P < C` 时拒绝 clean save；在任一 successor component write 后注入失败，上一个 published Save 仍可 reopen。 |
| EXT-1 | V1 manifest、目录与 production references 中不存在 script/plugin/overlay/hot-reload 占位。 |

## 12. 最终裁决表

| Verdict | 项目 | 理由 |
|---|---|---|
| **keep** | external typed GameContent | 不做则每个原型仍需修改 `CreateDefault()` 和重新编译 |
| **merge** | loader 输出进入现有 Definition / Graph pipeline | 避免 file DTO 与 runtime model 成为双 authority |
| **keep** | canonical semantic snapshot + full hash | Save 必须精确绑定 Genesis、Player materials 与 Ruleset parameters |
| **keep** | Objective Journal +完整 Player checkpoint | 少任一方都不能继续同一个世界中的同一个角色 |
| **simplify** | 一个 compiled Ruleset +窄 typed bindings | 复用世界能力，同时允许作者自由命名实体 |
| **simplify** | 每个 Content 目录一个 `game.json` | 直接复用现有 canonical object；真实规模出现前不引入 fragments / merge language |
| **defer** | script、第二 Ruleset、DLL loader | 当前没有能力差异消费者；`RulesetId` 已足够作为未来身份 |
| **defer** | hot reload、old-save migration、crash-latest autosave | 当前原型部署模型不需要 |
| **defer** | generator、inspect/smoke CLI、MCP / GUI editor | 当前 loader / validate / run / resume 竖切稳定后再扩作者前端 |
| **delete** | 空 extension interfaces、`scripts/` / `plugins/` 占位 | 新抽象唯一消费者仍是假设需求 |
| **delete** | include / overlay / patch / arbitrary property bag | 会形成来源、优先级和验证不清的第二语言 |
| **delete** | 可写 World snapshot 与 Journal 双 authority | restore frontier 可以静默分裂 |

## 13. 一句话结论

DramaBoard 的 Content 不应是一套伪装成 JSON 的游戏编程语言，而应是一份可编译的戏剧前提：

> **Engine / Ruleset 决定世界允许哪些动词；GameContent 决定谁带着什么欲望、秘密、关系、资源和时间压力进入这个世界；Save 冻结这份前提，并延续已经发生的世界历史与角色记忆。**

这条边界既服务项目真正的差异化，也比“配置 + 脚本 + DLL”更适合 Coding Agent 持续创作。
