# 首轮合成实验与后续反例

2026-11-03。主线程实际执行；产品基线 `83dcc9e`，实验源码尚未提交时运行，精确源码哈希和工件哈希见 `artifacts/predictive-time-parallelism/r0-baseline/manifest.json`。本报告是初步测量，不是 C1–C8 总验收。

## 实际验证

- 根 solution Release + Schema Verify 构建成功，零警告/错误。
- 独立实验工程 Release 构建成功。自检查包括 12 个串行/候选轨迹比较、18 次候选失效、17 个有效输入字段变更、2 个服务容量检查，以及调用上限/取消停止。
- 影响算法检查 729 个三节点图、21,870 次查询、196,830 个保守性条件，无发现漏算；范围限于报告声明的小模型。
- 首批矩阵：6 场景 × 3 服务模型 × 3 策略 × 5 配对种子 = 270 次运行；全部策略的同规则轨迹指纹一致。
- 本研究尚未进行真实 LLM 调用，尚无实际 Playground 并行证据。

## 初步测量

4角色、每人2次决定、候选上限4、基础假延迟10ms。下表为五个配对样本 serial/speculative 墙钟耗时比中位数；大于1表示提前推理较快。

| 场景 | 独立服务 | 单服务站 |
| --- | --- | --- |
| independent | 3.55 | 0.99 |
| shared-resource | 1.08 | 0.67 |
| new-message | 1.04 | 0.66 |
| zero-chain | 0.98 | 0.69 |
| delayed-message | 2.87 | 1.08 |
| future-time | 3.57 | 1.02 |

这些结果支持继续研究后端容量与输入耦合：可重叠服务有收益，容量固定且高失效率时会增加排队。不能把合成响应延迟当成真实端点性能。

## 审查发现及下一轮

1. 当前矩阵固定 serial→safe→speculative，且预热不足；短定时器结果可能有顺序偏差。下一轮各策略等条件预热、轮换执行顺序，再测量。
2. 当前失败路径虽取消排空，但只打印异常，不输出失败时间线；取消测试仅证明抛出异常，尚未独立证明资源结清。下一轮保留失败snapshot并检查调用结算与剩余候选。
3. delayed-message 的延迟按提交边界计数，不能解释成模型时间传播。后续须建显式模型时间事件对照；本轮不满足 C4。
4. safe 目前只对白名单 independent/future-time 开放，与影响估计尚未联动；不能据此声称已实现一般安全窗口。
5. 真实应用适配正在进行；首选 Completion gate 捕获现有控制器完整请求，在实际边界验证后才交付响应。

## 重跑

```powershell
dotnet build experiments/PredictiveTimeParallelism -c Release -m:1
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --self-check
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --influence-check
dotnet run --project experiments/PredictiveTimeParallelism -c Release --no-build -- --matrix --actors 4 --rounds 2 --candidate-limit 4 --latency-ms 10 --repeats 5 --output artifacts/predictive-time-parallelism/repro/matrix.json
```

命令针对本报告版本；后续修改应保留来源commit及原始工件，不覆盖首轮结果。算法合同见 [影响模型](influence-model.md)，研究状态见 [工作计划](../work-plan.md)。
