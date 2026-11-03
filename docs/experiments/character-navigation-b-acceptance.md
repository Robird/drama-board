# 角色地图与导航：切片 B 验收

日期：2026-11-03。范围为 [实施计划](../design/character-navigation/implementation-plan.md)中的普通 `map/path` 查询与持久候选；导航执行仍待切片 C。

## 交付合同

`map` 和 `path <目标地点ID> [from <起点地点ID>]` 对 Human Console 与 LLM 使用同一角色命令协议。查询不推进模型时间、不确认身体或通信提醒、不更新任何角色的 Visits。途中只能在既有决策窗口查询，且求路必须显式提供已知起点。

搜索仅消费个人 KnownMap 中的已确认有向邻接。采用 A* 的 h=0 形式，以公开固定行程求最小总成本；入口 Ordinal 顺序加队列入队序号稳定处理同成本，等成本不替换先发现的路线。使用 checked Int128 累计，超出 long 的支路不影响其他可表示路线；最短路线仍超范围时返回拒绝、保留原候选，不崩溃或冒充无路。没有新增生产内容的单向道路配置。

每角色在 Expedition 中保存一个不可变 PlaygroundPath。ID 由 actor/context/operation 身份确定，保存起终点、具体入口步骤和成本；没有全局注册表或分支 ID。成功查询替换、合法无路清空、语法/未知地点/成本溢出拒绝保留、死亡清除。新 Context 与冷开读取原候选，不重新求路；分支继承可继续使用，子分支替换不修改父分支。

当前候选进入冻结 Observation、Context、两布局的 full/delta 与人物 history。地图与路径展示已知地点名称和 ID、本端入口与分钟；明确区分规划和真实位置、路线成本与实际抵达时间。异地零段只说“规划起终点相同，无需移动”。本片不提供 `navigate`，远处步骤不列为当前可执行入口。

主要代码：[Path/Search](../../src/Playground/PlaygroundPath.cs)、[PathRules](../../src/Playground/PlaygroundPathRules.cs)、[Expedition](../../src/Playground/PlaygroundExpedition.cs)、[NavigationView](../../src/Playground.Cli/NavigationView.cs)。游戏修订 **24**；使用新 home/store，旧开发存档不迁移。

## 独立审阅与自动验证

工程审阅确认 Fold/状态校验只检查记录的身份、本人已知性、连续步骤、成本和端点，唯一搜索调用留在 Plan。审阅发现并修复同起终点可以被伪造成无路/绕圈的问题：事实必须有候选，状态中的该路径必须为零步骤、零成本。PlaceId 也增加空白拒绝，避免合法内容产生无法用命令表达的地点。

体验审阅检查了实际 Console 产物：旧规划与当前位置分开；异地零段不冒充到达；重新求路前保留原路径；每段 ID 与实际可执行入口的区别清楚。未把代理脚本验证称为人类自由试玩。

| 验证 | 本次结果与原始证据 |
| --- | --- |
| Release 全 solution、warnings-as-errors、Schema Verify | 通过，0 warning、0 error；`b-build-final-02.log`。新增 8 份 schema 历史由 Publish 生成。 |
| 全 solution 测试 | 10 个程序集、1637 项通过，0 失败/跳过；`b-tests-01.log` 与 `b-tests-01/*.trx`。 |
| 最后一次状态校验调整后的针对性复验 | 将同起终点零段约束下沉到冷开路径值校验后重新构建；27 项路径相关测试全部通过，`b-tests-final-path.log` 与对应 TRX。其他程序集此前已通过，未重复运行。 |
| 最终二进制 Console | 10 个普通操作、两次续局与 history 导出通过；`b-console-final/verification.json`、原始 input/output/error 和 `operations.json`。 |

原始文件在本地忽略目录 `artifacts/character-navigation/`；首个包含新测试的构建发现一处 nullable 编译错误，修正后上述构建和测试均通过。命令按仓库要求串行执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/b-tests-01
dotnet test tests/Playground.Tests/Playground.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Path" --logger trx --results-directory artifacts/character-navigation/b-tests-final-path
```

关键行为测试：

- [PathSearchTests](../../tests/Playground.Tests/PathSearchTests.cs)：有向邻接、隐藏边、环、平行边、稳定同成本、不可达、零段、溢出与可表示备选。
- [PathQueryTests](../../tests/Playground.Tests/PathQueryTests.cs)：提醒与时间/Visits/知识守恒、途中查询、候选生命周期、双角色隔离、死亡清槽、超范围拒绝及值不可变。
- [PathRecoveryTests](../../tests/Playground.Tests/PathRecoveryTests.cs)：建立/清空候选各自 S 与 E-only/pending 恢复、禁止控制器再次调用、恢复幂等、实际持久分叉、坏事实明确因果拒绝。
- [PathMaterialTests](../../tests/Playground.Cli.Tests/PathMaterialTests.cs)：两布局、增量候选清空、新 Context 自足、磁盘重开、未知查询保留、异地零段；格式断言围绕信息含义而非整页固定布局。

## 实际 Console 轨迹

fresh home、单角色 a=Human Console、gentle 内容、Gym record，顺序运行：

```text
map
path market
path home
go e-r6
go e-a9
path home
:quit
```

首次 market 尚未知，求路拒绝；随后经家→工坊→市场，`path home` 返回入口 e-v4、e-j3，共 30 分钟，没有利用尚未走通的市场→家直连。冷开后同一候选 ID 及步骤仍在。

继续 `go e-k7` 走通市场→家，再 `path market` 才选 e-m2 的 15 分钟直路。`path market from market` 创建异地零段，`path nowhere` 拒绝但保留该候选；再次冷开与 history 导出均保持材料。

本次轨迹共 10 个普通角色操作及两次续局，保存每次 stdin/stdout/stderr、结构化操作回执和人物 history。最终 home 为 `artifacts/character-navigation/b-console-final`，绕路候选 `path:a:3:1`、最终异地零段候选 `path:a:4:2` 在对应冷开后保持不变。真实 LLM 调用为 0；模型侧验证本片命令指导和共享冻结材料，真实完整路线调用留到 D。

## 尚未完成

N4 与 N5 的查询、候选保存/分支部分已有本片证据，N10 的新增候选状态恢复已覆盖。路径 ID 启动、活动导航持有旧路线、逐站 continue、途中 cancel、带队与危险预览复用尚未实现；不能把这些要求算作已验收。N11 的实际 lookahead 世界中探索/求路/推进导航后丢弃、N12 的真实 LLM 全流程和最终组合验收继续保留。
