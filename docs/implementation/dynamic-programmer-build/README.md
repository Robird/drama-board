# Dynamic-Programmer 施工工单（R1–R8）

> 层级：施工工单。创建：2026-09-19；同日经三角色独立评审与交叉质询修订。
> 语义权威是[机制模型](../../design/player-runtime/dynamic-programmer.md)与[运行时理念](../../design/player-runtime/README.md)；本文只固化工程分解与已定裁决，不复制其产品语义。
> 用户裁决：**新 Buffer/Programmer 机制整体替换旧 Intent 单发决策路径**；不并存、不加兼容层。

## 执行方式

每轮一个 fork 会话，按编号顺序执行。轮内工单自包含：开工前完整阅读该轮文档与其中"前置阅读"。完成后回传三样东西：运行的验证命令与结果、对工单的偏离及原因、把下方状态表对应行改为"已完成"。R8 前不修改设计文档；PROJECT-STATE 作为长线状态记忆随进度自主维护（见 [AGENTS.md](../../../AGENTS.md)）。

## 已冻结的工程基线

1. 指令是强类型纯数据（`MoveInstruction / ThinkInstruction / StayInstruction`），不复用可空字段的 `Intent`；Buffer 不可变、只保存未开始尾部。
2. Process 用互斥执行状态表达；`RunningStay` 是待机的唯一权威（不设第二停驻开关）；`RunningMove` 只存 generation 关联，运动真相读 Spatial。
3. Programmer 每次提交完整程序与完整工作区，整体验证通过后原子接受；合法空程序在**接受步骤**规范为 `[Stay]`（不在构造器或反序列化层）；不通过比较内容差异判断执行意愿。
4. 一切状态变化仍经同一个 Kernel 提交；本批不修改 Kernel/Spatial 行为。

评审补齐项的裁决：

| 事项 | 裁决 |
|---|---|
| 候选映射 | `AwaitingProgrammer` → respond（due=now）；`Ready` → start（due=now）；`RunningThink` → think-complete（due=CompletesAt）；`RunningMove`/`RunningStay` 不产生 Process 候选。接受与开始是两个 transition，同 tick 其它原因可以插在两者之间并把 start 变成受阻（DIR-2 语义，有意为之） |
| ControlRevision | 每条 Process fact 折叠时 +1；respond/start 候选 key 内嵌它；`ActivationId = "{runId}:{ControlRevision}"`；迟到/重复响应按 activation 拒绝 |
| 指令类型的家 | Protocol 持有指令、工作区、请求/响应；Process 状态与 facts 直接内嵌这些 Protocol 类型，不建第二套定义 |
| 旧路径处置 | R7 切换、R8 删除 `IPlayerDriver`、`DecisionRequest`、`Observation`、`PlayerDecision`、`Intent`、`PlayerDecisionValidator` 等；其"affordance 当前可用"匹配语义**不**迁移——接受程序时只查知识/引用资格，可执行性留给开始时的 planner |
| 无效响应 | HTTP 预检拒绝且不消费 pending；注入式非法响应令 Step 在发布前失败、零提交（沿用现有先例） |
| 规则常量 | MoveSpeed = 1 units/ms；ThinkDuration = 1000 ms；笔记追加格式 `[<模型毫秒>] <笔记>` |
| 工作区提交界 | `提交长度 ≤ max(65_536, 当前工作区长度)`：只限制 Programmer 发起的增长；系统 Think 追加后的原样重交永远合法，不因累计越限而失败（R2 校验器） |
| KnownPassages 方向位 | 取已知图静态 `InitialEntryAccess`，不读世界实时 override；远方动态入口状态不披露（运行时理念 §3），动态关闭由开始时 planner 拒绝与 `MoveBlocked` 原因表达。重开条件：动态入口变化进入可玩场景时重裁披露资格，并增加本地实时观察出口 |
| staying 后提交 | 返回 409（无待答激活）；staying 是健康静止，会话继续服务 GET，客户端凭 `status=staying` 呈现终态 |

## 组件落点

| 位置 | 内容 |
|---|---|
| `src/Protocol` | 指令、`SubjectiveWorkspace`、`ProgrammingRequest/Response`、`ActivationId`、材料 DTO |
| `src/Player` | `IDynamicProgrammer`、`ScriptedDynamicProgrammer` |
| `src/Decision.Validation` | `ProgrammingResponseValidator`（纯关联/引用校验） |
| `src/Server/FreePlay/Runtime` | `InstructionBuffer`、`PlayerProcessState`、Process facts、reducer/validator、`FreePlaySpatialRule`、`FreePlayProcessRule` |
| `src/Server` Session/Web | HTTP 待答、`ProgrammingInput` 解析、视图发布、端点 |
| `src/WebUI` | 程序提交表单、工作区/待执行程序展示、`staying` 状态 |
| Kernel / Spatial / Player.Agency / Host | 零行为修改，只消费 public API |

## 提交语义总表

一次 transition 的 draft facts 按数组序 fold：

| 事件 | draft facts |
|---|---|
| 接受程序 | `[Process(ProgramAccepted)]` |
| Move 开始 | `[Spatial(TraversalStarted), Process(MoveStarted)]` |
| Move 受阻 | `[Process(MoveBlocked)]`（保留头部与尾部，进入带原因维护） |
| Move 到达 | `[Spatial(TraversalArrived), Process(MoveArrived)]`（同批结束当前 Move 并确定下一阶段） |
| Think 开始 | `[Process(ThinkStarted)]`（消费头部、追加笔记、设定完成时刻） |
| Think 完成 | `[Process(ThinkCompleted)]` |
| Stay 开始 | `[Process(StayStarted)]` |

## 轮次总览

| 轮 | 主题 | 依赖 | 核心验收 |
|---|---|---|---|
| [R1](01-protocol-types.md) | Protocol 契约类型 | — | Protocol.Tests 全绿；生产代码未接入 |
| [R2](02-programmer-and-response-validation.md) | Programmer 接口与响应校验器 | R1 | Player.Tests、Decision.Validation.Tests 全绿 |
| [R3](03-process-domain.md) | Process 领域（状态/facts/reducer） | R1 | reducer 全转移矩阵测试通过 |
| [R4](04-world-and-fact-union.md) | FreePlayWorld 扩展与 FreePlayFact 联合 | R3 | 现有全部测试不变绿 |
| [R5](05-spatial-adapter-rule.md) | FreePlaySpatialRule（到达适配） | R4 | 到达同批结束当前 Move 的 kernel 级测试 |
| [R6](06-process-rule.md) | FreePlayProcessRule 与闭环验收 | R2、R5 | 机制文档 §7.2 行为矩阵的 kernel 级覆盖 |
| [R7](07-cutover-session-web.md) | 产品切换（Session/HTTP/WebUI/测试重写） | R6 | 全链路新语义，含前端与 e2e |
| [R8](08-cleanup-and-docs.md) | 删旧、守卫、文档与证据 | R7 | rg 零命中 + 双 solution 全绿 + 发布链 |

## 全局守则

- 不修改 `src/Kernel`、`src/Spatial`、`src/Player.Agency`、`src/Host` 的任何行为；只消费其 public API。施工中发现必须修改时，停止并在回传中说明。
- 不新增项目、不改 solution（`ApplicationBoundaryTests` 钉死 8+8 项目与 Server 的六个引用）；不引入兼容层、双路径或 feature flag。
- K&R 风格（`.editorconfig`）；新增代码 warning-clean（CI 使用 `-warnaserror`）。
- 每轮完成时 `dotnet test DramaBoard.slnx` 全绿；R7 起另需 `npm --prefix src/WebUI run build` 通过。DurableGraph 包在本地已按[准备说明](../../worksets/durablegraph-package-source.md)就绪时无需重跑。
- 语义疑问以机制文档为准；工单与源码事实冲突时以源码为准修正工单并回传。

## 状态

- [x] R1 Protocol 契约类型
- [x] R2 Programmer 与响应校验
- [ ] R3 Process 领域
- [ ] R4 World 与 Fact 联合
- [ ] R5 Spatial 适配规则
- [ ] R6 Process 规则与闭环
- [ ] R7 产品切换
- [ ] R8 清理与文档
