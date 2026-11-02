# 平衡测量与实际 Playground 离线接缝

2026-11-03，主线程实际构建/执行并复核工件。来源与哈希见 `artifacts/predictive-time-parallelism/r1-balanced/manifest.json`；输入为本次未提交研究源码，基线 `83dcc9e`。前次失败及修正前矩阵仍保留在 r0-baseline 与 r1-balanced/playground-tests，不覆盖为成功。

## 合成原型修正后的证据

- 三策略各自等条件预热，测量顺序轮换：54次预热、270次正式运行，所有配对轨迹一致。
- 四角色独立场景 serial/speculative 配对中位比：独立假服务3.14、单服务站1.06；共享资源单站0.71，零延迟闭包单站0.67。仅是假服务上的初步测量。
- 失败snapshot已输出完整时间线、提交前缀、候选/调用结算；实际CLI `--total-call-limit 1` 返回1，结果包含 Invoked=1、Canceled=1、Completed=0、RemainingCandidates=0。
- 真正提前发出的候选和即时需求调用分别计数，逐决定记录等待/提前量。自检查12项轨迹对照、17字段变更、预算/取消等全部通过。
- 本地协作取消会释放假服务容量；未模拟远端继续计算计费。delayed-message仍按逻辑提交边界计时，safe仍采用场景白名单。这些限制尚未解决，不能宣布C3/C4端到端完成。

## 实际 Playground 探针

[测试源码](../../../../tests/Playground.Cli.Tests/PredictiveTimeParallelismTests.cs)通过4项离线验证，使用真实控制器、CLI renderer、Kernel、存档及独立只读重开：

1. 两角色同地各自私有handoff的首次请求确实重叠，先返回的b未改变提交顺序；实际输入、完整事实投影、游标和最终人物材料与串行参考一致。尚未验证远处移动中的完整生活循环。
2. a对b说话后，b的旧请求失效，使用实际输入重新取得响应；旧候选未进入人物材料。
3. feedback响应在预计控制器gate中被扣留，实际采用后才执行保存回调，并以真实回执继续普通工具循环。本探针回调是计数fixture，不声称已验证反馈文件刷盘。
4. 在途候选取消后排空控制器，真实S、cursor、pending和Context不变；不推断远端未计算。

产品改动仅本仓 Playground→Playground.Cli.Tests friend 声明。预计Context在测试scratch中用真实OpenContext/Fold/Rebuild准备，实际Context仍按原Kernel顺序提交；没有新增产品预览API、默认并行或跨仓修改。

固定模型ToolCallId不变；测试宿主在提交前分配确定的OperationRecordId。事实比较为类型感知投影：可见联合值保存全部存储字段，避免访问非数值时会抛错的computed Number；投影在E发布前准备。独立边界检查先关闭写者再重开只读，未绕过存档owner锁。

## 重跑与未完成

```powershell
dotnet test tests/Playground.Cli.Tests -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify --filter FullyQualifiedName~PredictiveTimeParallelismTests
dotnet run --project experiments/PredictiveTimeParallelism -c Release -- --self-check
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --matrix --actors 4 --rounds 2 --candidate-limit 4 --latency-ms 10 --repeats 5 --output artifacts/predictive-time-parallelism/repro-v2/matrix.json
```

尚未进行真实模型调用；测试私有gate尚未提取为可运行在线适配；未知provider故障/预算公平、pending恢复、真实宿主日志与模型时间安全窗口还须继续验证。下一步应先打通影响估计→候选选择的实际合同，完善模型时间规则实验，再做实际应用在线对照。研究Goal保持进行中。
