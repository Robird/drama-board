# 地图与逐站导航试玩

本机已准备独立 home：`E:/repos/Atelia-org/drama-board/artifacts/navigation-play`。这是新的 gentle 双角色世界，未执行普通游戏行动；a 由你通过 Human Console 控制，b 使用本机 Responses 连接 `http://127.0.0.1:8491/responses`，模型 `deepseek-v4.1-flash`、low reasoning、无 API key。每次运行最多 32 次模型调用，默认关闭 lookahead。配置在该 home 内，可直接修改。

## 进入与续局

```powershell
Set-Location E:/repos/Atelia-org/drama-board
dotnet run --project src/Playground.Cli -c Release --no-build -- --home artifacts/navigation-play
```

`:quit` 或 Ctrl+C 暂停；下次运行相同命令继续。模型调用预算用完也只是暂停。想比较预取的主观等待体验，在相同命令末尾加 `--lookahead on`；明确关闭用 `--lookahead off`。预取共用调用预算，可能因输入变化而废弃，不保证每次更快。

不使用模型时，两位角色都改由同一 Console 轮流控制：

```powershell
dotnet run --project src/Playground.Cli -c Release --no-build -- --home artifacts/navigation-play --character-controls artifacts/navigation-play/all-human.json
```

这只改变运行时控制配置，不修改角色的领域状态。准备 home 时只执行了 `:look` / `:quit`，没有消费真实模型调用。

## 可以尝试的事情

先 `map` 看自己的知识，再用眼前的 `go <入口ID>` 探索。入口去向未确认时，只有走到另一端才知道连接；去过两地并不自动知道捷径。地图会保存在人物存档里。

探索两个地点之后，用 `path home` 规划返家。复制实际回执中的路径 ID，执行 `navigate <路径ID>`；到中间点会停下来，可以观察、说话、补给或再次求路。新的候选不会改掉正在执行的路线；`continue` 才走下一段。`cancel` 清除剩余计划；若因提醒在途中醒来，当前路段仍会走完，需要 `continue` 确认提醒。

`path <目标> from <起点>` 可以从已知异地做规划，但启动要求本人真的在起点。`path <当前位置ID>` 是零段路线，启动只给完成回执，不走动。无需记住历史 ID：当前候选和活动路线都在界面中。

`say <角色ID> <正文>` 只对同地角色说话；可用 `shout <正文>` 向声音连接的一跳范围大喊。主动 `follow` 后由领队导航带队；跟随者在自己抵达时学习连接，不提前得到领队地图。

## 反馈与人物经历

```text
:feedback 描述刚才不符合预期或难理解的体验
```

反馈保存在 home 的 `store/feedback.jsonl`，附带决定附近的提交定位。退出游戏后可以导出人物经历，输出需使用新的文件名：

```powershell
dotnet run --project src/Playground.Cli -c Release --no-build -- --home artifacts/navigation-play --gym-export artifacts/navigation-play/history-a.md --actor a
```

b 的模型经历用 `--actor b` 和另一个输出文件名。统一宿主记录在 `store/host/events.jsonl`，模型原始调用在 `store/llm-runs/`；这些诊断文件不构成角色地图知识来源。

## 在别的机器创建同样的试玩入口

先按仓库 README 构建；使用新的 home，保留现有存档：

```powershell
dotnet run --project src/Playground.Cli -c Release --no-build -- init --home artifacts/my-navigation
Copy-Item config/llm-connections.example.json artifacts/my-navigation/llm-connections.json
Copy-Item config/playground-character-controls.example.json artifacts/my-navigation/character-controls.json
```

在连接文件中设置实际可用的默认连接；控制文件只保留 a 的 `human-console` override，b 由默认连接控制。给 `playground.json` 加 `"characterControls": "character-controls.json"`、`"maxCalls": 32`、`"gym": "record"`。然后首次运行带 `--characters 2 --content gentle`；后续只传 home 即可。此流程不依赖本机忽略目录中的实验文件。

实际证据和已知体验边界见[组合验收](../../experiments/character-navigation-d-acceptance.md)。代理脚本和模型任务演练不等于你的自由试玩体验。
