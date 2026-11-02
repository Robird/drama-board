# 不可协作取消、故障隔离与真实恢复边界

2026-11-03。此轮在 [初始原型](initial-probe.md) 和 [平衡测量](balanced-and-playground.md) 上补齐两项服务模型缺口，并增加真实 Playground 的隐式材料失效与 pending E 恢复探针。所有默认产品规则和串行提交合同保持原样。

## 服务模型与计数

`PredictiveTimeParallelism` 新增 `--cancel-mode cooperative|noncooperative`。后者只允许取消尚未取得槽位的排队工作；服务一旦开始，即使候选被宿主丢弃，也继续消耗槽位和完整的模拟服务时间。失效候选进入后台观察，需求请求可以继续排队，停止时等待所有物理服务结清再释放资源。独立延迟后端同样受 `candidate-limit` 的物理在途上限约束，防止废弃工作绕过资源界限。

每个 reservation 保留独立服务记录：开始/结束、宿主取消时刻、结果、输入哈希及取消后继续服务的时长。`AbandonedCandidates` 属于本地候选状态，`Completed`/`PhysicalFaulted`/`Canceled` 属于模拟物理请求状态。不可协作服务的 host cancel 不计成物理 canceled；所有已调用请求结算关系为 `Invoked = Completed + Canceled + PhysicalFaulted`，正常或受控失败退出的 `RemainingPhysical` 与 `RemainingCandidates` 均为零。

故障由完整输入特征确定，不依赖全局到达序号。`stale-shared` 只使旧资源值下的某角色候选失败：若实际输入已变化，故障只作为废弃计算诊断，不阻止新需求。`actor-zero` 使真正匹配的实际请求失败：停止当前运行，保留失败快照，不自动重试。预算、取消和主故障都要在排空后报告；不能用清理异常覆盖原错误。

## 已运行证据

工件目录：`artifacts/predictive-time-parallelism/r2-model-time/`。

- `checks.json`：既有 12 个串行对照、输入字段变更、容量与预算检查，以及新增过期故障隔离、匹配故障停止、不可协作服务排空均通过。
- `stale-failure.json`：运行正常结束，一项过期物理失败被隔离；本批服务在取消后仍消耗约 58ms。其数值只是模拟服务测量，非真实费用。
- `matching-failure.json` 与 `.exit.txt`：匹配故障退出 1，无世界提交，全部服务结清，保留原始错误类型。
- `cooperative-matrix.json` / `noncooperative-matrix.json`：各 270 个测量运行、54 个同条件预热；每条件五个配对 seed，轮换策略顺序。所有同规则轨迹一致。
- `playground-trade.log` 与对应 TRX：当前六项真实应用离线探针通过。首个 pending 测试版本曾因测试渲染未开启 Context 失败，保留在 `playground-tests.log`；修正只让测试明确表示尚无 Context，没有改变产品渲染。

以下是非协作服务下 serial/speculative 有效工作耗时比的五对样本中位数。每运行四角色、两轮，基准模拟延迟 10ms，候选/物理独立服务上限 4。

| 场景 | 独立服务 | 单槽共享服务 |
| --- | ---: | ---: |
| 独立角色 | 3.01 | 0.99 |
| 共享资源 | 1.00 | 0.60 |
| 零延迟连锁 | 1.00 | 0.61 |

这支持“预取可隐藏独立等待，但无法创造后端容量”的判断。共享服务中的失效请求继续占槽，反而拖慢有效工作；后两项单槽场景在五次投机运行中分别记录约 187ms、193ms 的取消后服务。cooperative 相应比值约 0.67、0.64，但两批不是交错测量，不能把小差异精确归因于取消机制；代码和服务时间线更直接证明其成本来源。最终产品策略需要结合失效率和后端容量，不能只看峰值并发或候选采用率。

## 实际应用的两个新增反例

源码在 [PredictiveTimeParallelismTests](../../../../tests/Playground.Cli.Tests/PredictiveTimeParallelismTests.cs)。

**开窗可以改变另一个角色的输入。** 真实小镇 offer → reject → wait → say 前奏使终态报价已经交付双方。a 的 scratch OpenContext 清理报价只影响独立不可变副本；真正 Kernel 开窗则刷新 b 已开启的材料。b 的 ContextSequence、LastOperationSequence、DecisionGeneration 都不变，但完整模型请求已变化。测试要求旧候选废弃、fresh 请求进入 fallback、只提交 fresh 操作。这反驳“同一唤醒身份即可复用结果”，并证明必须把 OpenContext 的领域作用纳入输入验证。

**候选不参与 pending E 恢复。** b 的模型候选保持在途，a 的真实操作 E 持久化后、S 发布前注入 IOException；停止清理 b，关闭 writer，再独立冷开。RecoverPending 完成 a 的已记录事实，无模型调用，重复恢复返回 false；恢复后 cursor/人物材料与正常串行前缀一致，b 没有开启 Context，也没有候选内容。这里证明受控停止与冷开的组合，不声称进程强杀可以确认远端已停止；强杀遗留请求仍须计作 unknown。

## 复现

```powershell
dotnet build experiments/PredictiveTimeParallelism -c Release -m:1
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --self-check --output artifacts/predictive-time-parallelism/new-checks/checks.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --matrix --actors 4 --rounds 2 --candidate-limit 4 --latency-ms 10 --repeats 5 --cancel-mode noncooperative --output artifacts/predictive-time-parallelism/new-noncooperative/matrix.json
dotnet test tests/Playground.Cli.Tests -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify --filter FullyQualifiedName~PredictiveTimeParallelismTests
```

这些证据仍不覆盖真实端点的取消计费政策，也未覆盖全部 M1–M9。运动、唤醒生命周期、未来身体/天气材料和 Human 共存仍需后续实验；不因本轮通过而将 C5 或总 Goal 标为完成。
