# Playground 单分支推演与 LLM 预取：研究结论

2026-11-03。原定 G1–G7 均已具备实现、实验与独立验收证据，研究完成。本研究不自动合并主线、推送或发布。

## 结论

**无需猜中 A 的动作；只要 B 的完整有效请求及目标身份一致，就能安全地提前计算 B。** 已在真实 Host 中验证预测 A wait 1、实际 wait 2 仍采用 B，且保持唯一 Kernel 串行提交。预取只是响应缓存；不安装预测世界、不搬运预测事件、不提前执行工具或反馈。

40 个测量运行、20 组配对全部满足完整输入、操作、事实、最终状态一致。固定 1 秒服务时，低耦合且有独立容量的工作墙钟配对改善中位数为 **31.71%**，调用数不增加；共享瓶颈下为 **−3.48%**。高耦合独立容量为 **−0.19%** 且多出 50% 调用；高耦合共享容量为 **−23.73%** 且多出 25% 调用。五对全部样本、真实可服务后的等待、整个 Host 时长、废弃服务及日志成本见[连续对照](resident-experiment.md)。

本阶段另完成 **16 次真实模型调用，16 Completed、0 未决**，证明实际采用、废弃、提交及同一存档续局。真实输出造成不同轨迹，线上时长不构成公平加速比，usage 未知项保持 null；见[在线接线](online-integration.md)。

**建议：可考虑作为默认关闭的实验能力合并；不建议默认开启。** 可重复受益范围是低耦合、完整输入稳定、服务确有空闲并行容量且调用足够慢的情形。全部代码保留在 `experiment/playground-speculative-lookahead`，没有执行主线合并。生产接缝限 Playground Host，未修改领域 Schema、游戏默认规则、Kernel 提交权或跨仓 API。

## G1–G7 证据索引

| 条件 | 实际证据与边界 |
| --- | --- |
| G1 实际入口 | `--lookahead on|off` 与 home 顶层 `lookahead`，默认 off、CLI 优先；[原型验收](runtime-prototype.md)和[独立重跑](independent-review.md)均执行真实 CLI init/new/resume/CWD。连续矩阵及在线批次走生产 Host，已有角色与连接配置沿用。 |
| G2 有界推演 | [Projection](../../../../src/Playground.Cli/PlaygroundLookaheadProjection.cs) 自建独立内存 history/Kernel，仅预测一次合法 continue/wait 1；128 步、24 模型小时、250ms 协作上限；[ProjectionTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadProjectionTests.cs)与[时间/隔离证据](lifecycle-and-isolation.md)覆盖天气随机地址、移动到达、原 world/cursor、非法默认动作、重复 A、Human/地点/限额。存档不写入由能力边界审计及持久 Host 隔离测试共同证明，未做整个 store 字节哈希实验。 |
| G3 正确采用 | [RuntimeTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadRuntimeTests.cs)证明猜错 A 仍命中 B、同目标声音输入失配；[InputTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadInputTests.cs)保护完整请求字段/顺序/未知形状失败关闭；[时间/目标变化](lifecycle-and-isolation.md)覆盖共享昼夜、移动和目标替换；在线与定向测试均包含已完成/在途采用。20 对固定响应全轨迹一致，模型可见字符串不归一化。 |
| G4 生命周期 | [LifecycleTests](../../../../tests/Playground.Cli.Tests/PlaygroundLookaheadLifecycleTests.cs)、[补充证据](lifecycle-and-isolation.md)和[LlmHostTests](../../../../tests/Playground.Cli.Tests/LlmHostTests.cs)覆盖统一物理预算、候选异常、非协作取消排空、重复回合、真实反馈/纠正、Human 阻塞与 EOF、pending 恢复、主日志/raw 故障、候选 history 隔离。在线脚本也注入记账失败并保留原错，未追加网络请求。 |
| G5 连续对照 | [正式矩阵](resident-experiment.md)：低/高 × 独立/共享 × 五对，共 40 run、每个 16 成功操作，另八个预热；全部跨多个模型时刻并反复创建/回收候选。新[在线批次](online-integration.md)为 off6/on6/resume4，独立总账，未复用旧研究调用证据。 |
| G6 成本与建议 | [全部样本与计量](resident-experiment.md)分别报告工作窗、整段 Host、可服务后等待、物理调用、废弃服务、推演/比较、生产 raw/HostJournal/fixture 日志及内存 E/S。保留短服务和高耦合负结果；计时有重叠，不相加。不把 in-memory 结果称作磁盘性能。 |
| G7 独立交接 | [独立审查](independent-review.md)已用新目录重复 8 个代表样本和四个真实 CLI 入口；主线程最终 Release/Schema Verify 和完整 solution 测试通过；新目录工件及工程证据见下节，专题相对链接与集成 diff 在最终提交前核查。 |

## 工程与复现

代码提交：`a32e99a` 为宿主预取，`a063efa` 为常驻实验、计量和故障/隔离覆盖。初始方案在 `eda1df4`，研究基线 `3ddc384`。正式矩阵来源与依赖状态、运行后构建前二进制哈希都在工件中；较大工件位于忽略目录 `artifacts/playground-speculative-lookahead/`，不会随 Git 文档自动分发。

最终 Windows 工程检查均退出 0：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/playground-speculative-lookahead/p4-engineering/tests
```

构建零警告、零错误；10 个测试项目合计 **1583/1583 通过，零失败、零跳过**，其中 CLI 662。`p4-engineering/summary.json` 保留每份 TRX 的计数及哈希；build.log SHA-256 `10028c55d2b6d9222d78db5dd314d3762b234bc6e16bdbfd8d69cc07c586e363`，test.log SHA-256 `9328df9c839ab19f21a069801a049ffff819a59a5144556d14204d413e61e33c`。未执行 Linux 构建或真人游玩。

独立 100ms 服务的代表重跑同样满足完整轨迹一致，但四类都比关闭慢，属于保留的短服务负结果，不能称为 1 秒收益的复验。主线程另抽查线上采用 physical-2 的完整 request 与实际 logical request 原值相等，复核正式 20 对/独立 4 对比较记录、独立 CLI manifest 及全部 TRX。

从仓库根复现离线矩阵（输出目录必须不存在）：

```powershell
dotnet build experiments/PlaygroundLookahead/PlaygroundLookahead.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -warnaserror
dotnet experiments/PlaygroundLookahead/bin/Release/net10.0/DramaBoard.PlaygroundLookahead.dll --output artifacts/playground-speculative-lookahead/my-resident --pairs 5 --operations 16 --delay-ms 1000
```

实际已有 home 可追加 `--lookahead on`，或在 `playground.json` 中设置 `"lookahead": "on"`；省略仍关闭。在线 runner 只接受新批次 grant 与已有预留账本，不能重用本次已消费 grant；默认实验入口完全离线。详细用法见[实验 README](../../../../experiments/PlaygroundLookahead/README.md)。

## 证据限制与重开条件

- 只支持当前完整首请求形状：普通 Observation、空 Tail、当前标量工具 schema。未知形状停止预取，真实需求仍走普通路径；没有原生多轮会话候选缓存。
- 跨场景不是独立性证明。共享昼夜用例同时改变时间，不能单独归因于 light；独立 input-changed 门禁由同目标 shout 用例证明。没有单测共享库存竞争；受控 stale 目标实验也不伪称自然 Host 会保留候选到那个时刻。
- 只有一条预测分支、一个额外物理请求。准备时限在同步步骤间检查，无法抢占单个规则/渲染；非协作 provider 停止仍需排空，取消不保证远端未执行。冷开不恢复候选，不自动重试。
- 矩阵使用内存 E/S 和内存主日志，生产 raw 则实际写文件；独立容量响应服务中取消，共享容量一旦开始服务不响应。因此 profile 间同时改变容量与取消策略，不能把全部差异归因于容量。
- 本机 Windows 与单一服务时长的五对样本不代表所有平台、模型或世界。在线 usage 部分未知，未宣称完整费用；脚本化 Human 验证也不等同真人体验验收。
- 后续若要推广启用范围，先量化实际服务容量、失效率与同步日志成本。多分支、影响图、候选持久恢复、改玩法以增加窗口均不在本轮中启动；应由新需求和新证据重开。
