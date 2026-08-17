# Design Note 006：Scenario Definition、Instance 与 Run Manifest

**状态：WP24 已采纳。日期：2026-08-18。**

## 问题

FirstBoard 的客观初态原本在 `FirstBoardWorld.CreateInitial` 中命令式构造，角色卡、私有 ReferenceMaterial 和初始 Memory 又散落在 Demo `Program`。这对单场原型足够，但会阻碍 Providence / Scenario Forge：复制场景、改变 deadline 或私有材料、批量换 seed、比较不同 Player runtime 时，无法可靠回答“究竟是场景变了，还是运行配置变了”。

WP24 不引入配置 DSL，也不实现 Providence。目标只是把已经存在的内容变成可复制、可散列、可关联运行结果的数据对象。

## 三层裁决

### ScenarioDefinition：seed-independent 的场景内容

`ScenarioDefinition` 位于 FirstBoard 场景装配层，包含：

- `Id / Revision / RulesetId`；
- Places 与有序 adjacency；
- Actors 的初始位置；
- 每个 actor 自己的 Role、ReferenceMaterial 与初始 Memory shards；
- Objects 的初始 public place / owner / hidden 状态；
- `CellarDeadlineMs`。

角色卡、初始 Memory 与长期材料虽然不属于 Objective World，却会改变角色面对的初始策略问题，因此属于场景内容并参与 definition hash。私有材料仍嵌在所属 actor 下，权威键是 `(actorId, materialId)`；`scenario-definition.json` 是研究者可见的全知工件，不能被整体投给任何 Player。

`RulesetId=firstboard.duchess-letter/1` 明示当前数据仍依赖 FirstBoard 的特定 Law：钥匙开箱、密信内容、公共放置等语义仍由代码实现。本对象不是通用因果模板语言。

### ScenarioInstance：冻结 Definition + WorldSeed

`ScenarioInstance` 构造时校验并深复制全部集合，缓存：

- 完整 `DefinitionSha256`；
- `WorldSeed`；
- 完整 `InstanceSha256`。

外部传入的 `List<T>` 此后再被修改，不会让同一 instance 的 hash 或初始世界漂移。Definition hash 不含 seed；换 seed 保留相同 definition identity，但产生不同 instance identity。显示用 `Id` 只截取 hash 前缀，研究关联使用完整 64 位十六进制 SHA-256。

列表顺序当前属于语义：它同时影响 persistent numeric id、同刻 source order、prompt 中材料/Memory 顺序，所以 canonical writer 与 world 实例化必须读取同一冻结顺序，不擅自排序。

### Run Manifest：一次具体执行

Demo 在创建 backend、发出网络请求之前写：

- `scenario-definition.json`：canonical definition 的完整字节；
- `run-manifest.json`：一次运行的来源与状态。

manifest 区分：

- Scenario：definition / instance full hash、seed、ruleset、definition artifact；
- Origin：当前为 `root`，未来可扩 parent run / fork point；
- Simulation：lineage、horizon、turn budget；
- Players：actor→backend/model/thinking effort；
- Memory runtime：backend/model/effort/maintenance mode；
- Operational：timeout 与去除 userinfo/query/fragment 的 endpoint identity；
- Software：assembly informational version、可选 git commit/dirty、.NET runtime；
- Result：完成状态、最终模型时间、事件数与 LLM turn 数。

`RunConfigurationSha256` 排除 run id、wall-clock、output path 与一切 credential，只覆盖会改变实验条件的配置。失败局会把 manifest 从 `running` 更新为 `failed` 并只记录异常类型；API key 的值和环境变量内容从不进入 definition、manifest 或 hash。

Definition + seed + run config 只能重建客观初态和实验条件，不能保证重新调用 LLM 得到相同轨迹。世界 replay 的 authority 仍是 committed journal；manifest 明示这一点。

## Causal parameter 必须真正生效

`CellarDeadlineMs` 不只是 metadata。运行路径已改为：

```text
ScenarioInstance
  → FirstBoardScenario.CreateLoop(definition)
  → CellarDeadlineSystem(definition.CellarDeadlineMs)
  → cellar.sealed journal event
```

测试以 123ms 的 definition 变体验证 `cellar.sealed` 确实提交在 123ms。未来新增可变参数时也必须穿透到 Law，不能只改变 hash。

## 非目标与后续边界

- 不读外部 JSON 来构造 Definition；当前仍由类型安全代码创建。
- 不建立通用 Scenario DSL、Causal Template schema 或 Scenario Pool。
- 不实现在线 Providence、intervention command 或 meta journal。
- 不把 Definition 放进 WorldState；checkpoint/fork 必须通过 manifest 找回相同 definition artifact。
- travel/default wait、锁箱特殊语义仍属于 FirstBoard ruleset；等真实 mutation 需要再逐项数据化。
- `LineageId=10001` 仍是 Demo 固定值。RunId 与 LineageId 已概念分离，但多 root run / fork 的 lineage 分配留给后续 provenance 工作。

## 验收发现

- 默认 definition 生成的 world 与 WP23 前初态相同。
- 修改材料或 deadline 会改变 Definition SHA；只换 seed 不改变 Definition SHA，但会改变 Instance SHA。
- instance 能抵抗构造后外部 list 修改。
- 自定义 deadline 真实改变事件时间。
- 缺凭据的失败 smoke 在 backend 创建失败前已留下 definition 与 `failed` manifest。
- 两 actor 各一 turn 的 DeepSeek smoke 完成：14 events / 2 LLM turns，manifest definition hash 与工件字节 SHA 完全一致，扫描不到 API key。
