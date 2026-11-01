# 世界内容定义与存档绑定

2026-11-01。游戏修订 22 实施 C6：不可变内容随世界保存，运行规则与恢复读取保存定义。完成条件见 [README](README.md)，施工状态只在 [work-plan](work-plan.md) 维护，工程与实际 Console 证据见[内容验收](../../experiments/living-world-content-acceptance.md)。

## 内容入口

[CycleWorldDefinitions](../../../src/ResourceCycle/CycleWorldDefinitions.cs) 是普通 C# 创作入口，`LivingWorld(...)` 建立默认内容，`Named("hillside" | "gentle", ...)` 提供 CLI 预设。[CycleWorldDefinition](../../../src/ResourceCycle/CycleWorldDefinition.cs) 保存完整有效定义；`Change(...)` 返回新定义。人数、开始时刻、需求/天气开关和种子在指纹计算前归一化。`CycleGameFactory.Create(definition)` 校验后建立初始图，不改变 Kernel。

定义包括地点名称、描述与庇护，通路耗时及双向描述，独立的 64-bit 语义关系；资源位置、初始库存、容量、产物、再生/采集周期与技能要求；按服务顺序保留的初始居民、位置、技能、现金、身体、背包与容量；物品名称、配方、市场供需/价格/刷新周期及市场/工坊地点；补给效果、五种身体速率和天气参数。

集合复制后只读公开，子条目只含值字段或复制后的只读集合。构造和恢复均检查重复 ID、引用、图结构、正数周期、库存/容量、配方和供需/价格对应关系。地点不能声明出口退休。保存定义恢复时重算指纹，不依赖构造器已经执行。

身体范围 0–100、警戒线 25、定点单位、昼夜分段和事件优先级属于算法修订。当前支持食物/水分速率 -100..0，庇护/白天回暖 0..100，夜间温暖 -100..0；天气额外失温 0..100，概率为合法整数分子/正分母。零消耗或稳定昼夜循环可以永不耗尽，预测返回空期限；缓慢负漂移按昼夜循环预测，不再假设三个分段内耗尽。物品可同时补充饱食和水分。

`FromLegacy(...)` 只把旧领域夹具的初始输入一次性转换为定义，不是任意运行快照迁移器。默认静态内容及旧辅助重载不参与生产世界的执行、Fold 或恢复。不引入配置语言、编辑器、热加载或内容注册平台。

## 运行权威与缓存

`CycleBusinessState.Definition` 持久化定义，每次 `Replace` 保留同一对象，`CycleWorld.Definition` 暴露它。空间、经济、交易、身体、天气、规划、Fold 验证与观察读取当前定义。资源和身体仍保存各自运行锚点，运行中不能更换定义。

Graph、空间模拟器、Planner、Reducer、查询与 OccurrenceRule 是按定义实例隔离的派生缓存，不进入持久图；冷开从保存定义重建。默认与变体可同进程交错执行。`Expedition.ScenarioId` 必须等于定义 `ContentId`，不再与全局默认字符串比较。

冻结观察新增 `WorldName` 与 `ItemUseDescriptions`，Console/Completion 从同一材料读取名称、补给效果与当前速率。指纹仅在宿主管理输出和 Gym run-start 出现，不向角色泄露完整定义、远处地图或他人初始资产。

## 规范指纹

有效内容身份为 `(ContentId, Fingerprint)`，指纹为 SHA-256 十六进制文本。同一 ContentId 的有效字段改变也会改变指纹。

编码 v1 使用 `BinaryWriter` 固定字段次序、小端整数和带长度前缀的严格 UTF-8 字符串，开头为 `Hillside.WorldDefinition/v1`。集合写数量，可空背包容量写存在标记和值，其余必需字段不允许 null。

无序定义和字典按 Ordinal ID 排序，语义关系先规范两个端点再排序；居民顺序有服务语义，保留输入顺序。全部定义字段均参与编码，包括名字、描述、反向提示、容量、随机种子和开关；不使用 `GetHashCode` 或任意 `ToString`，派生缓存不参与编码。

## 发布与恢复

每个 S 的 Business 保存完整定义及身份；独立 `CycleDurableEvent` 保存规划前态的 ContentId/Fingerprint。`CommitEvent` 从 ready S 取得身份，`CommitState` 在写盘前拒绝不同身份，并保持既有故障退出合同。

恢复先检查结构与支持的修订、重算保存定义、核对 pending E 身份，再业务验证和 Rebuild。生产只接受当前修订。人工迁移夹具保留 S1/E2 中断合同：先验证原 S，再将 S 转到 E 修订后比对；每步事件转换后重新比对，错误 E 不能靠转换器重新贴身份蒙混过关。加载不执行 pending、不询问控制器、不发布 E/S。

普通 `Open(path)` 使用保存定义。`Open(path, expectedDefinition)` 及带 branch 的重载在业务校验与 Rebuild 前核对完整期望身份。分叉注册前检查所选 S、前态 S 自身和 S—E—S 三端身份；子分支继承原定义，源分支不变。

CLI 省略 `--content` 时新局用 hillside，续局读取保存定义。显式指定时先只读 Open，再用保存的初始人数、起始时间和需求/天气参数构造命名期望并比较；在 pending 恢复、宿主日志/操作写入与模型连接前拒绝不符。CLI 的比较在只读 Rebuild 后，直接传完整 expectedDefinition 的 API 则在 Rebuild 前；两者都不在拒绝前执行游戏操作或写源档。

## 默认与舒缓预设

| 参数 | hillside | gentle |
| --- | --- | --- |
| 镇口—营地通路 | 1 小时 | 半小时 |
| 浆果再生 | 1 小时 | 半小时 |
| 每人初始现金 | 12 | 20 |
| 口粮制作 / 售价 | 1 小时 / 5 钱 | 半小时 / 6 钱 |
| 每小时饱食 / 水分 / 夜间温暖 | -5 / -8 / -12 | -3 / -5 / -6 |
| 天气窗口 / 暴露损失 / 寒风概率 | 2 小时 / 10 / 1/2 | 4 小时 / 5 / 1/4 |

两预设默认都开启需求和天气，初始资产有限；gentle 是调整内容的演示。

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/gentle-world --content gentle --characters 1
# 续局直接使用创建时保存的定义
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/gentle-world
# 构建后，四进程工程脚本验证生产、续局与不符拒绝
pwsh -NoProfile -File scripts/Smoke-HillsideContent.ps1
```

独立验收还用普通 C# 建立另一个变体，改变路线、初始资产、资源周期、配方产出/耗时、价格、补给、身体和天气，通过普通命令及真实磁盘 E/S 验证效果。具体证据和限制见[验收记录](../../experiments/living-world-content-acceptance.md)；脚本不是自由游玩或真实模型运行。
