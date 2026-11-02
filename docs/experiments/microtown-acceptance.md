# 微型小镇与多游戏共享代码库：总验收

> 历史记录：Microtown 源码、测试和 smoke 已于 2026-11-02 从主线移除，见 [Git tag 归档](../archive/microtown.md)。本文命令、实现状态和测试结果属于当时版本。

2026-10-31，[本轮六项完成条件](../codex-goals/microtown/README.md)均已有工程与运行证据。生产代码基线为 `d5a9b89`；最后一片只增加真实试玩证据和交付文档，没有为通过试玩临时改变规则。Town 接受 GameRevision 3，ResourceCycle 接受 16，旧开发存档不迁移。

本次交付的是第二款独立、有限的小镇游戏和两游戏实际使用的共享代码。没有开始 Character Gym 二期、第三款游戏或通用角色记忆系统。

## 完成条件对照

| 条件 | 实际交付与证据 | 证据边界 |
| --- | --- | --- |
| 1. 独立可运行的小镇 | 三居民、三地点、饱食、库存、采购、两种耗时生产、消费与有限外销；同地交流、赠与、报价与双边原子交换。[产品入口](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/README.md)、[社交](microtown-social-acceptance.md)、[交易](microtown-trade-acceptance.md)与[真实试玩](microtown-playtest.md) | `--llm` 三居民独立决策；默认 Human 便于调试，`--llm --human a` 可混合控制。模型社会成交有开发提示，不宣称自发合作 |
| 2. 多游戏真实复用 | Goals、CharacterControl.Completion、CharacterGym 已由山林和小镇实际调用，另复用 Kernel/Spatial/Inventory；[新增游戏指南](../implementation/adding-a-game.md)逐项列出两个调用点 | 观察、根、规则、持久化适配和 Gym 边界仍由各游戏拥有，没有统一万能世界或将小镇变成山林开关 |
| 3. 经济可核对 | [TownEconomicTotals](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownModels.cs)核对现金、物品、在制品及分类流量；[TownStatistics](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/TownStatistics.cs)显示全部居民与最差生活值。真实父局现金 `54−162+204=96`，物品逐项平衡，见[试玩账表](microtown-playtest.md) | 区间为新游戏至当前状态，fork 含继承前缀；失败居民仍计入。库存不虚估为现金，现金不是福利 |
| 4. 恢复与分叉 | [持久化](microtown-persistence-acceptance.md)、[Gym 接线](microtown-llm-gym-acceptance.md)、[交易恢复](microtown-trade-acceptance.md)与[预测恢复](microtown-prediction-acceptance.md)覆盖 S、pending、发布失败、精确 fork。当前真实反馈 fork 另进程完成 10 条新操作，源分支与角色证据隔离 | Open 不执行 pending，恢复不重问控制器。仅支持完成 S 决策前态与串行分支，不恢复原生模型工具 Tail |
| 5. 工程与实际体验 | 确定性 48h 经济循环、拒绝/竞争/失败/恢复测试；三真实 LLM 父局跑至预定 30h，越过 24h 市场刷新，两个商品均生产并外销。再完成反馈分叉与有引导交易分叉，详见[试玩](microtown-playtest.md) | 父局达到实验终点；两个子局预算暂停，不冒充终局。108 次调用、106 次成功操作均有原始材料及独立复核 |
| 6. 旧游戏与开发入口 | 当前 solution 回归含山林领域、CLI、Completion、Context/Goals/Gym；提取时另跑山林完整 Console 路线和目标工具。提供[启动说明](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/README.md)和[新增游戏路径](../implementation/adding-a-game.md) | 山林实际人类与真实模型合作通关仍是原先独立延期项，没有借小镇验收宣布完成 |

## 工程基线与旧游戏证据

最后一次生产代码变更后的完整验证记录于[预测验收](microtown-prediction-acceptance.md)。主线程实际运行：

```powershell
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet test DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1 -OutputDirectory artifacts/microtown-predictions/console
```

CLI 零警告、零错误；13 个测试项目 **1,289 通过、0 失败、0 跳过**。其中山林领域 410、CLI 340、Completion 45；共享 Goals 31、Gym 29、控制器 11；小镇领域 142、CLI 15、Completion 6，另含核心与库存。完整原始输出在 `artifacts/microtown-predictions/tests-solution.log`。本片试玩前再次进行 CLI Schema Verify 构建并固定程序集哈希；之后只有文档改动，没有把前片全量测试描述成本片重新运行。

共享提取阶段的产品验证保留在 `artifacts/microtown-extraction/survival/` 与 `goals/`，两者均有 `acceptance.json` 和原始 Console 输出。前者包含道路/信号两种逃生路线、失败、冷开、路途警告及错误模式只读拒绝；后者包含目标创建/共享/更正/查询失效、生命周期、容量与冷开。之后共享连接与 Gym 的变化由上述完整 solution 回归覆盖；本轮没有重新运行真实模型山林旅程。

领域当前版本与 Schema 的拒绝合同见[领域说明](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/README.md)及[交易验收](microtown-trade-acceptance.md)。游戏身份、修订号和分支三者各自校验，不靠相同版本号猜测游戏类型。相邻 Completion 的选中连接解析增强在本地提交 `ef78922`，本轮未推送或发布包；源码开发入口仍依赖相邻仓库布局。

## 真实模型与主线程复核

[试玩报告](microtown-playtest.md)保存预算、终态账表、真实反馈和精确 S/request/branch 身份。原始证据及脚本位于本地 `artifacts/microtown-economic-day/`，由 Git 忽略：

- 父局 82 次模型请求、81 条操作，三人均完成两种生产、消费、采购与外销，30h 正常结束；没有使用模型外部脚本代选行动。
- 反馈子局 11 次请求、10 条操作，首请求保存投影与源请求一致，父局未来不进入子局。实际世界 E/S 与 Gym 回执逐条匹配。
- 社会子局 15 次请求、15 条操作，真实 `offer → accept → continue`；独立读取转换前 S、事实 E 和完成 S，确认付款与物品一次交换，原等待 ID 和截止保持。
- 主线程检查原始 Console、真实领域账本、回执和导出，独立审阅代理分别核对经济、UI、分叉与隐私。只读审计前后存档文件哈希和分支 head 均一致。代理报告与进程 exit 0 均未单独作为通过依据。

工件中的 verifier 证明特定记录、分支和角色隔离，并非任意文本语义泄漏的通用证明。仓内测试长期保留规则与恢复回归；如果本地工件被删除，本文仍保留结论和定位，但不等于能从 Git 重新取得那次模型响应。

## 保留的限制与后续入口

没有本里程碑内的必要剩余项。[工作计划](../codex-goals/microtown/work-plan.md)保留后续重开条件，具体体验观察只在[试玩报告](microtown-playtest.md)维护：批量生产的生存预览、整数小时等待、材料长度，以及一次性意图的跨回合记忆。它们没有在本轮被包装成已解决能力。

小镇有界交互/报价交付并非完整 Context 历史；控制器的固定开发提示也不是持久语义记忆。自动改进闭环、统计 A/B、长期经济平衡、结构化经历召回、并发分支与原生工具会话恢复均需新的明确目标。第三游戏出现时再检验当前模块是否仍合适，不预先将小镇交易规则抽为通用框架。
