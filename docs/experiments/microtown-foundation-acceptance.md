# 小镇首片：共享模块与内存经济验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

> 历史记录：Microtown 源码、测试和 smoke 已于 2026-11-02 从主线移除，见 [Git tag 归档](../archive/microtown.md)。本文命令、实现状态和测试结果属于当时版本。

日期：2026-10-31。范围仅为[小镇目标](../codex-goals/microtown/README.md)的第一实施片，**不是完整里程碑验收，也不是真实模型试玩**。

## 实际交付与双消费者边界

| 模块 | 当前消费者 | 游戏保留的职责 |
| --- | --- | --- |
| [Goals](../../src/Goals/README.md) | ResourceCycle 的角色交互；Microtown 的 `TownPlanning` | 角色归属、上下文序号、原子发布与呈现 |
| [CharacterControl.Completion](../../src/CharacterControl.Completion/README.md) | ResourceCycle 的薄 `LlmCharacterController`；独立测试使用两种游戏材料 | 冻结角色观察、能力政策、渲染、命令校验；小镇真实控制器尚未接入 |
| [CharacterGym](../../src/CharacterGym/README.md) | ResourceCycle CLI 的 GymSession/GymCommands | 完成 S 定位、fork、角色可见材料和存档修复；小镇适配尚未接入 |
| Kernel / Spatial / Inventory | 原游戏与新 [Microtown](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/README.md) | 小镇的配方、有限市场、生活需要和跨模块结算 |

新的共享程序集不引用 ResourceCycle。没有统一世界根、观察全集或游戏命令枚举；共享调用输入只是已经渲染好的材料，不是新的持久 Context。

小镇通过真实 Kernel 的 E/S 内存路径完成三角色 48 小时脚本：生产饭和木器、消费、进口/出口、经过市场刷新，三人存活。测试逐边界独立核对钱物账，最终进口支出 60、出口收入 84、现金总额 78；产饭 12、木器 6，出口饭 3、木器 6。原料进入在制品不算消耗，完成才记配方消耗；失败退回未完成原料，终点保留在制品。该脚本证明初始经济存在可行路径，不证明模型能找到或最优经营。

## 工程验证

主线程集成后实际运行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1 -OutputDirectory artifacts/microtown-extraction/survival
pwsh -NoProfile -File scripts/Smoke-ResourceCycleGoals.ps1 -OutputDirectory artifacts/microtown-extraction/goals
```

- Build：零警告、零错误，Schema Verify 通过。
- 全 solution：11 个测试项目、1130 个测试通过，无失败或跳过。新增 Microtown 12 项；共享 Goals 31 项、CharacterGym 29 项、共享调用循环 3 项。原游戏集成测试仍覆盖角色隔离、Goal 恢复、Gym cutoff/fork 与反馈协议。
- 两项真实 Console smoke 通过，原始输入/输出和 acceptance.json 保留于上述 artifacts 目录；这些是 Coding Agent 脚本，不能代替人类或模型试玩。
- 构建/全测试输出保留于 `artifacts/microtown-extraction/build.log`、`tests.log`（本地忽略工件）。

独立审查发现并复核了以下修复：

1. 提取后原渲染回调跑在共享循环串行门外，恢复为整个 adapter 服务串行；测试确认预取消不渲染、并发调用不二次渲染、完成后可再次服务。
2. Gym 构建指纹加入共享 Goals/控制器/Gym 程序集；实际 session 测试核对指纹、字符串状态及完整操作结果字段。格式 1 数字 Interrupted 继续可解释。
3. 小镇最初遗漏 `SpatialContactOccurrenceRule`。独立 [TownSpatialReviewTests](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/tests/Microtown.Tests/TownSpatialReviewTests.cs) 先以默认工厂合法反向移动证明相遇事实缺失，再以相遇与到达之间的第三居民饥饿事件证明过期预测异常。两条测试先失败，接入候选/规划及 `ContactOccurred` 后通过；未扩展途中交流玩法。

## 存档及未完成边界

ResourceCycle GameRevision 为 16。共享 Goals 使用新的持久身份，5 个原游戏引用 Schema 显式升版；旧开发图没有兼容别名或升级器，可能先在 DG 还原时拒绝。当前新图的目标 pending/冷开测试通过；修订拒绝夹具使用当前 Schema 加旧 GameRevision，并验证拒绝不改盘，不把它当成所有旧二进制存档的迁移验证。已有旧试玩材料未改写。

小镇目前没有磁盘 adapter、Console、角色可见冻结 Context、LLM 或 Gym 接线，也没有居民间交流/赠与/报价。`TownDecision.World` 是开发者脚本接口，不能直接交给模型。Town 自身 Forecast 暂为全量派生；owner 缓存、通知唤醒、忙碌即时操作、宿主预算仍待实现。下一步与所有剩余项只在[工作计划](../codex-goals/microtown/work-plan.md)维护；完整目标保持进行中。
