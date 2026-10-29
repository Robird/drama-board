# 生存二期 2A：LLM 接线验收

日期：2026-10-29。范围为[目标 §3.2](../design/survival-demo-milestone.md#32-2a单角色-llm-接线)，运行与恢复合同见[实施说明](../implementation/survival-llm-controller.md)。本片没有双角色生存、对话或赠与，不作为整个二期完成证据。

## 真实模型试玩

使用相邻 Completion `b91e0aad7b14a6ac7c9cf3d1929355b018ceb954` 的 `OpenAICodexResponsesClient`，实际调用描述为 `chatgpt.com / openai-codex-responses-v2 / gpt-6.1-sol`。游戏默认种子与数值未改，模型只收到当前角色的完整保留 Context，没有源码、隐藏地图、预制攻略或控制器私藏历史。提示词仅说明行动、补给和 handoff 用法。

| 旅程 | 实际结果 | 证据 |
| --- | --- | --- |
| 首局，1 次调用预算后停止，再从同存档开新进程 | 合计 9 次调用；D0 22:30 公路获救，食物 27.5、饮水 30、温暖 4；实际采集、即时补给、经历寒风及途中警告 | [首段原始 Console](survival-llm-first-console.txt)、[续局原始 Console](survival-llm-resume-console.txt) |
| 明确 handoff 使用说明后的独立新局 | 10 次调用，其中 3 次 handoff；D0 21:30 公路获救，食物 32.5、饮水 38、温暖 16 | [完整原始 Console](survival-llm-handoff-console.txt) |

首局动作依次为：`go trail-1`；重开后 `gather berries 1`、`use item.food`、`use item.water`、`gather water 2`、`gather berries 1`、`go trail-3`、`go trail-4`、`continue`。首进程提交版本 1–5，重开从 6 连续提交至 30，没有重做出发。独立审阅核对 9 个操作身份与提交一一对应；所有输入仅为独立 Observation、空 Tail，没有原生 assistant/tool 续接。调用限额到达后，已接受的移动仍按规则完成，模型在下一服务机会前暂停。

首局没有使用 handoff，因此不能单独满足这项验收。调整只在系统说明中明确“首次形成计划时记下、已有草稿则行动”，第二局未加入路线事实或额外资源。交接链条逐项核验如下：

| 模型调用 | 提交与后续输入 |
| --- | --- |
| 1：沿溪声寻找补给与人类线索的初始计划 | 恰一次 HandoffEdited；调用 2 看到当前草稿，调用 3、4 看到上回合交接 |
| 3：溪流湾先补给再探索的计划 | 恰一次 HandoffEdited；调用 4 看到草稿，调用 5–7 看到跨回合交接 |
| 8：过桥追寻车声并留意寒风的计划 | 恰一次 HandoffEdited；调用 9 看到草稿，调用 10 在途中告警时看到交接 |

每次笔记之后下一次调用都执行实际行动；吃食物、喝水和确认警告没有机械追加笔记。两局均无协议错误或游戏拒绝。主线程与独立 reviewer 均检查了实际输入、操作及终局，不以模型自称成功为据。

这些是两次模型自主单角色成功，采用同一出口；不宣称覆盖不同路线、真实 Human＋LLM 合作、原生模型会话恢复或大规模 Context 压力。首局输入长度为 1758–3391 UTF-16 代码单元。按时间顺序呈现的 Opening 和操作后更新仍会保留历史值，本次模型正确使用更新；未来若长回合出现混淆再比较呈现策略。

完整模型 JSONL（含响应原生块）与存档保留在忽略目录 `artifacts/llm-phase2a/`；以上 Console 文件为可移植的原始可见输入输出，仅将换行规范为 LF 并移除末尾空行，未包含认证 token 或 opaque reasoning。JSONL SHA256：

| 文件 | SHA256 |
| --- | --- |
| `live-first.jsonl` | `093D215028A83C1067FCF627657101DAADFE91568723D25B14473F48D22BA58A` |
| `live-resume.jsonl` | `C507B7033768B2556CF2CD0EF752059326BB82CE1A848068329640F6F288B5A0` |
| `live-handoff.jsonl` | `B978936699AFD6F265F2EFD1246E05A206F08BE8DB8A9BB6CF510382A94A6E8C` |

## 自动化与恢复边界

Release solution 构建及 Schema Verify 零警告/错误；本片未修改领域 Schema 或 GameRevision。源码依赖 DG `dd645c1`、Storage `70b406c`、Completion `b91e0aa`。

受影响和原有测试共 880 项通过、无跳过：Host 2、Inventory 27、Kernel 131、ResourceCycle 316、CLI 285、Completion adapter 28、Spatial 91。先运行全 solution，修正新增 SSE 夹具后复跑 adapter；宿主审计尾修后复跑完整 CLI suite。最后系统说明调整以第二局真实模型运行验证。命令和日志在 `artifacts/llm-build.log`、`llm-tests.log`、`llm-adapter-tests.log`、`llm-cli-tests.log` 与 `llm-final-build.log`，全量初跑日志如实保留夹具失败。

- [适配测试](../../tests/ResourceCycle.Completion.Tests/LlmCharacterControllerTests.cs)：Completed/exact-one、调用 ID 原样保留、零/多/错误工具、畸形/重复/额外参数、取消、预算、超大材料、游戏拒绝进入后续独立请求。
- [HTTP/SSE 测试](../../tests/ResourceCycle.Completion.Tests/ResponsesWireTests.cs)：真正 public Responses 客户端投影 JSON 工具并解析 SSE；完整调用但缺 terminal 不交游戏。原夹具只提供终止快照，未提供普通 function call 的 `output_item.done`，修正夹具后通过；没有为夹具削弱生产校验。该夹具不测试 Codex 认证。
- [领域中断测试](../../tests/ResourceCycle.Tests/LlmControllerRecoveryBoundaryTests.cs)：完整响应后 E 前中断允许改选；E-only 不询问控制器且效果恰一次；S 后展示前中断保留材料；拒绝反馈冷开仍可继续。使用真实磁盘和计数 fake controller，不能冒充端点中断实测。
- [宿主测试](../../tests/ResourceCycle.Cli.Tests/LlmHostTests.cs)：预算暂停与重开、模型完整响应后展示故障不发布 E、E/S 恢复先于新请求、操作收据修复、取消、连接选择及工具 schema 的完整审计。只把 Step 提交后结果称为 committed。

实际模型证明 S-only 续局。E-only 与 E 前故障通过以上确定性夹具验证，本次没有刻意在真实网络响应期间杀进程。连接错误探针使用不存在的认证路径，退出 1 并安全报告 `AuthStorageUnavailable`，不打印凭据。

原 batch、Human 多人采集、生存 smoke 均通过。后两脚本仍沿用旧 UI 与“非法命令不写盘”假设，本次已按现有持久 Context 合同修正：纯 meta/EOF 继续严格比较 E/S 字节；非法游戏操作须出现配对且已提交的 Rejected 收据，并保持最新可见物理状态、警告和活动期限不变。隐藏目标探针只比较本次新增操作结果，不让旧 Context 拒绝污染断言。原争采/取消/昼夜、双出口、消费、天气、途中警告、脱水失败及冷开对照保留。主线程检查了脚本 diff、最终观察和验收工件：

- batch：`artifacts/llm-phase2a/batch-smoke/`；
- 采集：`artifacts/resource-cycle-play-smoke/55169020afe64089b8e1938df6bc3210/acceptance.json`；
- 生存：`artifacts/resource-cycle-survival-smoke/009ccb593fa54eb08594cb4f37c744a6/acceptance.json`。

## 审阅收敛与后续

分工为适配器、CLI 宿主、独立恢复/审阅三个工作包，主线程统合依赖、实际运行及验证。审阅发现工具 schema 的默认多态 JSON 序列化会遗漏参数结构，已改为显式投影并加断言；终端同时去除原生 opaque 块与重复 JSON 输入，保留可读模型材料和提交结果。

2B 首先需要形成个人终局与多角色天气的共享合同。尤其途中死亡不能留下已过期的 Spatial 到达预测；退休活动实体、保留结束时历史位置是一项待在该片裁决的实现方向。随后再做控制器路由与 2C 的有限交流机会、原子赠与和真实合作，不把本片通关当作后二片的替代品。
