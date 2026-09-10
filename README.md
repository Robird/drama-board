# DramaBoard

DramaBoard（戏剧棋盘）是一个面向 AI Player 的开放世界戏剧棋盘游戏与确定性 Simulation Kernel 探索项目。（repo 目录名 dream-board 是历史遗留旧名，正式名以 DramaBoard 为准。）

继续开发先读 [PROJECT-STATE.md](PROJECT-STATE.md)：最终目标、当前焦点、后续路线、待决问题及源码入口。协作和维护约定见 [AGENTS.md](AGENTS.md)；设计与历史材料按当前问题选读。

[文档索引](docs/README.md)按当前设计、实现证据、研究和历史分类；[历史索引](docs/archive/README.md)提供旧实验与交接材料的恢复入口。

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
