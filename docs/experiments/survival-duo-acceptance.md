# 生存 2B：双角色规则、观察与宿主验收

日期：2026-10-29。对应[里程碑 §3.3](../design/survival-demo-milestone.md#33-2b双角色生存与控制器路由)和[实施合同](../implementation/survival-duo.md)。本片不包含对白、赠与或真实人类合作，2C 仍待完成。

## 工程与恢复

Schema Verify Release 构建零警告、零错误（`artifacts/duo-final-build.log`）。当前全部七个套件合计 902 项通过：Host 2、Inventory 27、Kernel 131、Spatial 91、Completion 28、ResourceCycle 330、CLI 293。首次全套日志 `artifacts/duo-tests.log` 保留初始失败；修复后重编并跑领域与 CLI 全套，分别见 `artifacts/duo-domain-tests.log`、`artifacts/duo-cli-tests.log`。最终只润色宿主总结，再运行 CLI 全套见 `artifacts/duo-final-cli-tests.log`；其余套件对应行为未再变化。

| 合同 | 当前证据 |
| --- | --- |
| 一人获救，另一人继续跨昼夜、从不同出口获救 | `DuoSurvivalRulesTests.Escaped_character_is_frozen_while_partner_continues_and_escapes_through_another_exit`；前者 Character/Terminal 引用保持，停止其需求和预测 |
| 途中死亡后不再幽灵到达 | `DuoSurvivalRulesTests.Death_retires_a_traversal_and_partner_passes_its_former_arrival_without_ghost_arrival`；空间移除、历史位置保留、他人越过原期限 |
| 一次天气，不同暴露；同时死亡；连续耗尽先于天气 | `DuoSurvivalRulesTests` 的天气与同刻参数化用例；全员结束不再抽样，部分结束只结算剩余者 |
| 全部/部分/无人获救 | 双出口、部分结局与同刻双亡用例；全员结束后 Kernel exhausted |
| 个人终局与天气 E-only | `DuoRecoveryTests` 四个参数化场景；真实磁盘发布 E 后故障、冷开、禁止 Plan/控制器恢复、重复 Recover 无效果，再次 S 冷开并继续 |
| 预测与恢复一致 | 上述每步对比全量 Rebuild 和 Spatial Contacts；恢复前后比较 Context 材料及完整轨迹 |
| 不泄漏远方结局、handoff、地图 | `DuoRecoveryTests`、`MulticharacterContextTests`；幸存者仍 Exploring，自己的经历只含自己的 ObserverId，结束者观察被冻结 |
| Human/LLM 精确路由 | `DuoHostTests.Mixed_host_routes_only_b_to_model_and_preserves_private_contexts_and_operation_ownership`；LLM 请求不含 Human 私有交接，操作结果按角色归属 |
| 宿主恢复与身份边界 | `DuoHostTests` 错误入口在 pending 恢复前拒绝且文件字节不变；LLM 已结束或 Human 首次 EOF 不初始化认证；双终局冷开不再请求输入 |
| 两种布局的同地人物 | `DuoHostTests` 验证默认决策布局可见姓名/标识/公开活动，私有背包与技能不可见，离开后不显示远处人物 |

独立只读审阅核对了个人结束与空间移除同事实发布、天气参与者恰为前态活跃集合，以及任意终局来源都移除三类角色预测。根据审阅补了同刻连续死亡与天气排序用例，并使 `CycleEnvironmentRules.Observe` 的直接公共调用也返回结束者保存的天气。

初次集成发现并修复的主要问题：默认决策布局遗漏同地人物；旧终局文案错误声称整个世界停止；测试迁移误改了一处非致命警告断言；新冷开测试忘记释放自己的存档句柄；两个新夹具分别用了错误庇护速率、错把警告下一步当成 Context Opening。后两项修正夹具/时序，没有放宽生产效果归属校验。

## 实际运行与边界

真实混合入口已完成一局，完整可见实录见 [Console 文本](survival-duo-console.txt)。Human 端由 coding agent 输入，b 使用真实 Codex Responses `gpt-6.1-sol`；这是引导式接线试玩，不是盲测、外部人类试玩或 2C 合作验收。

```powershell
dotnet src/ResourceCycle.Cli/bin/Release/net10.0/DramaBoard.ResourceCycle.Cli.dll --duo --llm `
  --model gpt-6.1-sol --auth-file W:/CodexHome/auth.json --store artifacts/duo-live/game `
  --max-calls 64 --llm-log artifacts/duo-live/run.jsonl
```

两人先后沿溪水到达溪流湾，同时采浆果、食用和饮水，再分别采水、穿过狭谷到公路。Human 角色两次因同伴加入采集提前返回；`continue` 保留原活动与周期，数量分别完成。模型自行选择 13 次操作：4 次 handoff、3 次移动、2 次采集、3 次消费、1 次 continue；没有游戏拒绝或协议纠错。

| 角色 | 控制来源 | 个人结束 | 食物 / 饮水 / 温暖 |
| --- | --- | --- | --- |
| b 阿白 | 真实模型 | D0 21:45 山脚公路获救 | 31.25 / 81 / 13 |
| a 阿青 | coding agent 操作 Human 输入端 | D0 22:00 山脚公路获救 | 30 / 34 / 10 |

b 结束后没有新模型调用；a 的行程继续，最后才出现宿主 `AllEscaped` 总结。独立审阅将 13 次模型操作和 12 次 Human 输入与操作日志逐一核对，59 次世界提交版本连续，全部 25 个操作成功；两人的私有 handoff 未进入对方角色材料。同采两次唤醒后，浆果原期限 16:30、溪水首份原期限 16:45 均未改变，第二份于 17:00 正常产出。此次两人使用同一出口；不同出口由规则测试和原单人生存回归覆盖，不能把此局描述为独立双路线合作。

本地 JSONL 保存完整模型输入、返回和提交；SHA256 为 `333B563A7E6F4DA10ADD81332EB8558146D6D8EE29867360C0E87994815D05EF`。提交的 Console 文本仅统一换行为 LF，未保留 opaque 响应块或凭据。首次 Human 中文 handoff 经工具 PTY 输入变成替换字符，实录保留；后续使用 ASCII 交接文字。另用 UTF-8 PowerShell 管道输入 `handoff 中文输入校验`，新存档 receipt/结果均正确保留中文（`artifacts/duo-live/unicode-probe`），因此本轮未将此工具终端限制误作游戏状态迁移故障。

通关后用不存在的 auth 路径重新打开实际存档，仍正常重绘两人终局和总结，版本保持 59，没有 binding/model-input/model-output（`artifacts/duo-live/reopen.jsonl`）。最后把宿主总结的枚举名改成“全部获救/部分获救/无人获救”，并显示个人结束时刻；原试玩实录保留润色前的 `AllEscaped` 文案，审计中的结构化结果名不变。

旧批处理、采集和生存三个 Console smoke 均通过，未修改脚本；工件根 `artifacts/duo-regression/1793288663353` 下 `batch`、`play`、`survival` 各有 `acceptance.json`。它们覆盖原玩法、两个出口、失败路线、天气、途中期限及冷恢复，不代替独立试玩。GameRevision 为 13；Character 6、Expedition 3、Environment 2、Fact 10，新增 CharacterEnding 1、WeatherEffect 1。没有旧开发存档兼容要求。

## 下一片

2C 首先确定有界交流机会与放行规则，再实施同地零时对白/赠与、感知与原活动并存、赠与恢复。随后进行真实 Human＋LLM 完整合作逃离，并记录互动实际改变了哪些行动。不能把本片两个角色各自逃离的结果算作合作完成。
