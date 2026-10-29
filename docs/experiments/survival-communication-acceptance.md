# 生存 2C：交流与赠与工程验收、模型预演

状态：领域、Context、Console 和持久化已实施；真实模型交流预演已运行。**尚未完成实际人类与模型的完整合作逃离，不能据此宣布二期完成。** 目标见[里程碑 §3.4](../design/survival-demo-milestone.md#34-2c交流赠与与合作试玩)，规则见[交流合同](../implementation/survival-communication-plan.md)。

## 实施与验证范围

- `say` / `give` 使用同地活跃对象；说话是署名陈述，不自动共享地图。赠与在单个事实中完成双端库存、额度和经历。
- 每人每模型时刻 4 次成功发送；确认、换 Context、冷开不退款。接收唤醒但不取消活动，明确确认后保留原期限。说话、赠物和补给不默默确认已有身体警告。
- 复用 Context / UndeliveredExperiences，两种布局和 full/delta 使用同源观察；参数域不展开人物×物品×数量的组合。原 `--play` 不启用通信。
- GameRevision 14；新增或提升的 11 份 Schema 历史记录经 Publish 生成、Verify 检查。旧开发存档不兼容。

`dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 无警告、无错误。全 solution 测试 **956 通过，0 失败，0 跳过**：Host 2、Inventory 36、Kernel 131、Spatial 91、ResourceCycle 367、Completion 28、CLI 301。证据为本地 `artifacts/communication-verify-build.log`、`communication-final-tests.log`。随后仅调整模型提示，再次 Verify 构建和 28 项 Completion 测试通过，见 `communication-prompt-build.log`、`communication-prompt-tests.log`。

关键测试不是仅检查新字段：

| 边界 | 当前证据 |
| --- | --- |
| 一次增加多份库存、整批拒绝、整数与总容量上限 | [ItemInventoryTests](../../tests/Inventory.Tests/ItemInventoryTests.cs) |
| 活动期间说话/赠水、回复/use/continue 保持期限；已有身体警告共存 | [CommunicationRulesTests](../../tests/ResourceCycle.Tests/CommunicationRulesTests.cs) |
| 已确认低水警告并开始等待后，同刻首次赠水可唤醒，结果从死亡变为存活 | 同一规则测试中的救命水对照 |
| 双方最多 8 次成功交流后仍能放行时间；跨 Context 不退款、下一时刻纯查询恢复额度 | 同一规则测试中的实际 Kernel 序列，每步比较增量预测与全量 Rebuild |
| 第四次说话/赠与 E 写成但 S 前中断，禁止再 Plan/调用控制器；再冷开后双方库存及经历不重复 | [CommunicationRecoveryTests](../../tests/ResourceCycle.Tests/CommunicationRecoveryTests.cs)，逐提交对照无中断轨迹及预测 oracle |
| Open 收件人追加 Item、Closed 收件人正常唤醒，署名/effect 校验与私有地图/交接隔离 | [CommunicationContextTests](../../tests/ResourceCycle.Tests/CommunicationContextTests.cs)及恢复测试 |
| 文本内部空白与 2000/2001 个 Unicode 字符边界、非法数量形成持久拒绝 | [CommunicationInputTests](../../tests/ResourceCycle.Tests/CommunicationInputTests.cs) |
| 通信保持合法自定义身体速率来源 | [CommunicationRateTests](../../tests/ResourceCycle.Tests/CommunicationRateTests.cs) |
| 两种布局、help、差分、空域、额度和带控制字符的对白 | [CommunicationViewTests](../../tests/ResourceCycle.Cli.Tests/CommunicationViewTests.cs) |

首次测试的三处 UI 失败已处理：一处旧 continue 文案期望；两处是文化相关字符串比较把 ESC 当作可忽略字符，实际渲染字面量没有原始 ESC。改为 Ordinal 安全断言，保留恶意标题与控制字符检查。独立源码审查另发现通信会重建身体速率来源，已修为只投影数值并保留来源，回归覆盖说话及赠与。

三个旧模式 smoke 使用现有 Release 产物、未改脚本通过；已逐一读取 `artifacts/communication-regression/1793290664835/{batch,play,survival}/acceptance.json`。它们证明旧批处理、采集、单人生存双出口/失败、天气及冷恢复回归，不代替本片合作验收。

## Console 与真实模型预演

UTF-8 管道驱动 `--duo` 的中文对白、双向赠物、full/delta 切换和两人等待，正常退出，证据在 `artifacts/communication-console/`。物品返还仅用于核对两端显示，不声称有合作策略价值。

第一轮真实模型使用 `OpenAICodexResponsesClient`、`gpt-6.1-sol` 和 `--duo --llm`；a 的 Human 入口由 root coding agent 输入，**不是用户本人**。10 次模型调用、36 次提交，无游戏拒绝或模型协议失败，在 D0 17:00 用 `:exit` 保存暂停，没有通关。

- a 请求饮水并提出分工，b 实际执行 `give a item.water 1`。
- a 再次说明自己采两份食物、给 b 一份，b 执行 `gather water 3`。
- b 后来仍自行启动采浆果；a 送去已采得的一份食物。b 的原采集周期 16:45 → 17:15 保持，收礼唤醒后立即 `use item.food`，随后饮水、`continue`，没有重置采集。
- 这证明模型可消费署名交流、改变即时行动并使用赠物；尚不证明稳定的长期分工或共同成功。

可读原始实录保存在 [survival-communication-preflight.txt](survival-communication-preflight.txt)（只规范换行为 LF；SHA-256 `6049A98CB38C3EBEF3E821E379DE6AC8A5E059CF37F8CAFE1C3D2FFB2001CA24`）。完整本地审计为 `artifacts/communication-live/run.jsonl`，SHA-256 `0DE847FB1AE88358691C2CF02E08CF2DC8EE41E54A1CC0C4474BB92D6C2F354F`；存档与操作日志位于同目录 `game/`。审计不进入角色知识。

## 预演发现与后续验收

第一轮发现合作约定跨 Context 遗失。独立核对模型输入确认：调用 3 和 5 都完整收到分工信息；调用 4 和 6 的新 Context 只带旧 handoff，没有那两句约定。属于模型未更新交接，未发现消息漏送。旧提示“已有草稿就直接行动”过强，现改为：采纳的新通信改变分工或承诺时，若草稿尚未覆盖，先更新一次 handoff，记录承诺、下一步及待核实事项；无新增信息则不重复改写。没有增加第二套历史或自动把角色话语变为真相。

提示修订后的第二轮预演已完整逃离，同样由 root 操作 a、真实模型操作 b。独立审计核对了 15 次模型调用、双方各 15 次成功操作、61 次连续提交，未见游戏拒绝或协议错误：

- 调用 2 把细化分工写入 handoff，下一 Context 的调用 5、6 可见；调用 5 继续保留“自己采水、等待阿青赠食”，传到调用 7–11，未再转去自行采食物。
- a 采浆果 2 份并赠食物 1 份；b 采水 4 份并赠饮水 2 份，另在出发前应请求赠水 1 份。双方分别采集、兑现约定、使用对方补给，然后同行。
- 共 5 次 handoff，同回合更新对应新分工或已兑现的赠与，没有观察到无意义反复改写。调用 10 更新下一段同行计划并传到新 Context。
- 两人均在 D0 22:00 抵达山脚公路，结束时食物 30、水分 79、温暖 10，汇总 AllEscaped。使用不存在的 auth 文件重开该终局存档仍正常显示结果，没有调用模型。

第二轮[完整可读实录](survival-communication-cooperative-preflight.txt)仅规范为 LF，SHA-256 `4D7F463952A935B8F0D60598584D8C672DD55EAF78A3CBDDFCDC805921F1DE73`。原审计 `artifacts/communication-handoff-probe/run.jsonl` 的 SHA-256 为 `9E0137ECE3BE8D56E0EB15672AF1D91419209A6BAD5694F1785F7C4B647D84BB`，同目录保存 game、reopen 日志与无错误的 stderr。两个样本的交互时机也不同，不能把改善单独归因于提示，更不能由一局成功证明长期可靠性。

**剩余验收是实际人类与模型合作通关及体验反馈。** 真人试玩可以直接运行：

```powershell
dotnet run --project src/ResourceCycle.Cli -c Release -- --duo --llm `
  --model gpt-6.1-sol --auth-file W:/CodexHome/auth.json `
  --store artifacts/human-cooperation --max-calls 128
```

使用新目录；a 为人类，b 为模型。`say b ...` 可商量行动，`give b item.water 1` 可赠物；`:exit` 暂停，续局使用同一命令与存档。需要实际记录由谁控制 a、交流/赠物如何改变选择、双方最终结局及遇到的困惑。测试通过、模型预演或宿主代输入都不豁免这项要求。
