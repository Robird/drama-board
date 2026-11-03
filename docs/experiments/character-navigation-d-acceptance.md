# 角色地图与导航：组合验收

日期：2026-11-03。范围为[需求账本](../design/character-navigation/README.md)及[实施计划](../design/character-navigation/implementation-plan.md)的全量 N1–N12。A–C 的历史证据保持原貌；本篇记录当前版本的组合复验、真实控制器使用与交付边界。

## 最终机制与本片修补

Playground 保存个人 Visits/ConfirmedPassages、一个候选路径和一个活动导航。未知连接只显示本端入口；搜索仅消费个人已知有向图。活动路线持有不可变路径值，逐站等 continue，cancel 不取消正在行走的路段。Spatial 仍唯一保存位置和期限，宿主不持有另一份地图或导航权威。

本片唯一生产修改为 [LookaheadProjection](../../src/Playground.Cli/PlaygroundLookaheadProjection.cs) 将活动导航纳入已有的 continue 预测条件。此前中间点会预测 wait，因导航互斥被拒绝。修补后仍遵守原有目标准入：若源角色已经进入通道而目标开始决策，返回 UnknownPlace 并放弃预取，不扩大可见性或并发提交范围。

工程与游戏体验分别独立审阅，主线程复核源码、实际测试和运行记录。没有新增预测器、后台输入、全程自动旅行、道路权限、记忆自动摘要或模型重试。

## N1–N12 完成审计

| 编号 | 当前证明及对应范围 |
| --- | --- |
| N1 | [CharacterMapKnowledgeTests](../../tests/Playground.Tests/CharacterMapKnowledgeTests.cs)：实际绕路到两端仍不知道捷径；只有本人完整到达才确认。当前和途中观察不由 Visits 自动披露目的地。 |
| N2 | 同组新增首次未知穿越的完整轨迹：低水提醒→map→真实关库冷开→continue→到达。地图查询、重绘、续局和确认提醒均保持未知，真实到达才确认；段期限及提醒不被查询改变。 |
| N3 | [NavigationMaterialTests](../../tests/Playground.Cli.Tests/NavigationMaterialTests.cs)、[ActiveNavigationMaterialTests](../../tests/Playground.Cli.Tests/ActiveNavigationMaterialTests.cs) 覆盖两布局/full/delta/Context、入口与途中输出、历史 Opening 的过去语境、猜测输入不背书。D 的成对世界改变本地未知入口对端 signal→road，实际 LlmCharacterController 请求及完整比较键相同；真实请求与人物 history 另经审计。 |
| N4 | [PathSearchTests](../../tests/Playground.Tests/PathSearchTests.cs)：已知长路/隐藏短路、有向邻接、环、断开、平行边、稳定同成本、零段、超范围支路与最短成本溢出。没有为夹具增加生产单向配置。 |
| N5 | [PathQueryTests](../../tests/Playground.Tests/PathQueryTests.cs)、[NavigationTests](../../tests/Playground.Tests/NavigationTests.cs)：每角色候选、替换/清空/拒绝保留、旧与他人 ID、实际起点、异地零段；[NavigationCompositionTests](../../tests/Playground.Tests/NavigationCompositionTests.cs) 实际 durable Fork 继承 ID 后成功启动，子分支替换候选/推进/取消/冷开不改父 Head、Business、cursor。 |
| N6 | NavigationCompositionTests 的同一双角色三地点轨迹：中间 say 确实交付队员，队员确认后，领队 use/map/path 均保持时间、全队位置、活动路径与进度。补给确实消耗物品并增加水分，新候选不改活动路线；领队 continue 才走原定终点并清计划。 |
| N7 | NavigationTests 的 wait/go/follow 拒绝及途中取消→补给→continue；NavigationCompositionTests 在真实适用地点证明 gather/produce 因导航拒绝，取消后同一动作可执行。[切片 C](character-navigation-c-acceptance.md) 的真实 CLI 水分警告轨迹证明 cancel 保留路段期限和提醒，到点不继续。 |
| N8 | NavigationTests：队员移动代数耗尽的本地拒绝、原队伍与真实通信提醒保持；普通 go、navigate、导航 continue 的单段风险预览一致。 |
| N9 | NavigationTests 与 [NavigationRecoveryTests](../../tests/Playground.Tests/NavigationRecoveryTests.cs)：逐人到达、跟随者不继承路线、领队/队员死亡。半队恢复按首个实际到达截取，剩余两次 Kernel Step 必须仍为到达，ForbiddenController 零调用；不假设名字排序。 |
| N10 | [PathRecoveryTests](../../tests/Playground.Tests/PathRecoveryTests.cs) 和 NavigationRecoveryTests：候选建立/清空，以及启动、半队、待确认、下一段、取消后途中各自 S 与 E-only/pending，真实关库、恢复幂等、禁止控制器。局部因果拒绝错知识、错进度、空间错位及旧事实绕过；不重搜、不重新分配 ID、不复活取消计划。 |
| N11 | [NavigationLookaheadTests](../../tests/Playground.Cli.Tests/NavigationLookaheadTests.cs)：生产 scratch 实际到达增知/推进活动路线而源 root/cursor/events 不变；生产 coordinator 输入一致采用，已知地图/候选/导航材料变化拒绝，目标身份相同且实际请求键不同。采用后的 continue 经正式 Kernel 恰提交一次 MoveStarted、一次 TraversalStartedFact，MovementGeneration 恰加一。查询响应也只提交一次；隐私成对请求见 N3。 |
| N12 | A–C 的真实 Human Console 入口操作、续局与 history；D 新鲜 home 的 20 次真实模型调用完成 map/go/path/navigate/continue/cancel/零段及跨进程续局。任务引导、阶段偏离与补充引导均保留，见下节。独立双角色试玩 home 已创建并重开，命令见[试玩](../design/character-navigation/playtest.md)。 |

N11 的实现语义需要澄清：生产预取只在 scratch 执行源角色的一次 continue/wait，遇到目标控制器即停止；目标模型返回的 path/navigate **不在 scratch 执行**。因此证据分成实际 scratch 推进隔离、持久分叉内求路/导航隔离、响应采用后的唯一正式提交；不为了字面上的“分支求路”额外执行预测模型命令或合并世界。三部分共同覆盖原始隔离要求。

## 构建与测试证据

本机 Windows、.NET 10.0.201，所有 dotnet 操作串行；原始证据在忽略目录 `artifacts/character-navigation/`。D 未变更领域 schema 或游戏修订，当前仍为 **25**。

- Release 全 solution、warnings-as-errors、Schema Verify 通过；最终日志 `d-build-final-n2.log`。
- 首轮全 solution 运行覆盖 10 程序集，只有新增 CLI candidate 变体失败。它过早匹配旧 Closed Context 的同名查询，得到 target-changed；修正夹具为溪边的新 Open Context 后，加强目标身份一致和新候选实际请求材料断言，没有放宽为任意拒绝。记录保留在 `d-tests-01.log` / `d-tests-01/*.trx`。
- 修正后的 CLI 全套 691 项通过，0 失败/跳过：`d-cli-final.log` / `d-cli-final/*.trx`。其余九程序集首轮全部通过；最后追加 N2 组合案例后另跑领域全套 **621 项全部通过**，0 失败/跳过：`d-domain-final.log` / `d-domain-final/*.trx`。主线程逐份解析 TRX，以各程序集最终结果合计 **1685 项通过，0 失败/跳过**；证据索引为 `d-test-verification.json`。

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/d-tests-01
dotnet test tests/Playground.Cli.Tests/Playground.Cli.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/d-cli-final
dotnet test tests/Playground.Tests/Playground.Tests.csproj -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/d-domain-final
```

最后两条是针对实际测试修订的复验，不将首轮失败称为全绿。自动验收只证明所列机制，不代替实际人类主观体验。

## 真实模型：请求、行动、经历与续局

新 home：[d-real-llm-01](../../artifacts/character-navigation/d-real-llm-01/)。先写 `plan.json` 固定本机 `http://127.0.0.1:8491/responses`、`deepseek-v4.1-flash`、low reasoning、最多 24 次物理调用、失败停止且不自动重试。单角色 gentle、lookahead off、Gym record。实际运行 **20 次请求，20 个 Completed terminal，20 个模型操作各成功提交一次**，没有故障重试或未决调用；尚余 4 次额度未使用。

运行使用切片 C 的 `b923ec7` Release 二进制，SHA-256 在 plan 中。D 的唯一生产差异是关闭 lookahead 时不执行的预测分支；领域、命令、渲染和模型接线未改变。最终 D 二进制另外创建并重开独立试玩 home。这里不宣称进行了真实模型的导航并行性能测量；预取隔离由当前生产代码的离线受控实验验证。

先通过正常 Human Console `create-goal` 写入短途认路目标，所有地图探索与导航行动由模型经 act 选择并提交。第一阶段 6 次调用包括 map、两次 go、目标进度记录和 `path home from market`，只走亲历的市场→工坊→住处两段路，没有选未知直连。随后跨进程续局，模型复制保存的 `path:a:3:1` 启动；在工坊停站并再次冷开，模型用 continue 到家。

模型接着规划去市场、启动导航，在工坊取消；这些动作都满足任务意图。之后它未及时更新 G1 的进度，重新求路回家、再出发到工坊，**没有自主完成原复合目标的零段收尾**。工程审阅核对当时材料：当前位置、取消结果和无活动导航均正确，G1 自述仍停在旧返程阶段。没有为迎合模型修改领域状态或导航规则。

第 16 次调用后，用正常 Console `update-goal` 明确已完成阶段以及当前只需取消并做本地零段；补充引导原文在 `05-human-progress-correction.input.txt`。接下来 4 次真实调用依次完成 cancel、`path workshop from workshop`、`navigate path:a:8:3` 和 finish-goal。最终 09:45 位于工坊，无活动导航，保留本人零段候选。只读导出及再次 Human 冷开正常。原始偏离没有被删掉或重跑挑选。

这证明两类控制器协议、地图知识、路径身份、导航节奏及恢复可用；它是**有明确任务且一次补充阶段引导的模型试玩**，不证明复合目标能可靠自主完成，也不替代用户自由游玩的主观评价。

工件包含每个进程 stdin/stdout/stderr/退出码、`reserved.json`、五份 `store/llm-runs/*.jsonl`、统一宿主记录、`operations.json`、`history-a.md` 和 `verification.json`。五次模型运行各由调用预算正常暂停，进程退出码 2；导出和 Human 进程退出码 0，stderr 均空。重开没有重放已经提交的模型命令。

主线程独立审计 `audit-d-real.py` 重算 raw/Host 原始 Payload 哈希链，将每次真实 response 的 RequestId、操作身份和唯一成功回执关联，并核对请求材料、usage 与明文 reasoning 投影。20 个 PlainText reasoning 块进入人物记录；本批模型没有普通可见正文块，不能把缺失正文写成数据丢失。请求和 history 未出现检查的内部通道 ID，未走通的直连依然去向未知。完整文件与哈希保留在 home，不复制原始模型内容进设计文档。

## 交付与后续观察

独立 [navigation-play](../../artifacts/navigation-play/) home 为 a=Human、b=本机 LLM，gentle、32 次预算、lookahead 默认 off；已创建和重开，未执行普通行动或真实调用。包含全 Human 覆盖配置、初始化/续局输出及二进制哈希。玩法、恢复、反馈、history 导出和 on/off 比较命令见[试玩指南](../design/character-navigation/playtest.md)。

没有待裁决的导航产品分歧。模型的过期主观目标记录是后续经历管理/目标消费研究的具体观察：重新进入该方向时可复用本次请求、行为及 history，研究如何让已发生事实更可靠地更新目标进度；本阶段不引入自动摘要或目标执行器。静态拓扑、单候选、逐站确认等边界继续遵循[设计延期表](../design/character-navigation/design.md#8-延期与重开条件)。
