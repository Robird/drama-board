# 角色目标管理首版工程验收

日期：2026-10-31。局部规则、游戏 E/S 接线、Console 与 Completion 材料已实现，工程及核心模型场景验证完成。范围按[里程碑](../design/goal-management/milestone.md)，行为合同见[操作](../design/goal-management/operations.md)及[容量](../design/goal-management/capacity-and-context.md)。本文记录工程证据；模型效果另见[真实试玩](goal-management-playtest.md)，代理操作 Human 入口的文本体验见[Console 记录](goal-management-console.md)。

## 交付与边界

- `ResourceCycle/Goals` 包含不可变 Goal、Link、Focus、敏感修订与纯规则，不依赖地图、植物或具体角色。角色私有 `CharacterInteractionState.Goals` 是当前权威，冻结 Observation 保存历史快照；控制器无私藏目标历史。
- 十二种固定命令接受严格 JSON。创建加父关联加焦点原子提交，身份单调且清理后不复用；共享关系为 DAG，不按文字合并、不自动完成父目的。焦点变化与活动取消是独立行为。
- 成功、show、no-op、拒绝均进入真实交互记录；前三者轮转服务，拒绝不轮转。目标操作不改变模型时间、库存、身体、随机流、活动截止或警告确认。没有为目标编辑伪造世界经历。
- Fact 保存决定后的目标后态、原操作身份和 typed receipt。Fold 用纯规则核对一致性，恢复不重新调用控制器。当前、Opening、逐项快照分别校验自己的历史边界，未来完成或修订序号不能混入旧快照。
- 全量材料只投影一次当前目标，保留全部未结束目录及复查线索、焦点详情、三条短回执和最近成功 show。查询后的真实目标变化使详情失效；无变化或拒绝不失效，改回旧值也不复活旧查询。
- survival/duo 启用能力，普通采集保持原行为。Completion 每次按角色实际能力选择固定规约，目标承载长期事项，handoff 仅保留一次性交接用途。

生产 `GameRevision` 为 **15**，旧开发存档不兼容。新增八个目标 DurableType v1，并提升 InteractionState/ContextItem 为 v2、Observation 为 v4、Fact 为 v12、Command/CommandKind 为 v3、EventKind 为 v10；Schema 工具发布了十五份新记录，再用 Verify 验证。没有手改生成文件或添加历史版本迁移框架。

## 自动化与实际运行证据

主线程统一执行，不把 subagent 源码检查计作测试通过：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Smoke-ResourceCycle.ps1 -OutputDirectory artifacts/goal-regression/batch
pwsh -NoProfile -File scripts/Smoke-ResourceCyclePlay.ps1 -OutputDirectory artifacts/goal-regression/play
pwsh -NoProfile -File scripts/Smoke-ResourceCycleSurvival.ps1 -OutputDirectory artifacts/goal-regression/survival
```

上述构建零警告/错误；全 solution **1,032** 项通过，三个既有真实进程 smoke 通过。随后只修整焦点回执文本并新增一例测试，再次 Schema Verify 构建及 CLI **314** 项测试通过（此前为 313）；最后为帮助补充状态适用范围，再次 Verify 构建及 CLI 314 项通过。其余项目未受这两处文本修改影响。日志保留在 `artifacts/goal-build-verify.log`、`goal-tests-final.log`、`goal-build-polish.log`、`goal-tests-polish.log`、`goal-build-help.log`、`goal-tests-help.log` 与 `goal-regression/`。独立 Console 新增 smoke 已完成八阶段，覆盖中文/emoji、查询、整理和冷开；发现的路径歧义已修复，真实 Console 复验见其报告。主体实现提交为 `42da30d`。

| 证据 | 验证范围 |
| --- | --- |
| [GoalRulesTests](../../tests/ResourceCycle.Tests/GoalRulesTests.cs) | 十二种操作，原子失败，同文独立身份，共享 DAG，暂停/结束回退，解绑有效后缀，三条敏感修订，12/32/48 容量与清理出口，Unicode/JSON 严格性，公开 command 构造无法绕过规则，时序边界 |
| [GoalIntegrationTests](../../tests/ResourceCycle.Tests/GoalIntegrationTests.cs) | 真实 Kernel 原始输入入口、角色私有隔离、轮转、拒绝、无物理效果，移动警告期间编辑、跨 Context 保留；篡改原输入/回执/身份/后态均被拒绝 |
| [GoalRecoveryTests](../../tests/ResourceCycle.Tests/GoalRecoveryTests.cs) | create+parent+focus、reopen、show、forget × E/S 四个中断边界；真实磁盘恢复及身份不复用；低层写入未来 marker 后应用冷开拒绝 |
| [GoalPresentationTests](../../tests/ResourceCycle.Cli.Tests/GoalPresentationTests.cs) | 两种布局、full/delta、历史前缀、一次投影、三回执、查询失效/续接、非 BMP 与控制字符、最大合法规模、S/E-only 恢复全文一致 |
| [LlmCharacterControllerTests](../../tests/ResourceCycle.Completion.Tests/LlmCharacterControllerTests.cs) | 同一控制器按每个角色的实际能力选择系统规约；目标路径不再要求重复 handoff，普通采集不获不存在的能力 |
| [Console smoke](../../scripts/Smoke-ResourceCycleGoals.ps1) | 真实输入/输出及操作日志配对，12 未结束、32 保留目标、48 关联分别触顶和整理后恢复，不只验证模型类 |

US-04～US-08 的暂停/续接、同文身份、更正与世界真相、恢复、目录增长由上述测试和 Console 流程支撑；US-01～US-03 的模型理解不能以这些确定性测试代替。

## 材料量测与修整

`Maximum_graph_longest_path_and_unicode_fit_host_budget_without_cutting_unfinished_items` 使用 32 个目标、48 条关联、32 节点 Focus、12 个未结束事项、最长字段与非 BMP/换行/控制字符、一次详细 show、三条回执及完整生存世界材料。最终 fixture：decision **61,230 UTF-16 code units / 37,386 scalars**，classic **61,619 / 37,775**，低于默认 200,000 UTF-16 输入门槛；这是指定压力形状，不是所有可能历史的数学上界，也不是 token 量测。最终详细日志在 `artifacts/goal-tests-help.log`，补充状态帮助前的量测另保存在 `goal-material-measurement.log`。

原 JSON 代理对转义的等价材料约 299,670 / 300,059 UTF-16，超过默认门槛。现仅恢复真正非 BMP 代理对，以保留文字可读性并避免六倍展开；字面量反斜线、控制字符和 Markdown 结构仍转义，有 roundtrip 反例。未静默裁剪目标、危险、交流或合法操作。

独立 Console 试玩发现原回执 `焦点：G1 → G2 → G1` 混淆旧路径和新路径；改用 `焦点：原 [G1 → G2]；现 [G1]`。帮助也直接列出 pause/resume/finish/reopen/focus 的适用状态，减少依靠拒绝才发现规则的成本。这两处修整不改变领域或 Schema。

## 实验解释与限制

6 个世界、7 个进程共完成 80 次真实模型调用，包含 26 次目标编辑、1 次目标查询及 1 次游戏拒绝。真实模型运行保留实际 SystemPrompt、完整角色材料、独立请求、原始操作、提交结果、模型配置及冷开过程；见[试玩记录](goal-management-playtest.md)。首场由模型自行建目标，真实 S 冷开后直接继续采水和赠与，双方获救；独立审阅核对了原始请求与提交事实，并指出复合目标在终局仍 Open。方法失效变体通过正常 Script 耗尽水源，模型保留同一交水目的、改等再生并分次兑现，随后自行 finish。角色记录的完成判断不是世界成功证据，赠与、抵达、死亡等仍需对应的已提交事实。少量场景通过不能证明一般规划可靠性。

避寒场景中，模型预先读懂风险，回暖后返回并完成原采菜目的；明确建议分记两件打算的补充局使用独立避寒目标并聚焦，完成后 show 原采菜目标并返回采集，没有显式恢复 Focus 指针。共享调查先保留了因“附近”范围含糊而扩大探索的尝试；限定当前地点资源后，模型通过同一子目标及两条来源保存可靠否定结论，切到另一父目标直接复用，并实际探索替代地点。后两类有明确自然语言引导，不是模型自发发现结构的证据；低温采集中断的严格活动不变量另由工程测试保证，不把预防性避险冒充该完整情节。

目标数量与字段有界不代表整份 Context 或磁盘历史有全局上界；成功 Items 仍保留。超出宿主总材料门槛时明确暂停，历史裁剪/实体召回/自动提醒按实际失败重开。原有生存二期“实际用户控制 a，与真实 LLM b 合作通关”验收独立保留，代理运行不能替代。
