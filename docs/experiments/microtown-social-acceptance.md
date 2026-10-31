# 小镇交流、赠与与忙碌响应验收

日期：2026-10-31。此片提供同地居民交互、有界个人记录与生产/等待中的响应窗口；报价交易、Town owner 预测维护和完整经济日真实模型试玩仍待后续，不将本片当成整个小镇目标完成。

## 交付

- `say <resident> <text>`：双方存活、同地且非自身；保存双方可见原话，最多 512 个 Unicode scalar。非法 UTF-16、空白、超长或收件容量不足均拒绝，不产生部分记录。
- `give <resident> <item|money> <count>`：一次事实完成双方扣入与记录，物品每次 1..1000、钱使用正 long；内部转移不计外部流量。消息稳定 ID 为发送者及操作序号，两侧分别分配本地记录序号。
- `TownInteractionState`：最多旧记录 16＋本次 opening 新记录 32＋待交付 32。opening 发布交付高水位，纯渲染不消费，冷开/拒绝重试保持窗口；已纳入材料不等于模型或人实际读过。消息满时拒绝用户发送，系统活动结果仍走独立本人经历。
- 忙碌通知不取消活动；即时操作轮转服务且保留响应资格。`continue` 保持活动 ID、原结束时间和在制品；`cancel` 结束等待或一次退回生产原料，无成品、无饥饿返还。移动中仍无响应/取消入口。
- 所有入库及生产启动检查未来完工与退料两个互斥结果的空间；不累加两份预留，但两种未来必须都能结算。根校验防止落入必然溢出的已安排活动。
- 冻结观察只含本人历史；其他人库存、资金、饱食和收件容量不公开。赠与候选仅按本人能力显示，明确接收方仍在执行时验证，不通过数量二分推断他人私有状态。
- 行动材料增加基于本人现有饱食/衰减速率的路线、剩余旅途、当前活动及多批生产预览；标明不进食/无干预假设、同刻失败优先及超实验终点风险。居民原话做 Markdown/控制字符转义并标为言语，不作为游戏规则。

实现：[交互记录](../../src/Microtown/TownInteractionState.cs)、[结算](../../src/Microtown/TownPlanning.cs)、[调度](../../src/Microtown/TownEngine.cs)、[观察](../../src/Microtown/TownObservation.cs)、[文本](../../src/Microtown/TownTextRenderer.cs)。新规则保留在游戏层，现有通用控制器/Gym 不需要理解赠与规则。

## 持久化与版本

Character Schema 升至 2，新增 InteractionState/Entry/Kind；Schema 工具 Publish 生成四个历史文件，再通过 Verify。Town S/E 的 GameRevision 升至 2，根/事件的 Schema 形状未改变。当前不提供旧开发档升级器。

实际尝试打开上一片真实模型存档，发现 DG 子对象还原先因缺少升级能力拒绝，尚未到应用 revision 检查。CLI 原先没有捕获 `InvalidDataException`，本片补正常错误退出。复测返回 1，明确说明数据/Schema 不支持与新建存档路径；对旧 store 全文件哈希检查不变。证据：`artifacts/microtown-social/old-revision-refusal.txt` / `old-revision-checks.json`。分支元数据或原 Gym 日志仍可只读查看，这不表示旧世界可继续运行。

## 工程验收

主线程实际运行：

```powershell
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Publish -v:q
dotnet test DramaBoard.slnx -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
dotnet build src/Microtown.Cli/Microtown.Cli.csproj -c Release -m:1 -p:DurableGraphSchemaHistoryMode=Verify -v:q
```

全量 **1233** 项通过，13 个测试项目无跳过；Town 领域 86 项。最后一次 CLI Verify 构建也包含旧档错误呈现修正，零警告错误。输出见 `artifacts/microtown-social/tests.txt`。

| 新增测试 | 证明范围 |
| --- | --- |
| `TownInteractionStateTests`，8 项 | 不可变列表、32 条完整新窗口、80 条上界、仅 opening 裁剪、严格 Unicode、坏游标/条目拒绝 |
| `TownSocialAcceptanceTests`，14 项 | 真实 Kernel 的原子守恒、long 钱、拒绝无通知、多未读与 busy 即时/continue、地点约束、库存容量和未来输出/退料极值 |
| `TownSocialAcceptancePersistenceTests`，5 项 | 磁盘 E-only 赠与/取消只恢复一次；取消后再次取消不重复退料；冷开交付材料；精确 S fork 不混入父未来消息 |
| `TownSocialObservationTests`，6 项 | 接收方隐私不影响 offer、双方私有材料冻结、文本转义、忙碌提示、裁剪计数、生产/移动/等待饱食假设预览 |

首轮控制字符断言采用文化比较，将 ESC 当可忽略字符而误报命中位置 0；修为 Ordinal 后通过，没有放宽生产转义要求。

## 体验验证边界

独立试玩使用实际产品 Human Console 与 Gym，原始脚本和材料位于 `artifacts/microtown-social/`；不调用模型，不代表真人合作体验或模型自主社交。13 条输入全部成功，没有修订或重跑脚本，实际角色顺序为 `a,b,c,a,b,a,b,a,b,a,b,a,a`。

a/b 采购并移动到工坊；hour 0.25，a 开始生产、截止 hour 1.25，b 发言唤醒 a。a 的 continue 保持原截止时刻；b 赠一份 grain 再次唤醒，a cancel 后为 grain 3/meal 1（含退回的 2 grain，无成品）。随后再次生产，在 hour 1.25 完成，a 为 grain 1/meal 3。hour 4，c 等待结束，EOF 暂停。

最终现金 46 = 初始 54 − 进口支出 8，grain 2 = 进口 4 − 配方投入 2，meal 5 = 初始 3 + 产出 2。取消的批次没有计作生产或消耗。冷开 `:look/:quit` 前后完成 S 都为 `c33f94ba0e00006c0f000000`；a/b 的记录保留，c 的观察及角色导出不含这段私下发言。主线程检查原始回执、库存/统计和冷开材料，未只采用试玩者的成功结论。

详细报告为 `artifacts/microtown-social/REPORT.md`；`evidence/` 保存入口/程序集哈希、全部 Console 输出、Gym 原始日志、分角色导出及逐操作 E/S 核对。pending 恢复、极值容量与 fork 由上表测试覆盖，不冒充此产品脚本的行为。此次未覆盖市场日更或真实模型社交。

后续先实现带有效期的居民报价/接受/拒绝/撤回，并继续复用当前响应/材料机制；报价的必达终态不能被用户消息容量阻塞。当前能力和剩余工作统一见[动态计划](../codex-goals/microtown/work-plan.md)。
