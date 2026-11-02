# 独立复核与新目录代表重跑

2026-11-03。由未实现本轮关键算法的审查代理执行源码/原始工件检查，并在新目录重新运行代表性离线入口。**在下述声明范围内通过，没有未解决的阻断。** 本报告提供 C8 的独立证据；C1–C8 的最终集成裁决仍由主线程依据[原完成条件](../README.md#完成条件)作出。

## 本轮实际重跑

工件根目录为 `artifacts/predictive-time-parallelism/r6-independent-review/`。本轮不构建、不运行 dotnet test、不调用真实模型；使用已冻结的 Release 二进制，所有 dotnet 命令均带 `--no-build`。七个命令均实际退出 0，stdout/stderr 与退出码分别保存。

```powershell
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --self-check --output artifacts/predictive-time-parallelism/r6-independent-review/synthetic-self-check.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --lifecycle-check --output artifacts/predictive-time-parallelism/r6-independent-review/wake-lifecycle.json
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --model-time-check --output artifacts/predictive-time-parallelism/r6-independent-review/model-time.json
python experiments/PredictiveTimeParallelism/Verify-ModelTime.py artifacts/predictive-time-parallelism/r6-independent-review/model-time.json --output artifacts/predictive-time-parallelism/r6-independent-review/model-time-replay.json
dotnet run --project experiments/PlaygroundParallelism -c Release --no-build -- --mode fixture --strategy serial --output artifacts/predictive-time-parallelism/r6-independent-review/playground-serial
dotnet run --project experiments/PlaygroundParallelism -c Release --no-build -- --mode fixture --strategy candidate --output artifacts/predictive-time-parallelism/r6-independent-review/playground-candidate
dotnet run --project experiments/PlaygroundParallelism -c Release --no-build -- --self-check artifacts/predictive-time-parallelism/r6-independent-review/controller-checks
```

再次复现时应替换为新的输出目录，不能覆盖这批现场。本批定位为正确性与接线验收，时长不作为性能论据。

| 实际入口 | 本轮结果与独立核验 |
| --- | --- |
| synthetic self-check | Passed；14 次对照、17 类有效输入变更；预算停止、取消、过期故障隔离、匹配故障停止、非协作排空及有限库存守恒标记均为 true。 |
| wake lifecycle | 9 类变化 × 两种完成顺序 × 两种取消模式；36 对、72 运行，Failures=0，unknown 退回需求调用。 |
| model-time 与 Python 重放 | 240 个测量运行、60 组四策略条件；独立重放 1,440 次决定，核验 1,565 个调用区间，完整配对输入/事实相等。重放不证明影响图声明覆盖所有机制。 |
| 实际 Playground fixture | serial/candidate 各 2 次物理替身调用；峰值在途分别 1/2，候选采用 2、废弃 0。两份完整 `semantic-trace.json` 字节相等，包含实际输入、有序事实/cursor 及最终材料；各自真实存档冷开核验成功。 |
| writer / admission | 三种故障注入均退役且拒绝后续准入；原 provider 错误保留。失败请求消耗 cap、旧批次拒绝、未完整尾拒绝、无预留拒绝全部通过。 |
| 六种实际 runner 组合 | 全部通过；详见下表。使用实际 runner、pool、普通控制器与领域发布，客户端完全由内部离线工厂注入，实际网络请求为 0。 |

六种组合的原始请求、时间线、独立反馈文件、世界存档和合成账本均已检查，不只读取顶层 Passed：

| case | 物理替身调用 / 账本 invoked | 核验事实 |
| --- | ---: | --- |
| invalid-text | 3 / 3 | a 的第二次请求 Shared 为一条，Tail 为带真实纠正码的 Observation；没有伪造工具回执，错误纯文本未进入角色材料。 |
| invalid-parameters | 3 / 3 | 非字符串 command 经真实协议校验拒绝，Tail 纠正后只提交 checked-a/checked-b。 |
| feedback | 3 / 3 | 预计请求产生时反馈尚未保存；实际采用后保存一条带真实边界的反馈，下一请求 Tail 为 Action＋ToolResults，FeedbackId/ToolCallId 与保存记录匹配；正文不进入世界经历。 |
| provider-and-dispose | 1 / 1 | 无成功操作；FixtureProviderException 保持为主错误，两个客户端 Dispose 原因另存，运行结果为 failed。 |
| dispose-only | 2 / 2 | a、b 成功提交后，两个 Dispose 原因使结果 failed；主错误保持 null，不抹去成功提交。 |
| stale-provider | 3 / 3 | a 真实 say 改变 b 输入；旧 b 候选及其物理错误被丢弃，fresh b 需求成功；采用 1、废弃 1，未把过期错误变成当前角色失败。 |

两项协议纠正检查的初版曾错误假设纠正在 Shared；实际 feedback-enabled 控制器使用 Tail。最终代码按 Observation 与 ToolResults 区分，且本轮重新核验了实际请求原值，没有改生产协议迁就检查。

## 源码和成本主张审查

- 候选预计控制器始终被扣住物理响应，无法提前执行 feedback 或创建 operation；只有实际 gate 在真实决定边界通过完整输入和连接身份验证后交付响应。领域合法性校验仍独立执行。
- 账本首次 Write/Flush 失败后永久退役，后续物理准入拒绝；provider 结果和诊断失败分开处理，避免重复终态。失败 summary 不依赖失败 writer 恢复。清理先排空候选再释放 pool，主错误与清理原因分别保留。
- `Measure` 当前调用点都是同步 Func/Action，没有跨 await 取线程局部分配值。scope 是 inclusive、可以嵌套且跨并发调用累计，不能相加充当总机械耗时或总内存。分配量不代表持有量/峰值，scope 耗时也可能包含锁等待和同步 I/O。
- `summary.Metrics` 不覆盖配置/连接、Git、初始化、全部 Kernel 规则和最终报告写盘；完整工作计时仍包含准备成本。排空结束单独捕获，冷开验证和收尾总时长另列，不能混用边界。
- 已重新计算 `r5-scale/phase-pairs/` 五对完整语义文件哈希，均相等；每对调用数 2/2、峰值 1/2。其中两对 candidate 更慢，全部样本必须保留；约 1.088 的配对比值中位数不构成稳定收益证明。

本轮没有重跑全部规模矩阵或影响图穷举，也没有把合成小模型的支持范围扩大到真实世界的所有规则。默认产品没有因此启用并行。

## 既有在线证据的独立检查

本次没有新增在线请求。此前独立读取了 `artifacts/predictive-time-parallelism/r3-playground-gate/` 四组在线 manifest/timeline/summary 及统一账本：8 个 invoked 与 8 个 Completed 一一对应。初次 fixed serial 的配置错误发生在任何调用前，Calls=0，核销后使用新批次，没有复用旧批次。

fixed candidate 的客户端在途交叠约 859.3ms，game candidate 约 1,625.8ms；两组 serial 无交叠。这证明宿主请求在途重叠，不直接证明服务器内部生成并行。game 的 b 先返回，但实际只在自己的 transition 3 边界交付；a、b 的真实 handoff 按原顺序提交，四次世界转换的 ordinal 为 0→1→2→3，两组均冷开核验成功。

逐角色完整请求输入在两策略间相同，但生成正文、输出 token 和缓存观察不同；单批在线耗时不能归因为公平、稳定的算法加速。输入 token 分解未知仍为 null，不能补零或计算完整费用。在线仅覆盖双角色同刻首次操作，不替代跨时刻证书、全部 M1–M9 或真实取消计费政策验证。

## 工程证据的独立读取

工程命令由主线程执行，审查代理未重新运行。已读取 `artifacts/predictive-time-parallelism/r4-lifecycle/solution-build.log` 的成功结尾：0 警告、0 错误；构建命令包含 Release、`-m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`。

独立解析同目录 `solution-tests/` 的 10 份 TRX：total/executed/passed 均为 **1,502**，failed=0、notExecuted=0。这次全量检查先于末轮追加的 M4 同地角色离开反例，不能将 1,502 的结果追认为新测试已执行。

追加后主线程重新执行完整 CLI suite。独立解析 `artifacts/predictive-time-parallelism/r4-lifecycle/cli-final.trx`：**582/582 通过、零失败、零跳过**；按 TestDefinitions/Results 的 testId 关联确认其中 **19/19 研究测试通过**，包含 `Departure_from_the_visible_place_discards_same_time_same_context_candidate`。这是后续受影响项目检查，不冒充第二次全 solution 重跑；实验源码和 r6 使用的程序集没有因新增测试而改变。

`integrated-manifest.json` 与 `integrated-final-manifest.json` 均保留；分别对核验时的 22 个来源文件重算 SHA-256 并匹配，后者对应最后追加测试的源码与 CLI 结果。在线账本哈希亦匹配，仍为 8 个 invoked/finished。结果分别保存在 r6 的 `integrated-verification.json`、`integrated-final-verification.json`，旧 manifest 另有 r6 副本。没有把只读源码或旧结果称为本轮新执行。

主线程收尾核验研究目录、两个实验入口 README 及项目状态共 18 个 Markdown、186 个本地链接和其锚点，零缺失；结果保存于 `r4-lifecycle/links-final.json`。集成差异检查通过，原始失败工件与在线账本保留，未推送或公开发布。

## 来源与工件哈希

本次开始时主仓 HEAD 为 `f383e05e86f8e6e67905e309b4a04eb0dc8455f8`，存在研究工作树改动，详见 `review-manifest.json`；不能将该 commit 单独视为全部运行源码。两个实验入口程序集在本轮前后 SHA-256 相同：

| 程序集 | SHA-256 |
| --- | --- |
| DramaBoard.PredictiveTimeParallelism.dll | `B1200036459D18B77CD5CC2275EBC9C24FF5D9DC2A118445F50648F877506750` |
| DramaBoard.PlaygroundParallelism.dll | `9A33B14115D329FBE1A8CA107C51C79C4AA9538FDEA59D0D957BE14B8B136EDF` |

| 工件 | SHA-256 |
| --- | --- |
| r6 review-manifest.json | `8803B0A539FA48BED845155EAD282BD3B26766A9B942EB4B195FD60DFFC89A54` |
| r6 synthetic-self-check.json | `8993F621DE8F2B3FE03113DF6D4B008AD90D4A9C6F9D508BEF794136CEBF60E4` |
| r6 wake-lifecycle.json | `83A397DAEBD6B216ED1194D360BCE1684340A5E8D67F114A7EDAF8E35F9AF1C6` |
| r6 model-time.json | `DB6413ECD4C41CC468BB563CA3F71E8FC3543272C454620AB839BF026823D7BC` |
| r6 model-time-replay.json | `E921AADEC7BA32656C6A513D00E257504DA8F98777C7D07AEF1F1819E213758B` |
| r6 controller-checks/summary.json | `4DFE0CB469F7729CC726235CD2F949E90CFEBB0EDB836234EF2E50C31DF28CFA` |
| r6 两份相等的 semantic-trace.json | `EF6E75AEE5E57CC09035972571AA64A54AB5AD1D5C872B55763F28E21F452BE2` |
| r4 integrated-manifest.json | `8758AEC0569E6F65FFD95D265E41E686C6A99ADCFCEF82D23E6855A58C2C8337` |
| r4 integrated-final-manifest.json | `6964E44D397061D17DE2071F2B33A866B33C0A8B64ABA6F9A1888946EAC52728` |
| r4 cli-final.trx | `367E26769D030FB30A0F08792956D058A7A57187CD841984E1939EE95170F8BC` |
| 统一 online-budget.jsonl | `2E802B96A5CE2E42A862B94278EDABE9CE0C2B4ED38FC507C5D9674E1749E373` |

表中 r6/r4 分别是完整前缀 `artifacts/predictive-time-parallelism/r6-independent-review/` 和 `artifacts/predictive-time-parallelism/r4-lifecycle/`。机器可读验收、全部汇总哈希和逐例结果保存在 r6 的 `verification.json`；完整请求、原生模型材料与存档继续留在忽略工件，不随本报告提交。
