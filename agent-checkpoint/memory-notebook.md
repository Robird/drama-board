# 主线会话 Checkpoint（memory-notebook）

**用途：上下文压缩前的主动快照。压缩后的主线会话（我）在 Observe 阶段读此文件恢复完整工作状态。**
**更新时机：每次用户触发压缩前，或每轮结束时按需刷新。本版：2026-08-16 第二轮结束（D9+WP8+评审+修复包完成，108 测试绿）。**

---

## 1. 我是谁、在做什么

- 我是 DramaBoard 项目的**主线会话**：保持高层理解与方向，按"自主循环协议"每轮自主规划，把实现派发给 `coder` subagent（`runSubagent`，agentName=`coder`，GPT-5.6 Sol），验收后逐包 commit。
- 项目：DramaBoard（戏剧棋盘）——AI Player 参与的沙盒戏剧棋类；当前阶段只做 DEVS-like Simulation Kernel（纯函数、零依赖、确定性、事件驱动跳跃时间）。
- repo：`e:\repos\dream-board`（目录名是旧名，正式名 DramaBoard，代码命名 `DramaBoard.*`）。

## 2. 恢复上下文的阅读路径（按序）

1. `docs/研发计划_002_工作包分解.md` —— **最重要**：自主循环协议（顶部）、状态表（各 WP 备注含设计裁决）、"第一阶段结论"、"下一轮建议"
2. `docs/研发计划_001_架构基线与决策记录.md` —— D1–D8（D1 纯函数零依赖、D2 事件链是 authority、D7 命名 DramaBoard）
3. repo memory（`/memories/repo/project-facts.md`）—— atelia 库形状、流程要点、终端坑
4. `git log --oneline` —— 每 WP 一个 commit，干活前跑 `dotnet test` 确认基线（当前 76/76 绿）
5. 设计愿景按需查：`docs/开放世界棋盘游戏设计_003_*.md`（Kernel 语义之源，§4 Forecast 失效、§11 同刻、§12 RNG、§16 队列≠Journal）；001（游戏愿景）、002（Host/Player/Protocol 边界）、基础设施.md（工作包优先级与不变量清单原始出处）

## 3. Kernel 当前形状（认知快照，代码在 src/Kernel/）

- **D9 已落实（强制 event-sourcing）**：`ISimSystem.Resolve` 只返回事件；world 由 Loop 把已提交事件交给全局 `IEventReducer` 折叠。旧 ResolveResult 双轨路径已删。
- `Time/`：ModelTime（**1 tick = 1ms**）、ModelDuration、Microstep、LogicalTimestamp。checked 溢出。
- `Scheduling/`：EventCandidate<TPayload>（Due=ModelTime）、ForecastQueue（tie-break `(Due,SourceId,CandidateId)`；注意：InvalidateSource/Generation 实际未被 Loop 使用——评审 A4 指出的"假承诺字段"，待裁决用或删）。
- `Simulation/`：ISimSystem（ForecastNext/Resolve 纯函数）、IEventReducer、SimulationLoop（每事件后全量重 Forecast；owner=(SourceId,CandidateId)；Microstep 按提交序分配跨时刻重置；**no-op 守卫 + 同刻 resolve 预算（默认 10_000，构造参数）**）。
- `Journal/`：DomainEvent(Timestamp, Kind=EventKind, Payload)、**EventKind(string Id, ushort Version)**（手写小写点分稳定 id；**路由只按 Id，Version 不参与**）、**EventKindRegistry**（kind↔payload 类型唯一性）、IJournalSink、InMemoryJournal（**严格递增**，拒绝 <=）。
- `Random/`：DeterministicRandom 坐标寻址纯函数；**RNG 常量与派生规则=replay 兼容契约**。
- 同刻语义：顺序 Resolve + tie-break + 因果穿透（测试锁定）。
- 测试侧（108 绿：Kernel 96 + Protocol 12）：五玩具模型、ReplayHarness、ForkHarness、CsCheck 不变量、WorldSnapshotContractTests（A10 变异探针）。
- `src/Protocol/`（WP8）：DecisionRequest/PlayerDecision/Intent/ExpectedOutcome/Observation/KnownFact/AvailableAction；零依赖（不引用 Kernel）；时间/版本用原始 long；Intent 扁平；六动作表达力锁定。

## 4. 尚未落盘的思考：WP9 规格重塑（下轮第一件事）

D9 已完成（方案 a 采纳并全面落实，正式条目在研发计划_001）。新焦点：**adversarial review（研发计划_003，Opus 出品，质量极高）的 Top-3 必须进 WP9 规格**：
- **A1+A2（同一件事）**：Kernel 加"显式输入端口"（外部输入作为事件入 journal，不经 Resolve；replay 只需 initialWorld+journal）+ SimulationCursor（承载 now/守卫/预算，可暂停恢复）+ StopReason（Exhausted/BoundaryReached/DecisionRequired）+ "切 N 段==一次跑完" property。这是 WP9 的 Kernel 侧地基（建议拆 WP9a）。
- **A3**：WorldVersion=(LineageId,LogicalTimestamp) 由 Loop 生产；Protocol 补 Microstep/LineageId。
- **A4 身份部分**：SourceId 必须由世界持久身份派生（写成正式决策）。
- 注意：DecisionRequired 的触发机制需要设计——候选/事件如何声明"需要决策"？我的初步倾向：特殊事件 kind（如 decision.requested）由 system 产生，Loop 识别后停机并在 StopReason 携带上下文；或 Resolve 返回值增加"请求暂停"通道。派发前想清。
- WP10 前置（评审遗留）：A4 仲裁剥离、A8 RNG 坐标纪律、A9 system 无状态化、A6d property。WP11 前置：A5 provenance。全部在 002"下一轮建议"留档。

## 5. 下一轮队列（也在 002"下一轮建议"）

1. WP9a：Kernel 侧——输入端口 + Cursor + StopReason + WorldVersion + SourceId 身份化 + 切分等价 property
2. WP9b：Host 侧——IPlayerDriver + Scripted/Random/Null + Protocol 补字段
3. WP10 前置清单（A4 仲裁/A8 RNG 纪律/A9 system 动态化/A6d）→ WP10 FirstBoard
4. WP11 前置：A5 provenance envelope 变更

## 6. 协作模式要点（实践验证过的）

- 派发 prompt 结构：环境说明+已有基础清单+按序阅读清单+任务规格（含"探索并报告张力"）+硬约束+验收标准+报告要求。**subagent 无对话记忆，必须自包含。**
- coder 可信度高（多轮报告与实际零偏差），但主线仍独立跑 `dotnet test` + 抽查关键接口形状；coder.md 已约定：不改状态表、不 commit。
- 用户偏好：中文；精力有限，**异步纠偏**（方向级问题列选项+我的倾向，先按倾向推进不阻塞）；额度到期前尽量多推进；授权本地 commit；**不 push、不改 atelia、游戏设计愿景级取舍留给用户**。
- 终端坑：run_in_terminal 可能剥掉 `cd/Set-Location` 前缀沿用旧 cwd——当前恰好都在 dream-board，但要留意输出异常（曾出现静默空输出）。
- 状态表备注是设计裁决的留档处；"下一轮建议"每轮结束必须更新——这两者+本文件构成跨压缩记忆闭环。

## 7. atelia（远期 WP11 才用）

- 真 repo：`e:\repos\Atelia-org\atelia`（`e:\repos\atelia` 是无关旧 Python 项目）。net10.0。
- EventJournal：git 风格事件父链+branch/fork+opaque payload——落盘 adapter 目标库；依赖闭包 Data+Primitives+Rbf+RbfSegmentStore（4-5 项目）。
- StateJournal：侵入式对象图（DurableObject/Durable* 容器）——只用于远期 checkpoint/lineage，**永不进 Kernel 领域模型**。
- SessionJournal：不稳定，第一阶段不碰。
