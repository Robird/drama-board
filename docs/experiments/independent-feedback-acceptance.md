# 独立宿主反馈实施验收

日期：2026-11-02。结论：独立反馈切片已实施，Windows 工程测试、真实宿主 Completion fixture 与脚本操作实际 CLI 均通过。合同见[独立反馈设计](../design/independent-feedback.md)，用法见[Gym 与反馈操作说明](../design/character-gym/usage.md)。本轮没有在线 LLM 调用；CLI 由代理脚本驱动，不是真人自由试玩；未在 Linux 验证。

## 工程与证据

使用根 global.json 的 Windows .NET SDK 10.0.201，执行：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
```

最终构建 0 warning、0 error，含 Schema Verify。原始材料保留于本地 `artifacts/feedback-acceptance/`，不提交 artifacts：`build-final.log` 为最终构建，`tests/` 为全 solution 初跑 TRX，`tests-final/` 为最终受影响项目复跑，另有对应文本日志。

初跑九个项目通过，Playground.Completion 的一条 fixture 将 9 个 UTF-16 code unit 的正文误作满足 8 的上限；将 fixture 改为 7 字符多行文本后通过，没有放宽校验。尾部警告、ephemeral 查询等补充之后，最终复跑 CLI 496/496、Playground.Completion 51/51。各项目最新结果合计 1441 通过、0 跳过；这是全 solution 初跑加受影响项目最终复跑的汇总，未宣称最终再次完整跑过所有项目。

| 测试项目 | 最新通过数 |
| --- | ---: |
| CharacterControl.Completion | 13 |
| CharacterGym | 31 |
| Goals | 31 |
| Host | 2 |
| Inventory | 36 |
| Kernel | 131 |
| Playground.Cli | 496 |
| Playground.Completion | 51 |
| Playground | 548 |
| Spatial | 102 |

真实宿主加 Completion fixture 验证默认 Gym off 下的 `feedback({text}) → act`、收据 ID 进入局部 Tail、反馈不改变世界状态与私有材料隔离等。fixture 经过真实宿主与工具循环，但不构成远端模型、认证或网络成功证据。工程覆盖还包括正文边界、写入失败退役与不确定结果、只读尾部处理、完整 Boundary 核验及请求分叉精确前缀。

## 实际 CLI 串联

执行可重复入口：

```powershell
pwsh -NoProfile -File scripts/Smoke-Feedback.ps1 -OutputDirectory artifacts/feedback-acceptance/cli
```

`cli/manifest.json` 的 Status 为 passed，Processes 实际为 10；各进程的输入、stdout、stderr 与子分支导出均留在同目录。使用 CLI 二进制 SHA-256：

```text
FDF2A1CE35D89BE512E9482CE061E93E33F1781F958B853BCECC5964A3A0BF2E
```

实际流程覆盖 Human 反馈、只读查询前后源哈希不变、冷开后同一决定前 S 的第二份反馈具有不同 RunId、无 Gym 的独立反馈分叉、分支列表、父分支后续 handoff 加 Gym、子分支反馈加 Gym、子分支导出，以及 ephemeral 反馈和显式 ephemeral 查询。子分支导出声明祖先轨迹缺口，不包含父分支后来的 `parent-future-secret`，导出前后源文件哈希不变。

这证明实际进程能够执行保存、查询、冷开定位与分叉闭环，不评价真人使用体验或模型自发反馈质量。未修改领域、Kernel、Spatial、Schema、GameRevision 或上游仓库；其他 JSONL 去重继续延期，历史验收报告未改写。
