# 世界内容定义与存档绑定

2026-11-01。**尚未实施**；这是 C6 下一片的设计起点，源码现状仍以实际文件为准。完成条件见 [README](README.md)，施工状态只在 [work-plan](work-plan.md) 维护。

## 当前缺口

已有普通 C# 内容入口，但运行规则依赖全局静态内容；仅凭 `ScenarioId` 与 `GameRevision` 不能防止同修订下修改内容后悄悄改变旧档语义。

| 范围 | 当前落点与缺口 |
| --- | --- |
| 地图、描述、庇护、路程、声音边、资源归属 | [CycleExpeditionMap](../../../src/ResourceCycle/CycleExpeditionMap.cs) 是全局静态图，身份固定为 `hillside-v1`。 |
| 初始角色、身体、背包、现金、资源模板 | [LivingWorldContent](../../../src/ResourceCycle/LivingWorldContent.cs) 与 [CycleGameFactory](../../../src/ResourceCycle/CycleGameFactory.cs)；人数、开始时刻和天气种子也可由 factory 参数改变。 |
| 物品、配方、供需、价格、补给效果 | [LivingWorldEconomyContent](../../../src/ResourceCycle/LivingWorldEconomyContent.cs)；经济规划、校验和观察直接读取静态定义。 |
| 市场与工坊绑定、市场刷新 | [CycleEconomyRules](../../../src/ResourceCycle/CycleEconomyRules.cs)、[CycleEconomyValidation](../../../src/ResourceCycle/CycleEconomyValidation.cs)、[CycleEconomyObservation](../../../src/ResourceCycle/CycleEconomyObservation.cs) 写死地点 ID；[CycleEconomyState](../../../src/ResourceCycle/CycleEconomyState.cs) 中 `CycleMarket.Fresh/Validate` 读取静态配额。 |
| 身体与天气 | [CycleSurvivalRules](../../../src/ResourceCycle/CycleSurvivalRules.cs) 固定代谢/温暖速率；[CycleSurvival](../../../src/ResourceCycle/CycleSurvival.cs) 固定范围/警戒线；[CycleEnvironment](../../../src/ResourceCycle/CycleEnvironment.cs) 内 `CycleEnvironmentRules` 固定间隔、损失与概率。 |
| 存档绑定 | [CycleDurableRoot](../../../src/ResourceCycle/Persistence/CycleDurableRoot.cs) 与 [CycleDurableEvent](../../../src/ResourceCycle/Persistence/CycleDurableEvent.cs) 保存游戏修订；尚无内容指纹。 |

## 最小合同

采用一份深度不可变的 `CycleWorldDefinition`（拟议类型），由普通 C# 工厂建立默认内容与具名变体。暂不引入外部配置语言、编辑器、热加载或内容注册平台。

- 定义包含地点/通路/语义连接、资源模板及位置、初始居民、物品/配方/市场、身体与天气参数。市场和工坊由定义中的地点 ID 绑定。
- 集合必须复制并只读，子对象不得暴露可变字段。构建时校验重复 ID、引用、范围、正数周期、库存/配额、配方输入输出及初始位置。运行时不能更换定义。
- 算法、固定计量单位和仲裁语义属于 `GameRevision`，不必把每个实现常量都变成配置。可调参数的支持范围须明确校验。
- 在 `CycleBusinessState` 中持久化定义子对象及其内容身份；每次业务 `Replace` 保留它。初始参数若允许覆盖，须在计算身份前形成完整的有效定义，不能存在未绑定的隐式默认值。
- 规则、Fold 校验、观察均读取当前世界定义；不允许恢复或执行时悄悄回退到编译时默认内容。
- `GraphDefinition`、空间模拟器/规划器/Reducer、索引等由定义构造，是运行缓存；冷开按保存定义重建，**不持久化派生缓存**。同一进程必须能运行两种定义而不串用全局静态图。

## 规范指纹与恢复

有效内容身份为 `(ContentId, Fingerprint)`；指纹使用确定的规范编码与 SHA-256，不使用 `GetHashCode` 或任意对象 `ToString`。

1. 编码固定字段顺序、整数表示、UTF-8 和显式 null。字典及无序集合按 Ordinal ID 排序，声音边规范端点；角色服务顺序等有语义的序列必须保留顺序。
2. 纳入所有定义字段，包括会影响角色材料的名称、描述和反向通路描述。编码格式本身有明确版本。
3. 每个 S 保存定义与身份；独立 E 包装器保存前态内容身份。加载先检查支持的游戏修订与定义结构，再重算 S 指纹、核对 pending E，之后执行业务校验与 Rebuild。
4. 冷开直接使用存档定义。当前默认内容改变只影响新局；不得用新默认值覆盖旧档。若宿主显式要求某定义，身份不匹配则拒绝。
5. `CommitState` 拒绝一次运行中改变内容身份；pending 恢复只消费保存事实，不重新规划或询问控制器。分叉继承原定义，源分支不变。
6. 内容错误或身份不匹配应在执行 pending、调用模型或发布新 E/S 之前拒绝。仍无旧开发修订兼容承诺。

## 实施落点与注意事项

创建由 `CycleGameFactory` 接收/规范化定义，再建立初始业务图；`CycleModels.cs` 中的业务状态持有定义。现有内容静态类可转为默认定义工厂，不应保留另一份运行时权威。

加载/发布落点为 [CycleDurableHistory](../../../src/ResourceCycle/Persistence/CycleDurableHistory.cs)；Schema 和算法修订由 [CycleGameRevision](../../../src/ResourceCycle/Persistence/CycleGameRevision.cs) 管理。[CycleBranchBoundary](../../../src/ResourceCycle/Persistence/CycleBranchBoundary.cs) 检查历史 S/E 及分叉边界时也必须核对内容身份，不能只保护常规 Open。

现有经济/生产对象可在自身检查结构；依赖物品目录、配方和地点的引用校验必须在持有定义的世界边界完成。不得为避开校验而重复保存一份可漂移的默认配额。

`CycleSurvivalRules.ForecastWarmth` 当前假设三个昼夜分段内一定耗尽。支持更温和参数时，须改为可证明终止、能表达永不耗尽的预测，或明确拒绝不支持参数；不能把合法内容变体变成运行时异常。天气参数与市场配额同时用于规划、事实校验、观察校验，必须成套替换。交易施工中的状态接口不由本设计预先重定。

## C6 验收

- 建立一个普通 C# 合法变体，修改一条路程、初始资产、资源周期、配方/价格、身体速率及天气间隔；不修改 Kernel。
- 用普通命令完成移动、采集、生产、买卖与消费，证明变体参数真正生效；冷开继续仍使用保存值。演示至少一种当前支持范围内的身体参数变体。
- 默认与变体同进程交错运行，空间、经济与预测不串用；新建世界修改默认内容不影响已保存世界。
- 无序输入的不同排列产生相同指纹；有效字段改变产生不同指纹；有语义的角色顺序变化不能被排序抹去。
- 重复 ID、坏引用、非法周期/参数在创建前拒绝；S 定义/指纹不一致、E/S 身份不匹配在恢复前拒绝，源档不变。
- 正常冷开、E-only 恢复及完成 S 分叉行动通过；pending 不重问控制器、不重复经济结算，分叉不修改源世界。
- 受影响测试、Schema Verify 通过，记录可复现的变体创建/运行入口与证据。静态内容文件存在或单次构建成功不等于 C6 已完成。
