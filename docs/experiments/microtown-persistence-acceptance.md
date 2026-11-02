# 小镇持久化与 Human Console 切片验收

> 历史记录：Microtown 源码、测试和 smoke 已于 2026-11-02 从主线移除，见 [Git tag 归档](../archive/microtown.md)。本文命令、实现状态和测试结果属于当时版本。

日期：2026-10-31。属于[微型小镇里程碑](../codex-goals/microtown/README.md)的一个纵向切片，不代表整目标完成。主线程集成并验证；独立 subagent 审查存档/恢复边界，并另行实际操控 Console。没有真实 LLM 调用。

## 交付与边界

- [TownDurableHistory](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/Persistence/TownDurableHistory.cs)：独立游戏根/事件，二者携带 `microtown` / GameRevision 1；只读还原、完成 S 和 pending E 分离、恢复及精确完成决策 S 分叉。当前只接受本版本，不提供迁移。
- [TownOperation](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownOperation.cs)：原始输入、ToolCallId、OperationRecordId 在解析前保留，成功/拒绝回执存入事实和本人状态；本人最近活动结果独立保存。
- [TownObservation](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownObservation.cs) / [TownTextRenderer](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown/TownTextRenderer.cs)：角色可见冻结材料，列出规则、动作范围和阻因；本人私有状态不泄露给其他居民，远处市场余额不泄露。
- [Microtown.Cli](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/src/Microtown.Cli/README.md)：同一 Human Console 服务三个居民，磁盘新档/冷开、预算、EOF/:quit 暂停、`:look` 和明确区隔的开发者经济统计。

Open 检查身份、修订与历史邻接/游标并重建完成 S；pending 的业务执行及完整效果校验在正常 Kernel 恢复时进行。没有为了启动而执行尚未完成的决定。失败后必须重开，不能从宿主日志重做已经发布的操作。

拒绝保持 DecisionSequence，却产生新的 OperationSequence 和完成 S；`CaptureDecisionBoundary` 捕获当前精确状态，不能以“仍是同一轮”为由沿用旧 S。分叉保留游标/lineage/经济前缀，排除源未来。总账以游戏状态为权威，当前统计区间是新游戏开始至当前，不是进程启动至当前。

## 工程验证

主线程实际执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

构建零错误/警告；全仓 11 个测试项目、1165 项全部通过。Microtown 47 项；新增 13 份初始 DG Schema 已通过 Verify。原游戏没有生产代码改动；其 Console、Completion、领域及共享组件均纳入本次全仓回归。日志保存在 `artifacts/microtown-persistence/build.log`、`tests.log`。

关键证据：

| 不变量 | 实际覆盖 |
| --- | --- |
| 冷开不依赖内存运行对象 | `TownPersistenceTests.EveryEconomicBoundaryColdReopensAndContinuesTheSameTrace` 每个发生项后关闭/重开，与内存基线对比业务、空间、游标及 cause；运行 26h，含目标、移动、生产、消费、外售和市场刷新 |
| pending 不重复决定或效果 | 磁盘 E-only 恢复采购、拒绝、目标操作时，控制器设置为一调用就抛；原始输入/ID/结果、余额及操作序号保持；再次恢复无效果 |
| 发布中断不安装未提交世界 | S 发布前/后注入异常，旧 Kernel 不安装，重开根据真实磁盘结果恢复；另测错误游标导致 adapter 退役 |
| 加载及拒绝不改文件 | DG 句柄关闭后比较全文件 SHA-256；Open 后在执行 pending 前再次关闭比较；实际 Town/Survival 双向错开均拒绝且文件不变，正确适配器仍可打开 |
| 精确分叉与隔离 | 源历史成员检查、禁止 E/无决策 genesis；从旧完成 S 及拒绝后的新 S 分叉，冷子分支执行不同合法动作，父分支/累计账保持原样 |
| 角色可见性 | 本人 Goals/回执/背包保留；其他角色私有内容不出现在序列化观察或渲染；远程市场余额变化不改变观察输出；旧观察不随后续交易变化 |
| 动作提示与规则一致 | 三地点逐个核对示例、每个宣称合法数量及超出上限的拒绝，不依赖整页文本快照 |

首轮测试工具尝试在 DG 独占句柄打开时扫描文件，触发 Windows sharing 错误；已改为关闭会话后比较。最终绿色结论来自修复后的主线程实跑，不把先前失败描述成引擎故障或忽略它。

## 实际 Console 证据

主线程运行 [Smoke-Microtown.ps1](https://github.com/Robird/drama-board/blob/b6fea5e94ca3ea718cee2546a2d785afa1bafa6d/scripts/Smoke-Microtown.ps1)，原始材料在 `artifacts/microtown-persistence/console/`。三个独立进程完成：

1. 新建 25h 游戏，a 采购过量被拒，再由三个居民各买 4 谷物；达到本次操作预算后暂停。
2. 冷开交付已保存回执；移动到工坊，各生产一批饭、吃一份、等待。经过第 24h 外界市场更新，到第 25h 有限终点。
3. 结束档再次冷开，不调用控制器、不重复产出或消费。

最终三个居民各 10 钱、饱食 25/100，无失败；总进口支出 24、现金 30；产饭 6、吃饭 3、保有饭 6（含初始 3）；市场更新一次。逐项账实核对通过。该 Console 片段没有展示完整双产品外售，完整经济路径由先前 48h 测试和本轮 26h 逐边界冷开测试覆盖。

独立 agent 另运行七个真实进程，材料在 `artifacts/microtown-persistence/independent-20261031-194520/`：首次 EOF 暂停、冷开中文/emoji 私有目标创建与查询、b/c 独立等待、`:look`/`:quit` 不改变完成 S，以及格式错误/不存在分支友好退出。不存在分支没有改变任何存档文件。主线程检查原始输出与 evidence.json。

独立反馈指出长页输出会使角色标题滚出屏幕，已将输入提示改为 `action[a]` 等角色标识；用更新后的 Console smoke 再验收，证据保留在 `artifacts/microtown-persistence/console-final/`。当前英文主体与中文共享 Goal 回执混用，未进行多语言框架改造。

以上是 Coding Agent 向 Human Console 提供 stdin 的工程试跑，不是实际人类合作或真实 LLM 居民验收。

## 仍未完成

共享 LLM/Gym 尚未成为小镇运行路径；还需模型连接/角色绑定、真实请求及反馈、已提交操作查询与反馈定位分叉命令。仅有精确 S API 不等于反馈闭环已贯通。

当前最新回执/最新本人经历不是完整 Context 历史。居民交流、赠与、原子报价交易、忙碌通知/取消仍需实施；有界未读经历交付不能靠 Gym 全知日志替代。Town 自身 owner 预测缓存与真实多居民模型实验也尚未完成。剩余工作只维护于[工作计划](../codex-goals/microtown/work-plan.md)。
