# 属性阈值预测验收

> 历史命名说明：本文保留当时的 ResourceCycle / Cycle 类型、命令与验证记录。当前应用已重命名为 Playground；现行入口与持久身份边界见[重命名说明](../../docs/implementation/playground-rename.md)。旧名称源码链接固定到重命名前的 Git 版本。

日期：2026-10-29。对应[设计](../design/attribute-threshold-forecasts.md)。工程验证与四次独立实际 Console 会话通过；未接入 LLM。

## 实现与工程证据

| 合同 | 实现与证据 |
| --- | --- |
| 属性独立的警戒／耗尽时刻，数值预测不等于通知 | [CycleAttributeForecast](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleAttributeForecast.cs)、[CycleSurvivalRules](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleSurvivalRules.cs)；[SurvivalForecastTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/SurvivalForecastTests.cs) 覆盖三属性、跨昼夜、上限、另一属性先耗尽、补给、已通知／确认、正零速率和时间表示边界。 |
| 行动只补充改变条件的例外 | `ActionWarning` 仅对从庇护出发的移动给出途中暴露及段内温暖时刻；同测试文件覆盖段内外目标、已有暴露和不读取未知目的地。 |
| 保存材料贯穿语义比较与历史恢复 | [CycleObservationEquality](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CycleObservationEquality.cs)、[CharacterContext](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle/CharacterContext.cs)；[RateExplanationContextTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/RateExplanationContextTests.cs) 检查同值仅预测不同与实际磁盘历史预测破坏拒绝。 |
| S／pending E 恢复不重新决定，旧 Opening 保持原值 | [CharacterContextPersistenceTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Tests/CharacterContextPersistenceTests.cs) 在既有多来源 fixture 上加入水分补给前后逐项预测断言；中断 E→S、冷开、无控制器恢复、再次冷开均保留冻结材料。 |
| 两布局与增量共享属性材料，删除空风险行 | [CycleAttributeView](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/CycleAttributeView.cs)、[CycleViewSnapshot](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/src/ResourceCycle.Cli/CycleViewSnapshot.cs)；[AttributeForecastViewTests](https://github.com/Robird/drama-board/blob/8401bf0e781007a403d3382e4e5e3fa76fd18cc2/tests/ResourceCycle.Cli.Tests/AttributeForecastViewTests.cs) 覆盖预测单独改变、旧／新 Context、低值与历史通知、终局及行动例外撤销，不锁定具体呈现位置。 |

生产 GameRevision 为 **11**；`SurvivalObservation` Schema 3 删除独立 `FatalAt` 字段并保存三项预测及阈值，`AttributeForecast` Schema 1。`FatalAt` 仅派生；物理 `Survival` 仍为 Schema 2。没有旧开发存档兼容，试玩须使用新的 `--store`。

本轮实际运行：

- `dotnet test tests/ResourceCycle.Tests/ResourceCycle.Tests.csproj -c Release --no-restore -m:1 -p:DurableGraphSchemaHistoryMode=Verify`：311 通过，0 失败／跳过。日志 `artifacts/threshold-domain-tests.log`。
- `dotnet test tests/ResourceCycle.Cli.Tests/ResourceCycle.Cli.Tests.csproj -c Release --no-restore -p:DurableGraphSchemaHistoryMode=Verify`：267 通过，0 失败／跳过。日志 `artifacts/attribute-forecast-cli-tests.log`。
- 主线程 `dotnet build DramaBoard.slnx -c Release --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify`：0 警告／错误。日志 `artifacts/threshold-solution-build.log`。

独立只读审查检查了算法、保存／比较／历史校验、Schema 和 UI 职责；主线程复核关键源码及实际日志，并落实了当前值与预测时刻一致性校验、无时刻文案范围、delta 不重复共同假设。未扩大到无关 Kernel／Spatial／Inventory 全测试运行。

## 实际 Console 试玩

独立试玩者直接运行最终 Release DLL，以 UTF-8 stdin/stdout 驱动实际交互，没有调用内部 API 替代游戏流程。四个进程退出码均为 0，stderr 均为空。原始输入、输出、合并实录、退出码和报告保存在 `artifacts/threshold-playtest/`，作为本机证据，不属于 Git 中的永久测试数据。

| 会话 | 实际观察 |
| --- | --- |
| `main` | 15:00 营地的食物 30 对应 16:00 警戒／21:00 耗尽；饮水 45 对应 17:30／20:37:30；温暖 60 计入昼夜后对应 22:25／次日 00:30。切换 classic、full/delta、单次预览后值一致。喝水至 90 后只更新饮水到 23:07:30／次日 02:15，以及真实库存／可执行性变化，没有等待或采集风险复读。随后到达 20:00 林屋。 |
| `cold-preview` | 冷开林屋，温暖 44、庇护回暖 +12，没有未来警戒／耗尽时刻。layout/mode/look 预览前后的存档文件 SHA-256 完全一致（排除宿主 `operations.jsonl`），见 `preview-file-check.json`。 |
| `leave-shelter` | 20:00 离开林屋的三小时行程有失去庇护提示，以及温暖预计 21:35 降至 25；23:40 耗尽在抵达以后，不作为本段风险展示。21:00 途中实际寒风使温暖从 32 降至 22，新预测为 22:50 耗尽，明确已处警戒区和历史警告待确认。 |
| `night-cold` | 再次冷开 21:00 行程中的存档，保留实际寒风经历、温暖 22、夜间速率 −12 以及 22:50 耗尽预测；没有把原来的预测冒充已发生事件。 |

主线程另行核对原始实录、各会话输入、哈希证据和退出结果；没有发现必须再次修改源码的问题。实际试玩覆盖正常 S 冷开；pending E 恢复、人工极端速率与时间上界由自动测试覆盖。没有验证 Windows Terminal 颜色／窄屏视觉效果、真实 LLM 使用或多人场景的新体验。
