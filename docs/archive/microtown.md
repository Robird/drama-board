# Microtown 历史归档

2026-11-02。Microtown 的经济、生产、市场与居民交易已融合到[山野镇 / Hillside](../codex-goals/living-world/README.md)。旧领域、Console、Completion 适配及配套测试从主线移除，不再参与 solution、CI 或共享模块回归。

## 固定归档

- Annotated Git tag：`archive/microtown-2026-11-02`。
- 指向移除前提交：`b6fea5e94ca3ea718cee2546a2d785afa1bafa6d`。
- [固定源码树](https://github.com/Robird/drama-board/tree/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d)包含三个源码项目 `src/Microtown`、`src/Microtown.Cli`、`src/Microtown.Completion`，三个同名测试项目及 `scripts/Smoke-Microtown.ps1`。
- 主线保留历史设计和实验正文；指向移除源码的链接固定到上述提交，不再引用当前工作树中的旧路径。

Tag 在本地创建；需要远端共享时单独推送 `git push origin archive/microtown-2026-11-02`。本次归档不自动推送。

只读查阅或独立检出：

```powershell
git show archive/microtown-2026-11-02:src/Microtown/README.md
git worktree add --detach ../drama-board-microtown-archive archive/microtown-2026-11-02
```

旧版本仍需其当时的 .NET SDK 与相邻 DG、Storage、Completion 源码；本仓 tag 不会冻结其他仓库。历史融合验收所用版本见[阶段验收](../codex-goals/living-world/acceptance.md)。不要将旧源码复制回主线或让当前世界读取 Microtown 存档。

## 行为覆盖与维护边界

移除前核对了旧测试与现有消费者。保留机制的回归入口如下；旧 Town 专有的饥饿模型、固定实验终局、队列容量和独立存档身份测试随原型归档，不作为山野镇兼容合同。

| 机制 | 当前回归入口 |
| --- | --- |
| 生产、退料容量、市场竞争、赠钱与资金/物品守恒 | `HillsideEconomyTests`、`HillsideEconomyAcceptanceTests` |
| 居民报价、原子成交、期限、死亡、参与者隐私及 E-only 恢复 | `HillsideTradeTests`、`HillsideTradeAcceptanceTests`、CLI 的 `TradeHostTests` / `TradeViewTests` |
| 同根镇山往返、冷开与内容绑定 | `HillsideFoundationTests`、`HillsideContentAcceptanceTests` |
| 预测维护、同刻仲裁与恢复 | `CycleMaintenanceBoundaryTests`、`CyclePersistenceTests`、`SurvivalForecastTests`及 Kernel/Spatial 测试 |
| 共享模型循环、连接与预算隔离 | `CharacterControl.Completion.Tests`、`ResourceCycle.Completion.Tests` |
| Gym 精确 S 分叉、冷开、导出及隐私 | `CharacterGym.Tests`、`GymBranchTests`、CLI 的 `GymIntegrationTests` |

未为继续提供第二个消费者而保留死代码，也未把 Town 专用实现移入共享库。新增玩法在 ResourceCycle 系列推进，共享机制继续由自身测试及当前真实消费者验证。

历史材料：[原型设计与目标](../codex-goals/microtown/README.md)、[原总验收](../experiments/microtown-acceptance.md)、[真实模型试玩](../experiments/microtown-playtest.md)。这些记录解释融合来源，不是待重新启动的任务。

## 移除验收

2026-11-02 在 Windows、SDK 10.0.201 下，对移除后的 `DramaBoard.slnx` 串行执行 Release 构建（`-m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`），零警告、零错误；随后使用 `--no-build --no-restore` 排除 `LiveE2E` / `LocalE2E`，10 个测试项目共 1,338 项通过，零失败、零跳过。构建图和运行代码中无 Microtown 引用；受影响文档的本地链接与归档源码目标均已检查。本轮未进行真实模型调用或 Linux 验证。

原始构建日志与 TRX 位于忽略目录 `artifacts/microtown-removal-build.log`、`artifacts/microtown-removal-tests/`。本地旧项目的忽略 `bin/obj` 缓存因自动审批策略阻止递归删除而保留；其不在 Git 主线和构建图中。
