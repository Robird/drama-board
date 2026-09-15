# DramaBoard

DramaBoard（戏剧棋盘）是一个面向 AI Player 的开放世界戏剧棋盘游戏与确定性 Simulation Kernel 探索项目。（repo 目录名 dream-board 是历史遗留旧名，正式名以 DramaBoard 为准。）

继续开发先读 [PROJECT-STATE.md](PROJECT-STATE.md)：最终目标、当前焦点、后续路线、待决问题及源码入口。协作和维护约定见 [AGENTS.md](AGENTS.md)；设计与历史材料按当前问题选读。

[文档索引](docs/README.md)按当前设计、实现证据、研究和历史分类；[历史索引](docs/archive/README.md)提供旧实验与交接材料的恢复入口。

## 构建与持久化

需要 .NET 10、PowerShell 7，以及兄弟 `durable-graph` / `atelia` 源仓库中的固定提交。先准备真实 NuGet 包，再构建；脚本使用干净固定 checkout，不引用兄弟工作树的未提交修改。来源、可选路径与重复执行规则见[包准备说明](docs/worksets/durablegraph-package-source.md)。

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore
```

世界存档采用 DurableGraph 的独立 Event/State 历史：冷重开直接加载完整状态，若存在 pending Event，则只完成该事件。见[提交与恢复语义](docs/design/durablegraph-occurrence-persistence.md)。

## FirstBoard 真 LLM demo

`src/FirstBoard.Demo` 会让爱丽丝与鲍勃各由一个 `LlmPlayerDriver` 驱动，完整运行 FirstBoard，并在 `artifacts/wp15/` 生成世界事件叙事、内心独白/台词轨迹和逐 turn 记忆快照。

```powershell
# 复用本机 Codex CLI 的 ChatGPT 登录态
dotnet run --project src/FirstBoard.Demo -- --backend codex --model gpt-5.6-luna

# OpenAI-compatible；变量必须已进入当前进程环境
$env:DEEPSEEK_API_KEY = '<key>'
$env:DEEPSEEK_BASE_URL = '<base-url>'
dotnet run --project src/FirstBoard.Demo -- --backend deepseek --model deepseek-v4-flash
```

运行 `dotnet run --project src/FirstBoard.Demo -- --help` 查看输出目录、整体超时、单次请求超时和每角色 turn 预算等参数。凭据不会写入输出。

增加 `--world-store DIRECTORY` 创建持久世界；续局使用相同目录并增加 `--resume-world`，保持相同的 Human 主体配置，使用存档内的种子、场景和调度规则。例如：

```powershell
dotnet run --project src/FirstBoard.Demo -- --backend codex --model gpt-5.6-luna --world-store artifacts/my-world
dotnet run --project src/FirstBoard.Demo -- --backend codex --model gpt-5.6-luna --world-store artifacts/my-world --resume-world
```

当前恢复范围是**世界与待处理事件**。Player 记忆、LLM 会话和 turn 预算在本次运行重新建立，界面会提示；续局不要传 `--seed`。已完成的历史不会自动重播；旧 Journal 存档转换与可玩持久 fork 暂未提供。

当前规则为 `firstboard.duchess-letter/3`：途中交互提前到交会时间所在刻度，到达时间仍向上取整。旧 `/2` 世界存档不支持续局，请使用新目录开始；旧存档保留不变。见[接触时间与兼容边界](docs/worksets/passage-contact-floor.md)。
