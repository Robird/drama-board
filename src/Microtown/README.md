# Microtown 内存纵向切片

这是独立小镇游戏的第一片领域实现，不是完整里程碑。`TownGameFactory.Create()` 创建三居民、市场/工坊/住区与 48 游戏小时的有限实验；`TownEngine.CreateKernel` 接入脚本控制器。引用共享 Kernel、Spatial、Inventory、Goals，不引用 ResourceCycle。

当前支持 `buy <item> <count>`、`sell <item> <count>`、`produce <recipe> <batches>`、`eat`、`wait <hours>`、`go <passage>` 和共享 Goals 命令。交易需在市场，生产需在工坊，移动复用 Spatial。库存无限；每居民只能一个耗时活动。

- 开始生产将原料从背包移至在制品，到期才计配方消耗和产出。饥饿失败退还未完成原料到遗产，失败者仍计入人口及最差生活值。实验结束保留未完成在制品。
- 初始现金总额 54，三个居民各一份饭、饱食 60/100；每小时饱食减 3，饭加 40。原料、配方、外部有限供需见 `TownContent` / `TownMarket.Fresh`。刷新只重置外部市场，未伪造居民进口。
- 同刻顺序为饥饿失败、市场刷新、Spatial 到达/路上接触、活动完成、实验结束、角色决定；同类候选采用 Kernel 的确定性仲裁。路上接触消费 Spatial 的 typed fact，暂不触发交流，避免预测被跳过或过期。恰好饥饿归零时才产出的饭不能挽救居民。
- 即时成功操作轮转服务其他空闲居民；拒绝保留当前角色。每次新服务先发布 `DecisionOpened` 完成 S，下一 Step 才调用控制器。真正的角色 Context 将在这个边界加入，不允许控制器暗中保存世界。
- `TownEconomicTotals` 是同根累计账权威，逐边界核对现金及逐物品的背包＋在制品余额。钱、饱食、件数分别呈现，不合成评分。

边界：目前只有内存史与脚本测试，没有 Console、角色可见冻结 Context、LLM、交流/报价、持久适配、冷开或 fork。`TownDecision.World` 是开发者脚本接缝，包含其他角色私有状态，**不能直接传给模型**。忙碌期间尚无通知唤醒，因此不暴露无法自然调用的 cancel 命令；失败回收在制品只是内部规则。Kernel 的同刻护栏不替代后续宿主调用预算。

当前 Town 预测每次 Forecast 从业务派生；Spatial 仍使用已有增量缓存与 Rebuild/Complete。尚未把 Town 预测按 owner 增量维护，后续应按真实通知依赖采用同样模式，不宣称已有通用增量预测框架。`TownFact` 首片保存完整不可变业务替换值，恢复只应用事实而不重新问控制器；领域对象暂未加 DG Schema，后续持久切片需显式设计游戏身份/修订和精确决定边界。

验证：`dotnet test tests/Microtown.Tests/Microtown.Tests.csproj -c Release -m:1`。测试通过真实 Kernel 路径覆盖三居民 48 小时经济循环、市场刷新、竞争、拒绝、决定前完成边界、不可变旧快照、失败及在制品核对；不是实际模型试玩证据。
