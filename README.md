# DramaBoard

DramaBoard（戏剧棋盘）是一个面向 AI Player 的开放世界戏剧棋盘游戏与确定性 Simulation Kernel 探索项目。（repo 目录名 dream-board 是历史遗留旧名，正式名以 DramaBoard 为准。）
设计文档阅读顺序：[Design Note 001](docs/Design%20Note%20001：戏剧棋盘——一种面向%20AI%20Player%20的角色化沙盒游戏.md) → [整体软件架构与技术栈](docs/开放世界棋盘游戏设计_002_整体软件架构与技术栈.md) → [Simulation Kernel](docs/开放世界棋盘游戏设计_003_Forecast_Elapse_Decide_SimulationKernel.md)。
研发文档接着阅读：[架构基线与决策记录](docs/研发计划_001_架构基线与决策记录.md) → [工作包分解](docs/研发计划_002_工作包分解.md)。

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
