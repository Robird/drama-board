# 缩放切片、实际应用分项与同目标时刻验证

2026-11-03。独立复核者阅读代码、实际 JSON/JSONL 和 TRX，并用 Python 重算配对轨迹、调用结算及统计；本工作包没有运行 dotnet 或在线模型。下列是本次原始工件支持的结论，不是通用并行化保证。

## 证据与口径

| 工件 | 本次复核范围 | SHA256 |
| --- | --- | --- |
| `artifacts/predictive-time-parallelism/r5-scale/scale.json` | 180 个实测 run、36 个预热 run、60 个配对条件、120 次非串行对照；逐项比较完整 Commits，核对工作完成、在途容量和物理调用结算。 | `9969F49AA943A0007C450B907F72D1A640DB14DAC1695688B8E638E9E6590798` |
| `artifacts/predictive-time-parallelism/r5-scale/phase-pairs/comparison.json` | 五对实测及其各自 summary、semantic-trace、final-material、timeline；汇总与各文件一致，五对完整语义轨迹和最终材料字节相同。 | `6840C87E29F866F1FCC023B005EE4FF8766865E953DB304EC34932ED096C34F0` |
| `artifacts/predictive-time-parallelism/r4-lifecycle/cli-final.trx` | 完整 CLI 582 条实际执行、582 通过、0 失败/未执行；其中研究用例 19 条全部通过：Temporal 6、Human 6、Shout 1、其他既有探针 6。 | `367E26769D030FB30A0F08792956D058A7A57187CD841984E1939EE95170F8BC` |

加速比统一为同一配对条件的 `serial / candidate`，表中给出五个配对比值的中位数，不使用“两个耗时中位数之比”替代。预热全部保留但不进入实测统计；每条件轮换策略顺序。`scale.json` 的样本 seed 为 17–21。费用只讨论替身调用数和本地服务占用，不能换算成真实 provider 的 token 或账单。

复现入口是 [ScaleExperiments](../../../../experiments/PredictiveTimeParallelism/ScaleExperiments.cs) 的 `--scale-check` 和 [Compare-Fixture.ps1](../../../../experiments/PlaygroundParallelism/Compare-Fixture.ps1)。前者的预声明配置另存 `r5-scale/plan.json`；后者的每批顺序、重复数和 250ms 延迟存于 `phase-pairs/comparison-manifest.json`。重新执行须使用新目录，并遵守仓库统一的 dotnet 串行构建约定。

## 合成规模实验：瓶颈由依赖与容量共同决定

配置固定为 2/8/32 个合成角色、两轮、每项基础延迟 5ms 加输入寻址扰动、非协作取消；候选窗口和独立后端物理容量为 `min(4, actors)`。两个场景分别是完全独立、每次操作改变所有后续角色可见共享值；`shared-resource` 在这里是共享观察依赖，不冒充有限库存争抢。两个后端分别允许独立重叠、只保留一个服务站。

每个规模 × 场景 × 后端都各做三种策略和五个重复，共 180 个实测。所有 run 都完成了恰好 `2 × actors` 次提交且本地物理服务排空，调用满足 `Invoked = Completed + Canceled + PhysicalFaulted`、服务记录数等于 Invoked；非串行策略的完整提交数组与串行参考一致。

| 角色数 | 场景 | 后端 | safe 中位加速比 | speculative 中位加速比 | speculative / serial 实际调用数 |
| --- | --- | --- | ---: | ---: | ---: |
| 2 | 独立 | 独立容量 | 1.992 | 2.006 | 4 / 4 |
| 8 | 独立 | 独立容量 | 3.999 | 3.990 | 16 / 16 |
| 32 | 独立 | 独立容量 | 3.936 | 4.023 | 64 / 64 |
| 2 | 独立 | 单服务站 | 1.002 | 1.003 | 4 / 4 |
| 8 | 独立 | 单服务站 | 0.996 | 1.006 | 16 / 16 |
| 32 | 独立 | 单服务站 | 1.000 | 0.997 | 64 / 64 |
| 2 | 共享依赖 | 独立容量 | 0.995 | 0.996 | 6 / 4 |
| 8 | 共享依赖 | 独立容量 | 1.002 | 0.998 | 30 / 16 |
| 32 | 共享依赖 | 独立容量 | 1.008 | 0.998 | 126 / 64 |
| 2 | 共享依赖 | 单服务站 | 0.984 | 0.640 | 6 / 4 |
| 8 | 共享依赖 | 单服务站 | 0.995 | 0.535 | 30 / 16 |
| 32 | 共享依赖 | 单服务站 | 1.002 | 0.508 | 126 / 64 |

这些数字支持以下有限结论：

- 独立角色可以利用约四个并行槽位；32 角色没有得到 32 倍收益。这是固定窗口/容量的饱和现象，没有测得增加窗口后的最优值。
- 后端单站串行时，即使所有提前请求都有效，加速仍约为 1。提高宿主并发不能创造后端容量。
- 共享依赖下，safe 白名单退回需求调用，保持调用数；固定投机没有采用任何真正提前的候选，却将实际调用数放大至 1.5、1.875、1.96875 倍。独立容量下耗时仍约为原值，说明浪费可能被墙钟重叠掩盖。
- 同样的废弃在单站后端直接争用需求请求，最大规模加速比约 0.508，即耗时接近两倍。使用含排空时间的口径，中位比值仍约 0.508；不是把未结算后台工作藏到了计时之外。

真正提前的候选由 `Source < Target` 定义。五次重复合计，独立场景每种候选策略在每个后端的提前采用/发起数分别为 10/10、70/70、310/310；共享依赖 speculative 为 0/10、0/70、0/310，safe 不发提前候选。即时需求候选不计作预测命中。

这是毫秒级 Task.Delay、Windows timer 和少量配对样本下的合成切片。`safe` 仍是规则白名单，不是把通用影响图自动用于当前游戏；本表没有测量影响估计器的规模复杂度，也没有验证真实 Playground 的 8/32 角色。服务占用比只是本地替身的资源代理，不能据此声称远端取消计费或实际 token 成本已测得。

## 实际 Playground：调用重叠成立，端到端收益有明显波动

该批使用真实材料、控制器、parser、Kernel、磁盘 E/S 和冷开核验，保留原玩法规则；两角色关闭 needs/weather，工作负载为各完成一次 `handoff fixture-<actor>`。Completion 是输入寻址的本地替身，每次设置 250ms 延迟；不是在线 LLM。

五对实测共十个 run、二十个 provider 替身调用；另有一对预热，不计入下面统计。每个 run 均完成 a、b 两个操作；serial 峰值 1、candidate 峰值 2，candidate 共十项采用、零项废弃。独立比较五对 `semantic-trace.json` 和 `final-material.txt` 的完整字节，全部相同，符合这条固定输入响应对照的轨迹要求。

| 配对 | serial 工作完成秒 | candidate 工作完成秒 | 工作完成加速比 |
| --- | ---: | ---: | ---: |
| 0 | 4.198 | 5.082 | 0.826 |
| 1 | 3.899 | 3.583 | 1.088 |
| 2 | 4.008 | 5.227 | 0.767 |
| 3 | 3.920 | 3.430 | 1.143 |
| 4 | 3.894 | 3.533 | 1.102 |

五个配对工作完成比值中位数 **1.088**，范围 **0.767–1.143**，其中 **2/5 变慢**。包含排空和释放后的配对中位数为 1.085；再含本次冷开核验、语义工件写入等到 summary 前的配对中位数为 1.074。五个样本不足以声称可靠的 p95、普遍加速或稳定的约 9% 改善。

实际 timeline 中 serial 两请求没有交叠，candidate 两请求在五次运行中的交叠为 260.674–276.640ms，中位数 265.797ms。这说明底层调用确实重叠，但不是端到端总时间的完整解释。

分项计量如下；为各策略五个样本各自的中位数，不把这些数相加当作整个工作耗时的完整分解。分配字节仅为被 Measure 包围的同步调用在线程上的计量，不包括完整异步 provider 生命周期。

| 分项 | serial 次数 / 中位 ms | candidate 次数 / 中位 ms | 分配或范围说明 |
| --- | ---: | ---: | --- |
| 人物材料渲染 | 2 / 58.985 | 4 / 62.200 | 中位分配 314,608 / 594,328 bytes。 |
| 预测 Context | 无 | 2 / 120.908 | candidate 中位分配 364,272 bytes。 |
| 请求投影 | 4 / 40.847 | 6 / 56.712 | 中位分配 565,024 / 688,584 bytes。 |
| 指纹 | 4 / 0.227 | 8 / 0.368 | 中位分配 71,872 / 143,744 bytes。 |
| 采用前输入比较 | 无 | 2 / 0.185 | 此包围区间中位分配为 0，不代表构造整个输入零分配。 |
| 领域事实诊断投影 | 4 / 252.187 | 4 / 221.470 | 不是世界规则自身的 Plan/Fold 耗时。 |
| E 发布 | 4 / 381.897 | 4 / 511.300 | 发布次数相同，包含实际持久化工作。 |
| S 发布 | 4 / 278.024 | 4 / 311.172 | 发布次数相同，包含实际持久化工作。 |
| 实验证据写入 | 21 / 107.022 | 28 / 176.041 | candidate 增加预测/采用诊断；不等同产品日志的未来固定成本。 |

`WorkCompletedMilliseconds` 从探针的 evidence 时钟开始，包含新存档创建、清单与源码/程序集身份记录、当前进程内冷启动路径以及真实持久化。首条 source-state 已在约 2.046–2.957 秒；它不是纯 LLM 等待指标，也不包含进程进入该时钟之前的全部启动时间。两个慢样本的 E/S 分项时间明显更长，但发布次数仍相同；没有隔离 I/O、JIT、GC 等因素，不能进一步断言根因。

因此本批支持“实际提交接缝可采用重叠请求且保持固定轨迹”，也提示投影、证据和持久化成本会抵消一部分收益；不能把合成近四倍直接宣传为实际游戏加速。后续如要判断常驻宿主价值，应另做同一进程内更长工作负载及不同 provider 延迟的预声明实验。本阶段没有因此扩展任务或默认启用并行。

## 实际材料与生命周期补充验收

以下测试入口均在实际读取的 `cli-final.trx` 中执行通过。它们提供当前接缝的具体反例，不能替代通用传播合同。

| 入口 | 本次能够支持的主张 | 边界 |
| --- | --- | --- |
| [Temporal.cs](../../../../tests/Playground.Cli.Tests/PredictiveTimeParallelismTests.Temporal.cs) `Departure_from_the_visible_place_discards_same_time_same_context_candidate` | a、b 原来同在 camp；a 的真实 go 进入 TraversingLocation 后从 b 的可见角色集合消失，b 同时间、同 Context 的候选废弃，只提交 fresh 操作；总计四次真实转换。 | 隔离了离开后的材料变化，不涉及途中 contact；完整请求校验不能退化成仅比较时间或 Context。 |
| 同文件 `Remote_staggered_service_discards_input_after_a_new_actor_arrives_at_the_exact_tick` | a 经真实 depart/traverse/arrive 在 14 点进入 cabin；14 点前边界不发生抵达，抵达后 b 可见 a；旧请求被丢弃。 | 旧请求 10 点、实际请求 15 点，单独不能隔离“入场而非时间差”导致失效。 |
| 同文件 `Same_target_time_and_context_still_discard_a_counterfactual_that_misses_the_new_entrant` | scratch 假设 a 到 ridge 后 wait，预计 b 在 15 点醒来仍无人；真实 a 改选 go，在 b 同一个 15 点、ContextSequence 和 LastOperationSequence 下出现在场，完整请求不相等并拒绝旧候选。 | 未知未来选择被明确作为投机假设；不是声称已有安全证书允许忽略 a。source world/cursor/head/内容不受 scratch 推进影响。 |
| 同文件 `Known_body_and_sunset_evolution_can_be_projected_by_scratch_kernel_without_drawing_future_weather` | 17→18 点身体、日落和完整材料在独立 scratch 预测后，真实输入采用成功；源 head/cursor、对象和环境 sequence 保持，20 点天气未被执行。 | 证明这条无外部选择的确定演化路径，不证明任意未来都能如此投影。 |
| 同文件 `Same_visible_current_input_hides_weather_seed_and_actual_cold_or_calm_invalidates_old_candidates` | seed 0/1 的当前可见请求相同，而 18 点天气及暖度不同。 | 原旧候选还混有 17→18 点时间差；只能反驳“当前可见材料足以确定未来”。 |
| 同文件 `Same_target_time_and_context_still_discard_calm_counterfactual_when_actual_seed_produces_cold` | 反事实 calm 与真实 cold 均在 18 点、相同 Context/操作序号，food/water/day 一致，天气/暖度不同使完整请求失配；真实源 seed/sequence 未被替代世界污染。 | 消除了时间差混淆。当前天气由真实 seed/stream/sequence 确定，宿主原则上可纯投影；不据此声称天气本质不可预测。 |
| [Human.cs](../../../../tests/Playground.Cli.Tests/PredictiveTimeParallelismTests.Human.cs) `Human_console_wait_holds_authoritative_frontier_and_released_input_invalidates_completed_candidate` 的 say/offer 两例 | 真正 ConsoleCharacterController 在 gated ReadLineAsync 等待，b 的候选先完成也不推进 world/cursor/head、不生成 pending/输入；a 释放输入后按原顺序提交，b 重新取得包含话语或未成交报价的实际材料。 | 是受控 TextReader 接口测试，既不是真人自由试玩，也不承诺消除 Human 对全局 frontier 的阻塞。 |
| 同文件 `Human_console_eof_or_cancellation_stops_without_adopting_or_inventing_input_and_drains_candidate` 的四个组合 | EOF/取消 × 候选已完成/未完成均不伪造 Human 操作、不采用 b、不推进真实边界，最后显式清理候选。 | 本地清理不是远端未计算或未计费的证明。 |
| [Shout.cs](../../../../tests/Playground.Cli.Tests/PredictiveTimeParallelismTests.Shout.cs) `One_hop_remote_shout_invalidates_candidate_before_the_receiver_relays_at_the_same_tick` | camp 与 stream 的角色不互相可见；10 点一跳 shout 使 b 旧候选失效，fresh 输入收到原话后才显式喊回；与串行完整输入/转换/最终材料相同，各消耗一次发送额度。 | 两节点显式回传，不是自动转发或三地点连锁已被验证。 |

应用当前只在 [PlaygroundPlanning.Arrive](../../../../src/Playground/PlaygroundPlanning.cs) 接受单个 TraversalArrivedFact；其他 Spatial 事实会拒绝。途中 contact 的机械能力另由 [SpatialContactKernelAcceptanceTests](../../../../tests/Spatial.Tests/Acceptance/SpatialContactKernelAcceptanceTests.cs) 验证，不能冒充 Playground 已接入 contact。对于未接入或未声明覆盖的机制，本研究不发安全证书，也不新建玩法来填表。

此外 [Harness 自检查](../../../../experiments/PredictiveTimeParallelism/Harness.cs) 新增 `scarce-resource`：两个合成角色争抢库存 1，串行与候选都得到 1、0；恰有一个旧候选废弃，candidate 实际调用三次、serial 两次，每个提交均非负且 `Resource + sum(Values) == 1`。本次读取 `r4-lifecycle/synthetic-checks.json` 的 `ScarceInventoryConserved=true`，并审查其实际断言；它是有限库存守恒证据，不是实际游戏市场压力或真实费用测试。

本报告没有将这些代表性路径扩大为所有动态地图、所有随机规则或全部并行服务的正确性证明，也不独自裁决 C1–C8 总体完成；总体证据索引和未覆盖机制应由综合报告统一维护。
