# 角色地图与导航：切片 A 验收

日期：2026-11-03。设计入口：[角色地图知识与导航](../design/character-navigation/README.md)。本次完成入口身份、个人连接知识及共享呈现；不宣称 map/path、候选路径或导航执行已经实现。

## 实现结果

- 唯一通道定义配置两端各自的入口 ID，内容校验拒绝重复、空白及与内部 PassageId 相同的身份；内容指纹覆盖两个入口。`go` 仅接受当前位置的 EntranceId，错误入口和原始 PassageId 均返回相同的本地拒绝。
- 静态地点与入口文案改为本地感官线索。内部 Spatial 和原始事实继续使用真实 PassageId；角色命令、途中材料、可见结果和呈现改用入口身份。
- Expedition 保存个人 ConfirmedPassages。只有本人完整到达才在事实中记录新连接，已访问两端、进入通道、途中观察或重绘均不授知。校验要求确认连接的两端均有本人 Visits，拒绝篡改到达事实中的角色、通道或缺失增量。
- Observation 冻结 KnownMap，保留地点 ID、本端入口和已确认目的地；Console 两种布局、角色 Context 与 LLM 材料共享投影。资源快照仍表达历史观察，不变成远方实时状态。
- 跟随者分别在本人到达时确认知识；重复穿越不重复添加。S、E-only/pending 与队伍同刻到达中间状态继续恢复。旧根和旧观察不随新知识改变。

主要实现：[WorldDefinition](../../src/Playground/PlaygroundWorldDefinition.cs)、[KnownMap](../../src/Playground/PlaygroundKnownMap.cs)、[Expedition](../../src/Playground/PlaygroundExpedition.cs)、[Planning](../../src/Playground/PlaygroundPlanning.cs)、[FactApplication](../../src/Playground/PlaygroundFactApplication.cs)、[PlayView](../../src/Playground.Cli/PlayView.cs)。

本次游戏修订号为 **23**，旧开发存档不兼容；使用新的 home/store。Schema 由工具 Publish 生成，随后以 Verify 验证，不删除既有历史文件。

## 审阅与自动验证

领域和呈现由两个有界实施任务完成，软件工程和游戏体验分别独立审视，主线程统一构建、运行与集成。审阅促成了入口拒绝空白、确认连接必须具有两端个人访问记录、反向入口描述自足，以及“入口去向未确认”的准确提示。

| 证据 | 本次结果 |
| --- | --- |
| Release 全 solution、warnings-as-errors、Schema Verify | 通过，0 warning、0 error。 |
| 全 solution 测试 | 10 个测试程序集，共 1607 项通过，0 失败、0 跳过。 |
| `Smoke-HillsideContent.ps1` | 四个实际 CLI 进程通过，覆盖生产/移动/销售、保存内容续局、显式内容不匹配拒绝且 store 不变。 |
| 新鲜 Human Console home | 原始 PassageId 拒绝，入口出发、回程、冷开续局与人物 history 导出通过。 |

构建与测试命令（仓库根目录，串行）：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/character-navigation/a-tests-final
pwsh -NoProfile -File scripts/Smoke-HillsideContent.ps1 -OutputDirectory artifacts/character-navigation/a-content-smoke
```

原始记录保存在本地忽略目录 `artifacts/character-navigation/`：`a-build-verify.log`、`a-tests-final.log`、`a-tests-final/*.trx`、`a-content-smoke/result.json`。首次测试发现八项旧身份/知识夹具不再匹配新规则，修正夹具并保留行为断言后重新运行全套；最终结论来自 final 文件，不使用首次失败记录冒充通过。

关键测试：[CharacterMapKnowledgeTests](../../tests/Playground.Tests/CharacterMapKnowledgeTests.cs) 覆盖绕路访问两端仍不知道捷径、实际到达授知、无效事实及旧根隔离；[FollowingRecoveryTests](../../tests/Playground.Tests/FollowingRecoveryTests.cs) 覆盖各队员到达与恢复；[NavigationMaterialTests](../../tests/Playground.Cli.Tests/NavigationMaterialTests.cs) 覆盖探索全程的两布局、full/delta、Context 与猜测输入边界。猜测输入可以原样留在操作记录，系统材料不得为其背书。

## 实际 Console 轨迹与边界

新 home：`artifacts/character-navigation/a-console`，角色 a 使用 Human Console，内容 gentle，Gym record。首进程操作：

```text
:layout classic once
go market-home
go e-m2
:look
go e-k7
:quit
```

第一条 go 被拒绝，随后从家到市场再返回；同一连接回程使用不同的本端入口。第二进程仅 `:look`、`:quit`，核对续局知识；第三进程通过 `--gym-export ... --actor a` 导出人物 history。各进程退出码为 0，stderr 为空。原始 input/output、`verification.json` 与 `history-a.md` 保存在该 home。

这是代理脚本操作 Human Console 的工程验证，**真实 LLM 调用为 0**，不替代实际人类自由试玩。LLM 侧本片验证共享材料与控制器提示接线，真实命令使用保留到组合验收。

N1/N2 的知识边界与本片 N3 输出面有测试和运行证据；恢复/跟随也已覆盖本片新增状态。全量 N1–N12 仍需在求路与导航落地后复验。特别是 N11 的实际 Gym 分支探索/求路/导航后丢弃、N12 的真实 LLM 完整路线仍未执行，不能用旧根不可变测试替代。剩余工作见[实施计划](../design/character-navigation/implementation-plan.md)。
