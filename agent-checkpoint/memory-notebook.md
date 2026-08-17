# 主线会话 Checkpoint（memory-notebook）

**用途：上下文压缩前的主动快照。压缩后的主线会话（我）在 Observe 阶段读此文件恢复完整工作状态。**
**更新时机：每次用户触发压缩前，或每轮结束时按需刷新。本版：2026-08-17 第五轮结束（A5+WP11 完成——**WP0–WP11 全线收官，第一阶段达成**；第二次评审 S1–S14 已裁决为第二阶段门槛；主 slnx 154 / Local.slnx 159 测试绿）。**

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

## 3. Kernel 当前形状（认知快照，代码在 src/）

- **D9 已落实（强制 event-sourcing）**：`ISimSystem.Resolve` 只返回事件；world 由 Loop 把已提交事件交给全局 `IEventReducer` 折叠。
- **WP9a 重塑后的 SimulationLoop.Run 签名**：`Run(world, SimulationCursor, until, journal, externalInputs?)` → `SimulationRunResult{World, Cursor, StopReason, Version, DecisionEvents}`。cursor 承载 now/同刻预算/no-op 守卫，跨 Run 不重置；决策停机谓词构造注入（控制流不入 journal）；externalInputs 在 cursor.Now 提交过 reducer 入 journal——**replay 只需 initialWorld+journal（评审 A1 已修）**；WorldVersion=(LineageId,EventCount)。
- `Time/`：ModelTime（1 tick=1ms）、ModelDuration、Microstep、LogicalTimestamp。`Scheduling/`：EventCandidate（**Generation 已删**）、ForecastQueue（tie-break (Due,SourceId,CandidateId)，**InvalidateSource 已删**）。
- `Journal/`：DomainEvent(Timestamp, EventKind(Id,Version), Payload)、EventKindRegistry、InMemoryJournal（严格递增）。路由只按 Kind.Id。
- `Random/`：DeterministicRandom 坐标寻址 + **DeriveStreamId(long/string)**（持久身份→streamId，禁字面量）；采样坐标入事件 payload（journal 自含可审计）。RNG 常量=replay 契约。
- **SourceId 一律由世界持久实体 id 派生**（A4 身份化）；同刻争抢用**顺序无关仲裁模式**（夹宝模型示范：胜者由 RNG 坐标决定与 Resolve 顺序无关，tie-break 只决定执行顺序）。
- `src/Host/`（WP9b）：IPlayerDriver（Null/Scripted/Random-SplitMix64）+ PlayerDecisionSession（单线程顺序编排：requestBuilder/decisionTranslator 注入，版本校验严格；async 边界仅在 Host）。
- `src/Protocol/`：DTO 已含 LineageId/Microstep（A3 修复）。零依赖。
- 测试 136 绿：Kernel 114 + Protocol 12 + Host 10。玩具模型新增：生灭世界（动态实体）、夹宝（仲裁）、Host 层岔路决策模型。A6d property 覆盖全部 system（曾抓到 Bouncing/InterruptedMining 的真实 now 依赖并修复）。

## 4. 当前焦点：第二阶段门槛修复（下轮开始）

- **WP0–WP11 全部完成**。A5 provenance（EventCause/批次/fork 边界）与 WP11 adapter（src/Journal.Atelia，双 solution：主 slnx 无 atelia 保 CI、Local.slnx 全量；JSON envelope 确定性字节已验证；CI 钉到 DramaBoard.slnx）都已落地。
- **第二次评审（研发计划_004，攻击 Host/落盘层）S1–S14**，已裁决四个修复组（详见 002"下一轮建议"）：α 感知/反馈闭环（S1+S2+S13，FirstBoard+Protocol）→ β Host 决策状态机+同刻屏障（S4+S3+S5）→ γ 持久化契约（S6+S7+S8，含 FirstBoard 真落盘验收）→ δ 速修（S11 EventKind 相等只比 Id、S14 重入防护、S9 Intent 数值域、S10 external 批次谓词、S12 RandomDriver 坐标寻址）。
- 关键教训留存：A2/A4/A7 的修复在 Kernel 层做对了，但没有"继承机制"——Host/装配层重蹈覆辙（S4 重现 A2、S3 架空 A4、S11 架空 A7）。修复时优先把约定变机制（如 EventKind 相等语义）。
- 第二阶段方向（修复后，需用户确认）：AI Player driver（LLM）、更丰富 Board、Godot 前端。全部在 002"下一轮建议"留档。

## 5. 下一轮队列（也在 002"下一轮建议"，详细裁决在研发计划_004 头部）

1. 修复组 δ（速修，建议先做——S11 是机制级且波及面小）：EventKind 相等只比 Id、Session 重入防护、Intent 数值域、external 谓词扫描、RandomDriver 坐标寻址
2. 修复组 α：感知/反馈闭环（FirstBoard affordance 从信念计算、拒绝可感知、placed/carried）
3. 修复组 β：Host 决策生命周期状态机 + 同刻决策屏障（整批回应整批提交）
4. 修复组 γ：持久化契约（lineage 元事件、批次完整性、cursor 快照、codec 升级、FirstBoard 真落盘）
5. 全部完成后：第二阶段方向征询用户（AI Player/更丰富 Board/Godot）

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
