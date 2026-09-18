# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-09-19；Dynamic-Programmer 施工 R1/R2 已落地，见[工单索引](docs/implementation/dynamic-programmer-build/README.md)；上一里程碑为 [0025 验收](docs/build-log/0025-verification.md)。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；scratch-fold 后发布独立 E/S，S 成功才安装世界；有限 [KernelCursor](src/Kernel/Simulation/KernelCursor.cs) 只支持完成已记录 pending。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：Graph Slice 1/2 已实现；contact 使用 floor，arrival 使用 ceil，保留严格内部掉头与 current-segment 配对消费。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[Graph 设计](docs/design/graph-spatial-world.md) 和 [contact 证据](docs/worksets/passage-contact-floor.md)。
- **Player 与协议**：冻结的 Observation、DecisionRequest、Intent 和稳定身份由 [Protocol](src/Protocol) 持有；[Player](src/Player) 提供 Null/Scripted/Random driver，Decision.Validation 负责请求关联和动作校验，Player.Agency 提供已知图快照接缝。
- **构建依赖**：Kernel/Spatial 使用固定 DurableGraph 包；来源和复现规则见 [包准备说明](docs/worksets/durablegraph-package-source.md)。已提交的 Kernel/Spatial schema history 不因本批改变。Server 前端静态资源的 .NET 10 清单故障（曾致 CI 的 Server.Tests 长期红）已用 Server.csproj 的 `EnsureWwwrootDirectory` 空目录目标修复，根因与已否决方案见 [wwwroot 调查](docs/worksets/server-wwwroot-staticwebassets.md)；本地全量测试绿，CI 推送后确认转绿。
- **本地可玩入口**：[Server](src/Server/Program.cs)持有单角色内存 [FreePlaySession](src/Server/FreePlay/FreePlaySession.cs)，[WebUI](src/WebUI/src)显示地图、时间、位置与自身轨迹；诊断独立只读取数。发布启动见 [README](README.md)，Windows 实际 apphost/Chromium 证据见 [0025 验收](docs/build-log/0025-verification.md)。

## 当前焦点与下一步

活跃核心固定为 Kernel、Spatial、Protocol、Player、Decision.Validation、Host、Player.Agency 及各自测试。FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料均已移至 [归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

当前应用边界为四点全图已知场景，只提供相邻 `action.travel`；出发/到达均由 Kernel 提交，等待不走模型时间。刷新继续同次运行，服务重启回 genesis；诊断仅展示最近 100 条完成记录，自身轨迹保留整次运行。七核心合同保持不变。

已形成 [Player 运行时与 Plan-Maintainer 关键设计理念](docs/design/player-runtime/README.md)，经[独立审视与交叉质询](docs/worksets/player-runtime-principles-review.md)完善。该文集中维护用户已确认的共享运行时、未来安排与“再想想”方向；当前源码尚未实现这些能力。

已形成 [World VM / Player Process / Dynamic-Programmer 模型](docs/design/player-runtime/dynamic-programmer.md)，基础模型与 Stay 补充均经[辩证审查](docs/worksets/dynamic-programmer-review.md)收敛。采用顺序 Move/Think/Stay：完整合法程序统一授权执行，合法空响应同次接受为 `[Stay]`；`[Stay, ...尾部]` 表示保留安排而待机，替代独立继续/停驻处置。普通耗尽仍先请求 Programmer，非法输入和故障不默认 Stay；解除待机与失败降级延期。

工程设计已定稿并形成 [Dynamic-Programmer 施工工单](docs/implementation/dynamic-programmer-build/README.md)（R1–R8，每轮一个 fork 会话按序施工，逐轮 build-green）。已定基线：强类型 Move/Think/Stay 指令与不可变 Buffer；互斥执行状态，RunningStay 为唯一待机权威、运动真相读 Spatial；Programmer 完整修订原子接受，合法空程序同次规范为 `[Stay]`；到达与进程状态同批提交。候选映射、ControlRevision、组件落点与无效响应分通道等裁决见工单 README。新机制整体替换旧 Intent 单发决策路径（R7 切换、R8 删旧）。R1（Protocol 契约类型）与 R2（`IDynamicProgrammer`、脚本驱动器与响应校验器）已落地，各自测试全绿，新类型尚无生产消费者；下一步 R3（Process 领域状态/facts/reducer），进度以工单 README 状态表为准。

## 延期与重开条件

- **后续交互机制**：首个移动闭环验收后，依据 Human 游玩与诊断证据逐项选择。探索披露、途中操作、默认行为协议、自动导航、NPC/剧情/物品不进入 0025。
- **持久化消费者**：核心保留 E/S 接缝和内存合同；新场景需要落盘时，从其真实闭包重新设计 adapter、恢复与 Player 同步边界。归档 FirstBoard 的结果只是历史证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 扩展**：调速/途中停留、Area、ViewLink、关系变化与 contact 索引按实际玩法和性能证据触发。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口：[DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx)，均含七核心、Server 及对应测试。先运行 [Prepare-DurableGraph.ps1](scripts/Prepare-DurableGraph.ps1)，再按 [README](README.md)串行构建/测试及[发布](scripts/Publish-Server.ps1)；[CI](.github/workflows/ci.yml)先准备前端，再验证 .NET 与发布产物的 Chromium 行为。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
