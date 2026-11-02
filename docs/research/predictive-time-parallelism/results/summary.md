# 预测式时间并行研究：综合结论与验收

2026-11-03。本阶段研究已完成原定 C1–C8：留下可运行原型、独立有限模型、真实应用离线对照、八次真实请求及独立新目录重跑。**建议保留研究原型，暂不默认启用并行。** 有限范围与传播延迟确实能创造窗口，但后端容量、输入构造与废弃成本共同决定收益。完整证据、未覆盖机制及采用门槛如下；本页不替代[工作计划](../work-plan.md)维护后续状态。

## 五个研究问题

| 问题 | 目前证据支持的结论 | 适用边界与反例 |
| --- | --- | --- |
| RQ1 规则约束 | 完整的有限范围、跨边延迟下界能增加可用窗口；地理位置本身不能代替语义依赖。 | 模型时间实验同 tick 一 tick 延迟链产生 20 个提前证书，零权闭包为 0；即时传递、共享 hub 则退化为串行。不能为速度改变默认游戏规则后仍声称同规则优化。 |
| RQ2 向外影响 | 语义依赖闭包可作保守基线；非负最短传播下界可在 H 内缩小包络。 | 独立有限事件 oracle 检查 729 图、21,870 查询、196,830 条保守条件，没有发现漏算；保守性仍依赖机制图覆盖全部允许效果与未来关系。样本不证明任意游戏。 |
| RQ3 向内失效 | 反向依赖及严格到达边界可提供必要的失效窗口；已排队机械效果必须投影，目标同 tick 到达不能判安全。 | 真实开窗报价维护、同目标时间且同 Context 身份下的新进入者/错误天气反事实均使旧请求失效。宿主使用实际 seed/stream/sequence 可确定投影天气；相同当前可见输入不足以替代该投影。 |
| RQ4 调度策略 | 有界首响应预取、采用前完整请求验证、既有次序提交，能隐藏独立服务等待；后端拥塞与废弃成本可吞掉收益。 | 非协作假服务独立场景 serial/spec 中位比约 3.01，单槽约 0.99；共享资源单槽约 0.60。2/8/32 合成规模检查通过，但不证明产品规模；实际短 fixture 五对端到端比值约 0.767–1.143，未显示稳定收益。在线缓存/输出不同也不能给稳定加速承诺。 |
| RQ5 工程价值 | Completion 请求 gate 是可行实验接缝：复用真实控制器与 Kernel，预计控制器不接收响应，避免提前执行反馈/操作。 | 真实应用有离线同轨迹、实际边界、冷开与同步阶段开销证据；19 项实际研究测试和六项 runner 组合通过。产品未默认并行，没有第二个持久化权威。一般机制覆盖和稳定端到端收益尚不足以支持默认生产采用。 |

依据分别见[源码基线](source-baseline.md)、[影响模型](influence-model.md)、[模型时间闭环](model-time-windows.md)、[不可协作取消与恢复](cancellation-and-recovery.md)、[实际应用及在线](playground-online.md)。最新 `r4-lifecycle/cli-final.trx` 的 582 项全部通过，其中研究测试 19 项：六项原有、六项 Human、六项 Temporal、一项 Shout，零跳过。此前全 solution 1,502 项通过；追加最后一个离开作用域测试后再跑完整 CLI，未把旧全量结果冒充新增测试结果。[首轮](initial-probe.md)和[平衡测量](balanced-and-playground.md)保留已修正实验的历史边界：旧 delayed-message 按提交边界计时、旧 safe 为白名单，不能作为最终模型时间结论。下文工件目录均相对于 `artifacts/predictive-time-parallelism/`。

## 保守机制与表达代价

| 机制/规则选择 | 可得到的性质 | 必须承担的声明、限制与体验代价 |
| --- | --- | --- |
| 有限语义作用域 | 无路径时可排除影响。 | 地点外还须枚举共享库存、报价、远程关系、观察及唤醒键；缺声明返回 unknown，不能只按距离切区。 |
| 局部即时交互 | 保留当前即时体验。 | 零时延连锁要求完整闭包；一跳接收后再自主发言仍可能扩散，安全窗口可能消失。 |
| 正的最短传播延迟 | 到达前可能提供安全窗口。 | 所有允许行动必须遵守该下界；会造成角色可见交互等待。错峰目标恰在到达 tick 仍须拒绝。 |
| 共享语义 hub | 正确表示远处竞争和信息耦合。 | 扩大闭包、增加失效；若拆分共享资源以求收益，属于规则变体，需独立同规则对照。 |
| 确定的机械投影 | 构造未来时刻完整预计材料。 | 必须纯计算、不推进权威随机游标或发布事实；投影、渲染、验证及日志都有实际开销。 |
| 动态拓扑/未知机制 | 维持保守性。 | 预计窗口内已知变化直接 unknown，或提供覆盖所有未来边的合同；重算代次需完整，unknown 退回实际输入验证/串行。 |
| 验证型投机 | 无安全证书时仍可尝试隐藏等待。 | 需共享准入/预算、实际输入比较、后台任务观察及排空；取消不能证明远端未计算，也不能返还已调用额度。 |

这些是定性代价，没有伪造统一评分。候选来源 S、预计目标与实际采用边界必须分开；同 tick 顺序、真实领域校验与唯一 Kernel 发布权威不因预取改变。

## M1–M9 证据分层

“有限模型”检验显式状态机；“实际应用离线”调用真实领域/控制器/持久化接缝；“在线”仅指本研究实际发出的请求。下表不把源码存在或测试准备中视为运行通过。

| 场景 | 现有证据 | 支持边界 |
| --- | --- | --- |
| M1 独立、同时与错峰 | 模型时间同时/错峰完整轨迹对照；真实应用离线两角色请求重叠且提交有序；在线首次操作亦重叠。真实 Temporal 远处错峰/新到达探针包含在最终 19 项研究测试中。 | 只验证列明的有限初态和实际接缝，不声称自由生活循环都可安全提前。 |
| M2 共享资源 | 合成共享服务/资源废弃成本；`r4-lifecycle/synthetic-checks.json` 的 `ScarceInventoryConserved=true`；真实报价维护使旧请求失效。 | 有限库存检查属于 synthetic harness，不是真实 Playground 库存竞争，也不以报价失效替代库存守恒证据。 |
| M3 即时连锁 | 零延迟闭包、同 tick 队列；实际 say 后失效；最终测试包含真实一跳 shout、接收者同 tick relay 导致旧候选失效。 | 检验当前一跳能力和显式再次发言，不推断任意多跳玩法。 |
| M4 移动与关系变化 | 动态拓扑窗口强制 unknown；真实 departure/traversal/arrival、同时间同 Context 的进入和离开反例均通过。 | 当前应用只接受 Spatial arrival，途中 contact 尚未接入；机械接触回归与应用候选安全分开。缺少该机制声明时不发安全证书，不新增玩法凑覆盖。详见[应用报告](scaling-and-application.md)。 |
| M5 wake 与身份生命周期 | [生命周期](wake-lifecycle.md)：72 运行、36 对轨迹比较及两项 unknown 运行；提前/延后/合并/取消/死亡/重唤醒/分支冷开/绑定代次均由队列驱动。 | 有限模型不声称真实产品死亡/热绑定切换；branch 只是模型队列重建。真实 pending 冷开证据另见恢复报告。 |
| M6 远程、共享市场、开窗 | 真实报价终态清理反例：另一角色开窗时，目标唤醒身份不变但完整请求变化；scratch 不改来源，实际候选须废弃。 | 不能由该反例推断整个市场已具完整影响图。 |
| M7 身体/昼夜/天气 | 真实 Temporal 测试通过，包含身体/日落纯 scratch 投影、来源随机游标不变，以及同目标时间/同 Context 的 calm 错误反事实与实际 cold 输入失配。 | 宿主基于实际 seed、stream、sequence 能确定投影当前 seeded weather；测试不证明天气不可预测。当前角色材料未含随机地址，相同当前可见输入不能证明未来材料相同。 |
| M8 工具、取消、故障、恢复 | 真实 feedback gate、取消无世界变化、E 已发布/S 未发布故障后的 pending 恢复；非协作服务排空、过期失败隔离、账本/证据 writer 故障测试。`r4-lifecycle/controller-final/summary.json` 的六种真实 runner/pool 离线 fixture 检查均 Passed。 | 覆盖 invalid-text、invalid-parameters、feedback、provider-and-dispose、dispose-only、stale-provider；网络调用为零，不推断真实端点故障政策。Lifecycle 的无效局部 CheckStopError 微例已删除，不计清理证据。 |
| M9 unknown 与 Human | 图和生命周期 unknown 不发证；六项真实 Human Console 测试通过，覆盖受控输入等待、输入后失效、EOF/取消排空。 | Human 等待期间候选可完成，权威世界不能越过其决定边界；受控 TextReader 是接口测试，不是真人体验。 |

## 正确性、收益与成本口径

离线 fixture 按完整有效输入寻址；比较所有实际采用输入、命令、事实与必要状态，不按全局第 N 次请求发脚本。模型时间实验 240 次测量、180 次同规则比较，通过后独立重放 1,440 次决定；生命周期也比较完整队列输出而不只修改请求字段。

真实应用离线一预热加五配对使用实际请求、全部有序事实、cursor、最终材料和冷开；在线两个策略的完整输入哈希相同且都成功，但生成正文和原生身份不同。因此在线只支持接线、采用检查和请求重叠，不能提供同轨迹等价证明。

本研究已调用八次 provider，八次 Completed、零未决；详见既存 `online-budget.jsonl` 与[在线报告](playground-online.md)。已知输出合计 1,087 token，输入分解未知，不能计算精确总 token 或费用。两固定请求 serial/candidate 为 4.272s/2.074s，应用短工作负载为 8.429s/5.018s；仅一批、缓存条件不同，不作为稳定速度结论。无需为了复述这项成功重复在线调用。

`r5-scale/scale.json` 已完成 2/8/32 actor 的 180 个正式运行、36 次同条件预热和 120 次同规则轨迹比较，所有工作完成并排空。固定每人两轮、候选上限 min(4, actors)、非协作取消、5ms 假服务，覆盖 independent/shared-resource × independent/shared 服务；这是规模切片，仍不是一般安全图算法或真实应用人数扩展。真实 CLI 仍只有 1–3 角色。

`r5-scale/phase-pairs/comparison.json` 的五个真实应用离线配对通过完整语义轨迹对照，候选峰值在途 2，serial 为 1；每策略两次调用。同步阶段实测覆盖预计 Context、实际人物材料、请求投影、指纹、输入验证、事实投影、E/S 发布和证据写入。配对 serial/candidate 工作耗时比中位约 1.088，范围 0.767–1.143，短 fixture 与存储/进程准备波动下没有稳定端到端收益。预计侧增加材料与请求构造，输入比较自身很小；不能将重叠服务时间直接当成整段省时，也不能将同步阶段和异步等待简单相加冒充总耗时。详细规模与分配表见[规模与应用报告](scaling-and-application.md)。

## C1–C8 验收索引

此表对应[研究入口原始完成条件](../README.md#完成条件)，不修改或缩减条件。

| 条件 | 可审阅证据入口 | 当前裁决 |
| --- | --- | --- |
| C1 基线与问题模型 | [source-baseline](source-baseline.md)、[playground-online](playground-online.md)：真实请求、开窗副作用、身份与边界、在线轨迹。 | 完成；当前实验来源和程序集核对见[最终复核](final-review.md)。 |
| C2 可运行原型 | [合成入口](../../../../experiments/PredictiveTimeParallelism/README.md)、[实际应用入口](../../../../experiments/PlaygroundParallelism/README.md)；模型时间、实际 gate、顺序提交与取消报告。 | 完成；实验已运行，默认产品未开启并行。 |
| C3 影响与安全窗口 | [influence-model](influence-model.md)、[model-time-windows](model-time-windows.md)：独立 oracle、两估计、反向下界及 unknown。 | 完成；保守性主张限于声明机制和有限模型。 |
| C4 规则约束实验 | [model-time-windows](model-time-windows.md)：六规则各自 serial/coarse/timed/spec 对照。 | 完成；规则变化与同规则调度收益分开。 |
| C5 正确性与成本 | 上述 M 表、19 项研究测试、六 runner 组合、[生命周期](wake-lifecycle.md)、[成本与应用](scaling-and-application.md)、取消恢复及在线证据。 | 完成；必需场景按层映射，负结果及未知费用保留。 |
| C6 实际应用验证 | [balanced-and-playground](balanced-and-playground.md)、[cancellation-and-recovery](cancellation-and-recovery.md)、[playground-online](playground-online.md)。 | 完成；有实际应用及小批真实模型对照，不声称在线轨迹等价。 |
| C7 研究与代码交接 | 本报告五 RQ 结论、各专项报告、两个实验入口 README、下列复现与采用门槛。 | 完成；源码、工件来源和哈希见[最终复核](final-review.md)。 |
| C8 独立复核 | [最终复核](final-review.md)：新目录七个命令、完整轨迹及原件检查、Schema Verify、全 solution 与最终 CLI 验收。 | 完成；主线程复核实际代码、TRX、配对原件及验收索引；未扩大默认行为或研究范围。 |

## 复现与采用建议

从仓库根目录使用 global.json SDK；同一可写 dotnet 图串行运行，所有输出使用新目录，保留历史失败结果。以下是现有离线入口，不会隐式调用真实 provider：

```powershell
dotnet build experiments/PredictiveTimeParallelism -c Release -m:1
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --self-check --output artifacts/predictive-time-parallelism/new-review/checks.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --influence-check --output artifacts/predictive-time-parallelism/new-review/influence.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --model-time-check --output artifacts/predictive-time-parallelism/new-review/model-time.json
python experiments/PredictiveTimeParallelism/Verify-ModelTime.py artifacts/predictive-time-parallelism/new-review/model-time.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --lifecycle-check --output artifacts/predictive-time-parallelism/new-review/lifecycle.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --scale-check --output artifacts/predictive-time-parallelism/new-review/scale.json
dotnet test tests/Playground.Cli.Tests -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify --filter FullyQualifiedName~PredictiveTimeParallelismTests
```

真实应用离线/在线、同步阶段配对及 runner 故障检查的参数、配置与预算 grant 见 [PlaygroundParallelism README](../../../../experiments/PlaygroundParallelism/README.md)。在线运行须先核对已有总账并为明确的新假设预留新 batch，不能复用已调用预留。最终工程检查与独立新目录代表重跑的实际命令、工件哈希和分层结果见[最终复核](final-review.md)。本地忽略工件需要随实验环境保存；新检出可按入口重新生成离线证据，不能从 Git 中获得未提交的在线原始材料。

目前建议保留实验入口和测试，**暂不将并行作为默认产品行为**。后续最小采用路径是单次首响应 gate、每角色最多一个候选、全局物理在途与调用预算受限、实际完整输入匹配后才交回普通控制器；仍保持唯一 Kernel 提交。优先选确有独立等待且预计输入成本低的场景。共享服务饱和、即时全局闭包或预测失效率高时应退回串行，而非放松输入比较。

不建议据此建设多 Kernel 并行提交、区域独立时钟、世界回滚或候选持久恢复。若要生产化，新的证据门槛是：真实 workload 的机制覆盖审查、稳定配对收益及废弃成本、可观测的预计/验证开销、真实端点容量/取消政策，以及完整停止故障路径。未来新规则、新输入或新传播能力都应重新打开相应证明义务；一次有限矩阵通过不构成永久安全保证。
