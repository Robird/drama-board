# DramaBoard 当前项目状态

> 跨会话入口；维护方式见 [AGENTS.md](AGENTS.md)。按本文件选择下一处阅读，不必顺读历史记录。
> 最近整理：2026-10-27；应用层 ResourceCycle 预测失效原型已贯通。运行与证据见[实例验收](docs/experiments/resource-cycle-acceptance.md)。

## 最终目标

探索 LLM-Native 的开放世界游戏：用真实可玩场景逐步选择角色、模拟、棋类与 RL Gym 的机制。领域世界应可持续演化，同时保持时间推进确定、空间行为可解释、领域代码易写。DramaBoard 作为 DurableGraph 的真实消费者，持续反馈声明式增量持久化与 Schema 演化的需求。

## 当前实现锚点

- **Kernel**：统一 Occurrence 仲裁；scratch-fold 后发布独立 E/S，S 成功才安装世界；有限 [KernelCursor](src/Kernel/Simulation/KernelCursor.cs) 只支持完成已记录 pending。见 [SimulationKernel](src/Kernel/Simulation/SimulationKernel.cs) 与 [E/S 设计](docs/design/durablegraph-occurrence-persistence.md)。
- **Spatial**：Graph Slice 1/2 已实现；contact 使用 floor，arrival 使用 ceil，保留严格内部掉头与 current-segment 配对消费。见 [GraphSpatialState](src/Spatial/State/GraphSpatialState.cs)、[Graph 设计](docs/design/graph-spatial-world.md) 和 [contact 证据](docs/worksets/passage-contact-floor.md)。
- **Host**：通用 [SimulationHost](src/Host/SimulationHost.cs) 循环驱动 Kernel，保留在目标核心中。
- **ResourceCycle 应用实例**：[CycleEngine](src/ResourceCycle/CycleEngine.cs) 依赖路由/dirty 重算与全量对照；[CycleDurableHistory](src/ResourceCycle/Persistence/CycleDurableHistory.cs) 单根 E/S 与 pending 恢复；[Console](src/ResourceCycle.Cli/Program.cs) 提供脚本运行与诊断。没有修改时空核心，见[设计](docs/design/resource-cycle-demo.md)。
- **构建依赖**：Kernel/Spatial/ResourceCycle 直接引用相邻 DurableGraph 当前源码及 Generator/schema 工具；ResourceCycle 和 Spatial.Tests 的 Persistence 也来自同一源码图。Storage 沿用 DG 的 revision 校验，验收显式运行 Schema Verify。源码根与使用边界见[源码依赖说明](docs/source-dependencies.md)。

## 当前焦点与下一步

用户已确定围绕现有时空模型在本仓库建设下一代引擎与一组实例游戏；Mud 保留为可玩参照，选择性借鉴其动作、观察、事件与状态关联经验，不进行原地迁移或兼容适配。新业务逻辑先写应用层，经实例验证后再提取共性。

首个实例为昼夜变化、太阳花（白天 3 小时成熟）、月光草（夜晚 4 小时成熟）与程序化销毁，不含角色、背包、物品或空间。原型已通过增量/全量轨迹对照、跨进程续跑与 E-only 恢复；详细边界见[验收](docs/experiments/resource-cycle-acceptance.md)。

当前可用模式为预测者声明依赖、事件粗筛/细筛失效、dirty 去重后统一重算；暂停时仍保留订阅。原型规则为昼夜暂停保留进度、销毁清零，同刻成熟→光照→销毁。固定绑定用于预测失效，尚非通用业务广播；候选扫描等仍有线性成本。下一步检视业务编写与诊断体验，再按真实需要选择动态预测者生命周期或其他业务实例；不自动展开 Spatial 重构。

当前构建包含三个核心、ResourceCycle 库/Console 及测试；其余旧应用层采用固定 tag 与[仓内归档](docs/archive/player-runtime.md)保存，不因新实例方向自动解冻。

Dynamic-Programmer R1–R8 的既有实现、验收、Player 运行时理念与长期 Plan 设计统一保留在[Player 归档](docs/archive/player-runtime.md)，不驱动当前施工。

FirstBoard 场景、Demo、LLM Player、实际持久化 adapter 和专属资料此前已移至[归档索引](docs/archive/firstboard-llm.md)，不参与默认编译或搜索。

## 延期与重开条件

- **Mud 成果复用**：按新实例的真实需要选择内容、独立库或设计经验，不搬迁整套领域/Session，不要求旧存档兼容。
- **多人采集与 Player**：资源周期实例验证后，再增加 2–3 个 Player、两个地点、角色/背包等子图。已定最简边界为行动产生结果后请求决策，等待合法输入期间暂停逻辑时间，Human 与 LLM 共用；具体同刻输入顺序未定。长期 Plan 等旧实现保持归档。
- **持久化扩展**：ResourceCycle adapter 仅服务本实例的单分支闭包；其他游戏、分支或空间持久化依据真实需求扩展，不把它视为通用 host。归档 FirstBoard 的结果不作为新实例的落盘证据。
- **分支、续局与外部调用**：可玩 fork/倒带、完整 Player closure、LLM 调用恢复、异常/retry 历史按真实需求重开；DurableGraph 不透明恢复 Task、LLM 调用或执行栈。
- **Spatial 演进**：用户预期未来也可能改为显式预测模式，先以资源周期实验取得证据，不将空间重构设为本轮前置；其他扩展按实际需求重开。
- **持久 Script VM**：研究材料已归档；只有需要持久执行状态时才重新选择方案。

## 按需导航与验证

- 两份等价入口 [DramaBoard.slnx](DramaBoard.slnx) 与 [DramaBoard.Local.slnx](DramaBoard.Local.slnx) 包含核心与当前实例。[README](README.md)与 [CI](.github/workflows/ci.yml)直接构建源码依赖，使用精确 SDK 10.0.201；第三方包来自 nuget.org，保留仓内包缓存。旧包准备脚本仅保留历史复现用途，当前不要求发包。试运行：`dotnet run --project src/ResourceCycle.Cli -c Release -- --trace`。
- 文档入口：[docs/README](docs/README.md)。FirstBoard/LLM 的恢复路径、清单与原始基线在 [归档索引](docs/archive/firstboard-llm.md)；其正文必须显式 `rg --no-ignore` 查阅，不能作为当前待办或测试基线。
