# Microtown

这是独立小镇游戏的领域实现，完整里程碑仍在施工。`TownGameFactory.Create()` 创建三居民、市场/工坊/住区与 48 游戏小时的有限实验。引用共享 Kernel、Spatial、Inventory、Goals，不引用 ResourceCycle。Human Console 入口见 [Microtown.Cli](../Microtown.Cli/README.md)。

当前支持 `buy <item> <count>`、`sell <item> <count>`、`produce <recipe> <batches>`、`eat`、`wait <hours>`、`go <passage>` 和共享 Goals 命令。交易需在市场，生产需在工坊，移动复用 Spatial。库存无限；每居民只能一个耗时活动。

- 开始生产将原料从背包移至在制品，到期才计配方消耗和产出。饥饿失败退还未完成原料到遗产，失败者仍计入人口及最差生活值。实验结束保留未完成在制品。
- 初始现金总额 54，三个居民各一份饭、饱食 60/100；每小时饱食减 3，饭加 40。原料、配方、外部有限供需见 `TownContent` / `TownMarket.Fresh`。刷新只重置外部市场，未伪造居民进口。
- 同刻顺序为饥饿失败、市场刷新、Spatial 到达/路上接触、活动完成、实验结束、角色决定；同类候选采用 Kernel 的确定性仲裁。路上接触消费 Spatial 的 typed fact，暂不触发交流，避免预测被跳过或过期。恰好饥饿归零时才产出的饭不能挽救居民。
- 即时成功操作轮转服务其他空闲居民；拒绝保留当前角色。每次新服务先发布 `DecisionOpened` 完成 S，下一 Step 才调用控制器。`CreateControlledKernel` 只交付冻结 `TownObservation`；返回原始输入和操作身份，再由游戏解析。本人最新操作回执与最新活动经历分别保存，不允许控制器暗中保存世界。
- `TownEconomicTotals` 是同根累计账权威，逐边界核对现金及逐物品的背包＋在制品余额。钱、饱食、件数分别呈现，不合成评分。

`TownDurableHistory` 提供独立 DG 根/事件，二者都标记 `microtown` / GameRevision 1；只接受本版本。Open 还原完成 S 和 pending E，再重建派生缓存，不执行事件或写盘；Kernel 的 `RecoverPending` 应用已保存结果，不重新询问控制器。`CaptureDecisionBoundary(actorId)` 捕获精确完成 S；拒绝后同一 DecisionSequence 对应新的 S。`Fork` 只接受指定源分支历史中的完成决策 S，保留原游标、lineage 和累计经济账，子分支独立延续。

`TownObservation` 只有本人私有状态、同地居民公开身份/活动，以及可知的地图和经济规则。实时市场余量只在市场可见；其他地点只显示公共价格和刷新时刻。`TownTextRenderer` 仅从冻结值产生 Markdown，列出动作形状、合法数量范围和阻因。

边界：尚未接入 LLM/Gym 宿主、交流/报价或忙碌通知。当前仅保留最近一条操作回执与本人活动结果，不是完整 Context 历史；社交切片需进一步完善。`TownDecision.World` / `CreateKernel` 保留为开发者脚本接缝，包含其他角色私有状态，**不能直接传给模型**。尚不暴露 cancel。Kernel 的同刻护栏不替代宿主预算，Console 另有每次运行操作上限。

当前 Town 预测每次 Forecast 从业务派生；Spatial 仍使用已有增量缓存与 Rebuild/Complete。尚未把 Town 预测按 owner 增量维护。`TownFact` 保存完整不可变业务替换值和强类型空间事实；E 与 S 是独立持久图，恢复不依赖跨图引用相等，也不重新规划。领域状态及回执均已有 DG Schema。

验证：`dotnet test tests/Microtown.Tests/Microtown.Tests.csproj -c Release -m:1`。测试通过真实 Kernel 路径覆盖三居民 48 小时经济循环、市场刷新、竞争、拒绝、决定前完成边界、不可变旧快照、失败及在制品核对；不是实际模型试玩证据。
