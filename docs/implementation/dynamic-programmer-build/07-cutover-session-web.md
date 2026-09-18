# R7 · 产品切换：Session、HTTP 与 WebUI

状态：未开始。依赖：R6。产物：`src/Server`、`src/WebUI`、`tests/Server.Tests`、e2e。

## 目标

一次性把 live 流量从旧 DecisionPoint/Intent 路径切到 R5/R6 的新规则与 `IDynamicProgrammer`，server 与 web 同批完成（拆开会留下打不通的旧表单，违反 build-green）。旧类型本轮不再被生产引用，但保留编译（R8 删除）。

## 前置阅读

- [机制模型](../../design/player-runtime/dynamic-programmer.md) §7（建议配置）、§7.2（行为验收）、§8（与当前源码的关系）
- 现状：`src/Server/FreePlay/FreePlaySession.cs`（pending/TCS/Submit 模式）、`src/Server/Web/DecisionInput.cs`（严格解析风格）、`src/Server/Program.cs`、`src/WebUI/src`（PlayerPage/types/player-api）、`src/WebUI/e2e/movement.spec.ts`

## Server 改动

**FreePlayScene 收缩为组合根**：删除 `IOccurrenceRule` 实现、`DecisionKey`、`TryPlan`、`_decide` 回调与 DecisionPoint 分支；保留 `Definition`、`Genesis`、`Fold`、`Validate`、`Location` 与 planner/queries/reducer 构件（向规则构造暴露所需内部构件）。`Validate` 在原有两项之上**新增跨域不变量**：actor Traversing ⇔ `Mode` 为 `RunningMove(g)` 且 g 与 `actor.MovementGeneration` 一致（旧路径已移除，此刻起恒可成立）。

**FreePlaySession**：

- 构造签名 `FreePlaySession(IDynamicProgrammer? programmer = null)`；默认 `WebHumanProgrammer`（镜像旧 `WebHumanDriver`：包一个等待 `TaskCompletionSource<ProgrammingResponse>` 的委托，定义在 Session 文件内）。
- Kernel 规则列表变为 `[FreePlaySpatialRule, FreePlayProcessRule]`；泛型沿用 R4 的 `FreePlayFact`。
- 待答流：process 规则的 respond Plan 调用 programmer 时，Session 侧回调镜像现有 `DecideAsync`——锁内置 `PendingProgram(Request, TaskCompletionSource<ProgrammingResponse>)`（**不存 World**：世界真相由规则侧持有，提交侧只做纯关联校验）并发布 waiting 状态与请求视图；注入式 programmer 直接调用，Web 走 TCS。
- `Submit(ProgrammingInput input)`：
  1. 会话不可用（faulted/stopped）→ 503；`_pending` 为空或 `activationId` 不匹配 → 409 stale（含 staying 之后：无待答激活是健康静止，客户端凭 `status=staying` 呈现终态，GET 继续可用）；
  2. 逐条构造 `Instruction`（构造异常 → 400 invalid-program，**保留 pending**）；组装 `ProgrammingResponse`；
  3. `ProgrammingResponseValidator.Validate(response, pending.Request)` 失败 → 400（保留 pending、时间不前进）；
  4. 成功 → 消费 pending、发布 advancing、完成 TCS → 202。
- `RunAsync`：Committed 循环不变；`Exhausted` 且 `kernel.World.Process.Mode is RunningStayProcess` → 以状态 `"staying"` 正常结束（继续提供查询服务）；其余非 Committed → 照旧抛错暴露规则故障。
- 视图（`Views.cs`）：`PlayerView` 增加 `Workspace`（string）、`PendingProgram`（指令 DTO 列表）与 `Request`（可空）：`ActivationId`、`Reason`、`ReasonDetail`、`LocationText`、`KnownPassages[]`、`Workspace`、`RemainingProgram[]`（冻结快照，替代旧 `Decision`）；`DevView` 增加 `ProcessMode`、`ControlRevision`、`PendingActivationId`（替代 `PendingDecisionId`）。Player 视图不得出现 dev 内部字段（沿用 HttpBoundaryTests 的禁字段断言）。

**HTTP**（`Program.cs` + `src/Server/Web/ProgrammingInput.cs`，替代 `DecisionInput`）：

- 路由 `POST /api/player/programs`（旧 `/api/player/decisions` 删除）。
- `ProgrammingInput(string ActivationId, IReadOnlyList<RawInstruction> Program, string Workspace)`、`RawInstruction(string Kind, string? Passage, string? Note)`；`TryRead` 沿用逐字段严格解析：顶层恰含 `activationId`/`program`/`workspace`（缺失、多余、重复拒绝；`program` 必须是数组，**缺失或 null 拒绝，显式 `[]` 合法**；`workspace` 必须字符串、可为空串）；指令对象 `kind` ∈ `move|think|stay`，move 恰含 `{kind, passage}`、think 恰含 `{kind, note}`、stay 恰含 `{kind}`，字符串非空白。

## WebUI 改动

- `types.ts`：新增指令/请求 DTO 与 `status: 'staying'`；`player-api.ts`：`submitProgram(activationId, program, workspace)` → `/api/player/programs`（202/400/409/503 语义沿用）。
- `PlayerPage.tsx`：
  - waiting 时展示请求材料：唤醒原因（缺指令/行动受阻/思考完成 + ReasonDetail）、当前位置、工作区、待执行程序、已知通路表（waiting 时以 `Request` 快照为单一读取来源；`PendingProgram` 是活状态、用于非 waiting 展示——两者生命周期不同）；
  - **快捷一步按钮**（当前地点**已知可通行**的方向——按请求材料的静态已知位过滤，命名保持 `前往 <place> <duration> ms`，e2e 依赖）：提交单步程序 `[Move(p)]` + 当前工作区——单步动作就是一步的程序，同一控制语义；动态关闭不在此过滤，由提交后的正式受阻与原因表达；
  - **程序编辑区**：指令行编辑（种类 move/think/stay + 通路选择/笔记输入，可增删，**预填当前 RemainingProgram**——受阻/维护时"重试原程序"与"保存并待机"都清楚可见，机制 §5.3）+ 工作区文本框（预填当前值，整体提交）；「清空并待机」按钮显式提交空程序（UI 文案说明其含义为 `[Stay]`）。两种提交路径都走同一 API。
  - `staying` 状态展示"待机中：世界继续，等待没有安排"；Summary 组件同步新状态。
- e2e（`movement.spec.ts` 重写）：等待页可达 → 快捷移动 A→B→D（时间 1000/4000）→ 无效提交（未知通路）保留 pending → 旧 activation 409 → 刷新连续性（刷新点安排在有 pending 请求与非空工作区的状态，证明 Buffer/笔记不丢——§7.2"信息连续性"行）→ 「清空并待机」后 status 变 `staying`、时间冻结、轨迹不变、再提交得 409 → 诊断页独立（Player 页无 `/api/dev` 请求、无内部字段）。

## 测试重写（Server.Tests）

- `FreePlaySessionTests`（用 `ScriptedDynamicProgrammer` 注入）：
  - 两段移动自动执行、C 处不唤醒（轨迹与 transition 计数）；
  - Think 闭环：正耗时、笔记纳入、完成后的请求材料可见笔记；
  - 受阻后重试与 `[Stay, ...]` 保留安排（状态可查询）；
  - 空程序提交 → `staying` 正常结束、GET 继续可用；
  - 无效提交（坏 shape、未知 handle、错误 activation）不消费 pending、时间不前进；并发提交恰好一个 202；
  - 经 HTTP 的正向程序提交：多指令程序（含 Think + 工作区文本）→ 202 → 推进至思考完成维护，下一请求视图含追加笔记（本批唯一的新产品面必须有正例，不只负例）；
  - staying 后提交 → 409 且 GET 继续可用；
  - 写权限一行断言：工作区写入"我已位于 D"类谎言后，Location/轨迹不变（§7.2"写权限"行，主要由响应形状结构保证，此断言钉住主观文本不扩张行动资格）；
  - 跨 run 的 activation 409、停止后 503；programmer 抛错 → faulted 且保留最后世界；注入式非法响应 → faulted 零提交（对应旧 `OwningRuleRejects...` 的语义迁移）。
- `HttpBoundaryTests`：新端点解析严格性（含 `program` 缺失 vs `null` vs `[]`、指令字段多余/缺失/kind 未知）、禁字段、并发、stale、fault 诊断。
- R5 的 `ArrivalWithoutRunningMoveFailsBeforeCommit` 随跨域不变量激活同步改写：断言该不一致世界在构造期被 `scene.Validate` 拒绝（Plan 内防御分支保留，但不再可经 Kernel 构造触达）。
- `ApplicationBoundaryTests` 不变。

## 验收

```text
dotnet test DramaBoard.slnx
dotnet test DramaBoard.Local.slnx
npm --prefix src/WebUI run build
npm --prefix src/WebUI run test:e2e   # 本地具备 Playwright/发布环境时运行；不具备则注明由 CI 覆盖
```

## 禁止事项

- 不保留旧端点、旧 Submit 形状或 Intent 兼容分支；不为切换加 feature flag。
- 旧 `IPlayerDriver`/`DecisionRequest`/`PlayerDecision`/`PlayerDecisionValidator` 本轮不删（其单测保持绿），R8 统一处置。
- HTTP 层不做语义校验之外的世界读取；GET/刷新仍只读。
