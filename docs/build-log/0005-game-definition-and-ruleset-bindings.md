# Build Log 0005：Game Definition、typed Ruleset bindings 与 content-neutral core

> 状态：**Planned implementation vertical slice**
>
> 依赖：[Build Log 0004](0004-game-content-and-composite-save.md)
>
> 吸收原 Build Log 0006 的 FirstBoard author-ID migration。
>
> 范围：在仍使用内嵌 Pack A factory 的前提下，一次完成 `src/FirstBoard` 的 Definition schema、validation、canonicalization、Genesis，以及 Ruleset 对 author-owned identity 和语义文本的全部消费迁移；不做 DLL loader、Runner 泛化或 Save。

## 1. 完成状态

Definition 已完整拥有 Pack A 的作者实体、显示文本、初态和封闭的 Ruleset bindings；`CreateInitialWorld()` 不再合成 Definition 之外的 locked container。FirstBoard 的 planner、deadline rule、reducer、Observation、affordance、replay 与 validation 只从同一份 frozen Definition/config 取得 author-owned identity，不再从 `alice`、`cellar`、`brass-key` 等 Pack A 字面值推断规则。

本 slice 以一个 ID 集与 Pack A 完全不相交的内存态 B Definition 跑通 exact Ruleset trace。它是最早的可证伪 Content-neutrality 竖切，而不是等到外部 Pack B DLL 出现后才第一次检验 typed bindings。

本 slice 暂不做 `ScenarioDefinition → GameDefinition` 等 CLR 机械 rename。`firstboard.duchess-letter/2` 同时混入了 Pack A 身份和旧语义；本次不兼容迁移将 Ruleset identity 改为 `dramaboard.rulesets.firstboard/1`，canonical schema/codec identity 改为 `dramaboard.game-definition/1`。Pack A、内存态 B 和后续外部 Pack B 都使用同一 RulesetId。

## 2. 唯一 typed config owner

新增封闭的 `FirstBoardRulesetConfig`。字段以当前真实消费者为限，至少包括：

```text
DeadlineMs
DeadlinePassageId
LockedContainerObjectId
UnlockingObjectId
RevealedObjectId
RevealedObjectAuthenticityText
RevealedObjectContentsText
```

`DeadlineMs` 不再以 `CellarDeadlineMs` 作为 Definition 顶层第二 owner。V1 规定 deadline 关闭所绑定 Passage 的 A → B entry，Endpoint B 是已经进入受关闭区域的 witness side；validation 必须验证该 passage、方向与初始 access 满足 Ruleset 前提。

最后两个文本槽保留当前两个可分别按 fact kind 分享的知识语义：对 revealed object 的真伪判断与内容认知。它们必须非空并进入 canonical bytes/hash。不能把两者压成一个 Object description，也不建立 dictionary、property bag、Content-defined fact kind 或 effect registry。

Place、Passage、Object 增加 Player-visible name 与必要 description；Actor 继续以 `Role.Name` 为显示名。普通可见、移动、container 和 deadline 文本由这些字段加 Ruleset-owned template 生成。Ruleset-owned fact kind 同步使用 Content-neutral 名称；`ChestOpened`、`CellarSealed` 等内部状态名可以暂时保留，但不得把 Pack A 人物、地点或物品文案写入 `BoardFact.Text`、Observation 或 action description。

## 3. Definition 与 Genesis invariants

- 所有 binding 必须引用已声明且类别正确的 Passage/Object，四个 binding identity 必须满足 Ruleset 要求的 distinctness。
- locked container 是普通 `ScenarioObjectDefinition`，初始无 owner 且恰好放在一个 Place。
- revealed object 初始隐藏；unlocking object 是可正常持有、放置、给予和展示的 Object。
- locked container 可见、可 inspect，但不是 portable object；Take 的 direct resolver 必须独立拒绝，不能只依赖 affordance 隐藏。
- `CreateInitialWorld()` 只能枚举 Definition 的 Actor/Object/placement，不 append synthetic entity。
- reducer、validator、deadline、planner、Observation 与 replay 都接收同一 frozen Definition/config。不得长期保留“优先 config，缺失时 fallback `BoardIds`”的双 authority。
- Pack A author constants 在外置前可以留在唯一内嵌 Content factory 中；`src/FirstBoard` 的其余 production code 对它们零直接消费。

## 4. 施工步骤

1. 扩展 Definition records、constructor、lookup API 与 `FirstBoardRulesetConfig`。
2. 收束 `DeadlineMs` 和所有 binding/prose validation；增加 unknown、错类别、重复、方向错误和矛盾 Genesis 负例。
3. 更新 `Freeze()`、canonical writer 与 SHA identity；keyed collections 继续按 ordinal ID canonicalize。
4. 更新 `CreateInitialWorld()`，把 locked container 纳入普通 Object/placement，删除 synthetic append 与 reserved-ID 特例。
5. 让 reducer 从 `ScenarioInstance` 或 frozen Definition/config 构造，而不是只拿 Graph；planner、deadline rule、Observation 与 reducer 同批迁移。
6. portable affordance、Take/Put/Give/Show resolver 都排除 locked container；direct Take 稳定拒绝。
7. deadline 只操作 `DeadlinePassageId`，并从 bound Passage 推导 witness side；container/key/revealed object 的全部 event、fold 和 fact 只读 bindings。
8. 将 Pack-specific fact kind/prose 改为 Content-neutral Ruleset concepts，并由 labels、descriptions 与两个 closed revealed-object prose slots 生成文本。
9. 更新内嵌 Pack A factory 和现有 unit、host、atomicity、replay、persistence tests。
10. 增加 ID 完全不相交的 core-level B fixture 与下述 exact trace。

## 5. 内存态 B exact trace

B fixture 只复用同一 FirstBoard 能力骨架，不新增 action、fact、第三名角色、脚本或第二 Ruleset。验收必须至少证明：

1. B 的全部 Actor 都收到首个 `DecisionRequest`，request/Observation 只含 B 的结构化 author IDs。
2. B Actor 使用 B Place/Passage 完成 travel。
3. deadline commit 精确修改 B 的 bound Passage，并给位于 Endpoint B 的 witness 正确事实。
4. direct Take(B container) 被拒绝，普通 portable object 仍可 Take/Put/Give/Show。
5. 持有 B unlocking object 的 Actor 对 B container 执行 Use；accepted batch 经 reducer fold 后，B revealed object 归该 Actor。
6. 随后 inspect 产生相互独立的 authenticity/content knowledge facts，文本来自 B Definition 的 typed prose。
7. 将 committed Journal 从 Genesis exact replay，得到相同 World；World、Observation、Journal payload 的结构化 author-ID fields 对 Pack A ID 集零命中。

这条 trace 必须走到 reducer fold、inspect 和 replay；只证明 Definition 可构造、Genesis 可创建或 planner 接受，不足以结束本 slice。

## 6. 非目标

- 不建立 Content Provider、Pack manifest、ALC loader 或第二个真实 Content DLL。
- 不修改 Runner options、LLM composition、Presentation、report 或 CLI。
- 不把 FirstBoard 泛化为任意 RPG Ruleset。
- 不增加关系数值系统、通用 container schema、ECS、脚本、property bag 或 JSON authoring loader。
- 不做程序集、namespace、路径或公共类型批量 rename。

## 7. 验收

- [ ] Genesis 中每个客观实体都可追溯到 Definition；不存在 synthetic container。
- [ ] config/prose 的 unknown、错类别、空值、方向错误或结构矛盾在 Kernel 构造前拒绝。
- [ ] locked container 只出现一次、可见可 inspect、不会出现在 portable actions，direct Take 稳定拒绝。
- [ ] labels、config、Role materials、initial Memory 和 revealed-object 两段知识文本都进入 canonical bytes/hash。
- [ ] keyed collection reorder 不改变 canonical bytes/hash。
- [ ] RulesetId 精确为 `dramaboard.rulesets.firstboard/1`；root schema/codec identity 精确为 `dramaboard.game-definition/1`。
- [ ] 除唯一内嵌 Pack A factory 外，FirstBoard production code 对 author-owned `BoardIds` 零直接消费。
- [ ] Pack A trace 除预期 identity/text schema 更新外保持行为等价。
- [ ] 内存态 B exact trace 完成 decision、travel、deadline、container、reveal、inspect 与 replay。
- [ ] B 的结构化 IDs 与进入 Prompt 的语义文本均来自 B Definition。
- [ ] `dotnet test` 全量通过。

## 8. 建议提交边界

这是一个必须以完整 B trace 收敛的语义 activation。内部可以用可审阅的小提交组织 schema 与 consumer migration，但不得把带 fallback 或只有部分 consumer 改读 config 的中间状态当作已完成 slice。
