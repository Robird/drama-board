# 小镇 Human Console

一个 Console 依次服务三个居民，世界在需要行动时暂停。当前是工程调试入口；LLM、Gym 反馈/导出命令及居民对话交易仍在后续切片。

```powershell
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town --hours 48
# 冷开同一个存档；不要再传 --hours
dotnet run --project src/Microtown.Cli -c Release -- --store artifacts/my-town
```

无 `--store` 时仅保存在内存。新档 `--hours` 为 1..720，默认 48；存档已有实验终点，冷开不能覆盖。`--max-operations` 默认 200，统计本次进程所有居民的提交操作，包含拒绝；预算用尽暂停，下次启动可继续。`:look` 重显当前材料，`:quit` 或 EOF 暂停，不产生一次游戏操作。多个分支时必须用 `--branch <ID>` 明确选择；当前分叉管理由 `TownDurableHistory` API 提供，尚无 Console fork 命令。

每次交付的材料包括角色姓名/ID、时间、本人生活/库存/目标、最近回执、附近居民、规则与当前合法动作。成功的即时命令轮转服务其他空闲居民，拒绝留在同一角色；耗时行动开始后需等完成才能再次决策。输入示例：

```text
buy grain 4
go market-workshop
produce meal 2
eat
go market-workshop
sell meal 1
wait 4
```

这些是命令形状示例，并非多角色可直接逐行执行的完整脚本；看清当前服务的角色与地点。每条通路可双向使用，`go market-workshop` 在工坊表示返回市场。

回执与最近本人经历属于游戏状态，冷开继续显示；pending E 先由 Kernel 恢复，不重复询问。末尾 `[Host statistics]` 是开发者全知统计，与角色材料分开；其时间区间是新游戏开始至当前状态（包括 fork 继承的前缀），不是本次进程运行区间。资金、物品与饱食分开列出，失败居民仍计入最差生活值。

构建后可运行实际多进程 Console 探针：

```powershell
pwsh -NoProfile -File scripts/Smoke-Microtown.ps1
```

探针保留原始输入/输出及 acceptance.json，覆盖拒绝、预算暂停、冷开、三居民生产消费、市场更新、有限终点和结束后再开；它是脚本驱动 Human 入口，不是实际模型试玩。
