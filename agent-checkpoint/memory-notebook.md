# 主线会话 Checkpoint（memory-notebook）

**用途：上下文压缩前的主动快照。压缩后的主线会话（我）在 Observe 阶段读此文件恢复完整工作状态。**
**更新时机：每次用户触发压缩前，或每轮结束时按需刷新。本版：2026-08-16，第一阶段（WP0–WP7）完成后。**

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

- `Time/`：ModelTime（**1 tick = 1ms**，WP4 从 1 秒改来——弹球暴露的）、ModelDuration、Microstep、LogicalTimestamp。checked 溢出。
- `Scheduling/`：EventCandidate<TPayload>（Due=ModelTime，**Microstep 是 Resolve 阶段概念，候选不带**）、ForecastQueue（tie-break `(Due,SourceId,CandidateId)`；generation floor 失效带记忆）。
- `Simulation/`：ISimSystem<TWorld,TCand,TEvt>（ForecastNext/Resolve 纯函数，Resolve 返回 ResolveResult=新 world+未提交事件）；SimulationLoop（**每事件后全量重 Forecast**——失效表现为"下轮不再产生"，ForecastQueue.InvalidateSource 实际未被 Loop 用；owner=(SourceId,CandidateId)；Microstep 由 Loop 按提交序分配、跨时刻重置）。
- `Journal/`：DomainEvent(Timestamp, Kind=string, Payload)、IJournalSink、InMemoryJournal（拒绝时间倒退）。
- `Random/`：DeterministicRandom **坐标寻址纯函数** `(worldSeed,streamId,generation,sampleIndex)→样本`；自实现 ln 级数不依赖 Math.Log（跨平台位稳定）；**RNG 常量与派生规则=replay 兼容契约**（变更需版本化）。
- 同刻语义（WP5 裁决、WP6 测试锁定）：**不上 μ 阶段体系**；顺序 Resolve + tie-break + 因果穿透（同刻先 Resolve 者可使后者不再发生）。
- 测试侧：五个玩具模型（Timer/Reroute/Bouncing/Mining/InterruptedMining，物理与领域全在测试项目——Kernel 不含任何领域内容）；ReplayHarness（re-run determinism 全覆盖 + reducer fold 探索版）；ForkHarness（前缀折叠重建）；CsCheck 4.8.0（仅测试项目）6 条不变量。

## 4. 尚未落盘的思考：D9 的设计倾向（下轮第一件事）

D9 = 统一事件 envelope + IEventReducer 正式契约。核心问题：**"world 由事件产生"是强制还是约定？**
- 方案 a（强制 event-sourcing）：Resolve 只返回事件，Loop 用注册的 reducer 折叠出新 world。D2 的彻底落实，封死"world 与事件描述不一致"缺口。
- 方案 b（宽松双轨）：保持 Resolve 返回 world+事件，用 property test 锁定 fold(initial, events) == world。
- **我的倾向：a**，但需评估五个玩具模型的改造成本与"事件粒度被迫变细"的风险（Resolve 中间态是否都要事件化？）。派发时让 coder 两方案都做小 spike 再定。
- envelope 相关：Kind 应从 string 变稳定 id + schema version 考虑（WP11 落盘序列化需要）；payload 联合类型摩擦中等（WP4 实测），envelope 或 typed adapter 是解法候选。
- 注意：D9 决定后要回写研发计划_001 成为正式 D9 条目。

## 5. 下一轮队列（也在 002"下一轮建议"）

1. D9 决策（spike→裁决→落实，含玩具模型迁移）
2. WP8 Protocol（DecisionRequest/PlayerDecision DTO；envelope 定型后形状才稳）
3. WP9 IPlayerDriver + DecisionPoint（Kernel 首次出现"等待外部输入"暂停点；async 边界在 Host 层，Kernel 保持同步纯函数）
4. WP9 前插 adversarial review（基础设施.md"架构攻击者"清单：同时事件、迟到决策、speculative 失效等）

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
