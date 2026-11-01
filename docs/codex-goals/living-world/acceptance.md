# 山野镇阶段验收与交接

2026-11-01。[原定 C1–C8](README.md#完成条件)全部具备实现、工程和实际运行证据。产品为 **山野镇 / Hillside**，唯一推荐入口 `src/ResourceCycle.Cli`；没有强制主线或固定天数的世界终局。Microtown 停止独立产品演进，保留实现、测试及历史证据。

生产规则以 `ea87f55` 为最终源码基线，GameRevision 22；本文件所属交付提交只补充入口说明、验收脚本和实际证据，未改变这些规则。操作平台为 Windows、.NET SDK 10.0.201；未宣称完成 Linux 或整仓 solution 回归。

## C1–C8 对照

| 条件 | 结果与权威证据 |
| --- | --- |
| C1 连续世界 | 同一 Cycle 根承载角色、身体、空间、背包、现金、目标、探索及材料，镇山往返不重置、原出口不退休；[首片](../../experiments/living-world-foundation-acceptance.md)与修订 22 [复验](../../experiments/living-world-content-acceptance.md)。根 README 和 Microtown 文档明确停止旧原型独立演进；保留内部 Cycle 命名不形成第二个产品。 |
| C2 可重复生活 | 普通 Console 两进程 39 条经济命令完成两次出行返回，7 条交易命令验证成交/拒绝/撤回，修订 22 全部复验；[经济](../../experiments/living-world-economy-acceptance.md)、[交易](../../experiments/living-world-trades-acceptance.md)、[内容复验](../../experiments/living-world-content-acceptance.md)。默认需求/天气开启；[三日自主运行](../../experiments/living-world-llm-playtest.md)另有生产、消费、市场与守恒证据。 |
| C3 角色与控制 | 默认三人；共享 Human、全 LLM、任意一个 Human＋其他 LLM，启动/冷开可改绑定，角色身份与状态不变。宿主 fixture、实际 Gym 冷开重绑定及[真实混合会话](../../experiments/living-world-freeplay-mixed.md)共同证明；使用说明见根 README。 |
| C4 可解释互动 | 定向说话、赠与、报价、跟随、大喊及忙碌响应使用同一事实/冻结材料链。当前完整回归包含 Following、Shout、交易和隐私用例；[交易验收](../../experiments/living-world-trades-acceptance.md)覆盖活动保持，真实混合会话自然发生跟随、脱队、采集、大喊与重逢。角色话语保留署名，不提升为世界真相；没有以模型自发成交作为必需门槛。 |
| C5 恢复与 Gym | 正常冷开、E-only 不重 Plan/控制器、同刻竞争/拒绝与 Schema Verify 由四片工程证据支持；[Gym 实际验收](../../experiments/living-world-gym-acceptance.md)用真实请求锚点注册完成 S 分叉，两个进程冷开行动，源 head/操作/导出保持不变，角色范围与祖先截断均核对。 |
| C6 可调整世界 | [不可变有效定义](content.md)统一地图、初始角色、资源、配方、经济、身体/环境参数；普通 C# `Change` / `Named` 可建变体，无需改 Kernel。保存定义与指纹绑定 S/E，拒绝不改源档；gentle 实际 Console 四进程、实例隔离、正常/pending/分叉及参数效果见[内容验收](../../experiments/living-world-content-acceptance.md)。 |
| C7 实际运行与游玩 | [同一三模型历史](../../experiments/living-world-llm-playtest.md)两段串行接续 72h，264 次真实 Completed，三人存活且资产守恒；两次独立自由会话为[单人 Console](../../experiments/living-world-freeplay-console.md)与[Human＋两真实模型](../../experiments/living-world-freeplay-mixed.md)。操作者为 Coding Agent，未包装成真人或预编脚本。 |
| C8 交接与收敛 | 根 README 提供启动/选择角色/暂停/续局/内容/Gym 用法；本页给出证据索引与限制，工作计划已收敛。995 项最终规则回归通过，另运行 74 项 Gym/分支相关检查（部分与全套重叠），当前文档链接及脚本语法通过；没有待修的已知严重阻断或等待用户本人试玩才能关闭的依赖。 |

四片历史工程文档分别记录当时修订和未完成项，不能将那些历史边界当作当前缺口；最终修订 22 的完整结果以[内容验收](../../experiments/living-world-content-acceptance.md)为准。本轮主线程及独立复核读取了实际 TRX counters、脚本产物、模型日志和终态，不仅引用子代理结论。

## 开始玩与继续

在 drama-board 根目录执行：

```powershell
# 三人新局：自己控制 a，两个模型居民共用本次 64 次调用预算。
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-hillside --llm --human a --gym --model gpt-6.1-sol --max-calls 64

# 同一命令与同一存档即为续局；不带 --llm 则由 Console 轮流控制全部角色。
dotnet run --project src/ResourceCycle.Cli -c Release -- --store artifacts/my-hillside
```

内置连接使用本机已有认证；其他连接选择、全模型运行及内容变体见[根 README](../../../README.md)。新局路径须由自己选择；不要把验收工件目录当成新局输出或覆盖其中记录。`:quit` / EOF / Ctrl+C 退出宿主，已提交进度保留。预算不属于世界终局；恢复时重新指定控制器、模型与预算。`--until-hour` 是绝对小时，不能早于当前存档时间。注册过分叉的存档需明确 `--branch`。

验收后的三日世界位于 `artifacts/living-world-llm/world`，分支 `f82aef6e`，第 3 天 08:00、完成 S `f82aef6e0e0000a062010000`。默认内容指纹 `E763926A859EDDEC4F1219F5701186F03656D17693E99435193D752C50C18E22`。三人资金合计 93＝初始 36−购买 62＋出售 119，仍各有身体与补给，可继续运行；到预算边界时尚未处理的同刻候选会在接续时继续仲裁。

## 验证版本与边界

- 修订 22 最终领域 548、CLI 402、Completion 离线 45 项全部通过，无失败或跳过；CLI Release/Schema Verify 构建通过。Gym 模块 30、宿主相关 27、分叉/内容绑定 17 项另行通过。原始 TRX 和命令位置见对应验收记录，不把重复覆盖加成独立测试数。
- 最终持续运行及两场自由游玩共用 CLI SHA-256 `212557583C703D92636B5DC01B4B2E258EBFA440FB67C7E4DD128C113558C58E`；早期 Gym 六调用探路的哈希单列。依赖源码：Completion `97f755f184a1027323e270d16f91a3f89b134701`、DurableGraph `f7f0c28bacaa67993b7b07a436c3d82bc4ebdda8`、Storage `9ad39fc51a420e8302468533b82d8bc3a91a403e`，交付核查均无改动。
- 工程脚本、离线 fixture、真实模型运行、Agent 自由游玩分别留证。实际模型调用总数为短探路 6＋持续局 264＋混合局 24＝294；子分支管理验收没有额外模型调用。没有自动重试、隐形补给或为了成功修改途中存档。
- 原始 artifacts 留在本地且被 Git 忽略；文档、可复用验收脚本和源码受版本管理。旧开发存档不兼容，仅当前修订/内容身份受支持；保存世界仍使用其原定义，不跟随新代码默认值偷偷变化。
- 模拟仍按角色串行决定，等 Human/模型时世界暂停；没有墙钟持续世界、多人联网或运行中热换控制器。原生模型 ToolSession、完整历史自动记忆和任意位置倒带不在本阶段保证内。
- 72h 是有限样本，不承诺任意模型永远存活、无限经济稳定或社会合作必然出现。日志没有长期容量管理；本次测得的体积和总墙钟不等于引擎性能基准。

## 后续可选方向

没有必须交给用户裁决的遗留项。本阶段在此结束，不自动扩充战斗、剧情、地图或 Gym 二期。两场自由游玩共同指出新回合重复规则和完整 Goals 帮助造成阅读负担；若继续打磨，优先考虑保持角色信息完整前提下的 Human Console 帮助折叠。路标/场景可交互性、长期记忆及社会动机是后续候选，需各自立题。实际人类体验仍值得收集，但不阻塞本阶段，也不抹去旧生存原型独立保留的真人合作验收事项。
