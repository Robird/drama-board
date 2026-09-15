# 0025 实施验收记录

状态：A1—A8 验收通过；2026-09-16，Windows 实跑。下文工作树描述保留施工交回时点，后续提交以 Git 历史为准。

## 本轮基线

- 2026-09-16，Windows，HEAD `9030de517bb0e349ed5a448840de541b59f32545`。
- 开始时既有修改：`PROJECT-STATE.md`、`docs/README.md`，以及未跟踪的 `0025-server-webui-first-movement.md`。保留其设计内容；仅按本任务更新动态状态和实施结果。
- `Prepare-DurableGraph.ps1` 成功复用九个固定包，未更改包版本。
- 新增工程前，`dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 通过，零警告/错误；Kernel 7、Spatial 24 项 schema history 验证通过。
- 随后串行 `dotnet test DramaBoard.slnx --no-build --no-restore -m:1`：255 通过，0 失败，0 跳过。
- 本机 Node `v24.11.1`、npm `11.6.2`。

## 验收映射

| 条款 | 实现入口 | 必需证据 | 状态 |
| --- | --- | --- | --- |
| A1 | 两份 solution、Server.csproj、前端锁文件 | 应用引用守卫、核心 diff 审计、npm ci | 通过 |
| A2 | FreePlay 场景与 Kernel 会话 | A→B→D 四次提交、1000/4000 ms | 会话测试与 Chromium 通过 |
| A3 | 异步 driver、Submit、HTTP | 等待/非法/并发重复测试与刷新 | 会话/HTTP 与浏览器通过 |
| A4 | 会话生命周期与错误状态 | 旧运行、取消、故障测试 | 通过；浏览器关闭重开保留同一 pending |
| A5 | Player 材料与页面 | 服务端轨迹、刷新、SVG/出口 Chromium 验证 | 通过 |
| A6 | Dev 投影与独立页面 | HTTP 信息边界、浏览器请求记录 | 通过；Player 没有请求诊断 API |
| A7 | 发布脚本、静态文件、E2E | 非仓库 CWD 运行真实 apphost 与两页截图 | 通过；未知 API 返回 404 |
| A8 | README、PROJECT-STATE、本文 | 最终串行 build/test、文档与 diff 审查 | 通过；既有设计修改保留 |

## 最终验证与交回结果

- 整库严格 Rebuild 通过；随后串行 `dotnet test DramaBoard.slnx --no-build --no-restore -m:1 --logger trx --results-directory artifacts/0025/dotnet`，268 通过，0 失败/跳过；原七核心 255 加 Server 13。TRX 在 `artifacts/0025/dotnet/`。
- `FreePlaySessionTests` 验证真实提交、冻结历史、等待/非法/并发、旧运行/停止、移动后故障保留最后完成前缀，以及 injected driver 的未知出口/额外字段拒绝。`HttpBoundaryTests` 验证严格 JSON、并发最多一个 202、旧运行 409、故障 503 与 Player/Dev 信息边界。
- Server 独立只读审查未发现阻塞缺陷；该审查本身不代替实际测试。
- `git diff --exit-code HEAD -- src/Kernel src/Spatial src/Protocol src/Player src/Player.Agency src/Host src/Decision.Validation Directory.Build.props` 通过：核心合同、schema 与固定包配置保持原样。
- 前端 `npm ci` 与 TypeScript/Vite build 通过；`pwsh -File scripts/Publish-Server.ps1` 完整执行并发布 exe/资源。初次浏览器安装因与 npm ci 竞争中断，发布结束后串行重试 `npx playwright install chromium` 成功。
- `dotnet build DramaBoard.Local.slnx --no-restore -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 通过，零警告/错误。
- `npm --prefix src/WebUI run test:e2e`：1 项 Chromium 端到端用例通过（6.5 秒用例时间）；实际发布 apphost 从 `F:\TEMP\dramaboard-e2e-BbUi6x` 启动，测试自行停止进程并移除临时工作目录。未遗留 Server 进程。
- 本轮只实跑 Windows。CI 已配置 Ubuntu/Windows 的 Node、前端、.NET、发布及 Chromium 步骤；未触发远端 CI，不声称 Linux 已通过。

## 可带回设计会话的结果包

1. **工作树**：HEAD 仍为上述基线，未提交。新增 Server、WebUI、Server.Tests、发布脚本与本验收记录；修改两份 solution、CI、ignore、README、PROJECT-STATE 和文档导航。既有 0025 设计正文保留，仅更新状态与证据链接。
2. **启动**：仓库中执行 `pwsh -File scripts/Prepare-DurableGraph.ps1`、`pwsh -File scripts/Publish-Server.ps1`、`& ./artifacts/server/DramaBoard.Server.exe`。地址为 `http://127.0.0.1:5080/player` 与 `http://127.0.0.1:5080/dev`。完整命令见[根 README](../../README.md)。
3. **证据**：Server 测试位于 [tests/Server.Tests](../../tests/Server.Tests)，浏览器用例位于 [movement.spec.ts](../../src/WebUI/e2e/movement.spec.ts)。本机生成证据在 `artifacts/0025/`：`dotnet/*.trx`、`movement-evidence.json`、`server.log`、`playwright-report/index.html`。产物和证据被 Git 忽略，可通过上述命令重建。
4. **截图与例子**：[Player](../../artifacts/0025/player.png)、[诊断](../../artifacts/0025/dev.png)。A(0) → B(1000) → D(4000)，历史为 Started(0)、Arrived(1000)、Started(1000)、Arrived(4000)，共四次 Kernel 提交；刷新及关闭重开仍在 D，保留 A/B/D 轨迹与同次运行身份。
5. **实施细化**：应用退出使用额外 `stopped` 状态并返回 503；诊断只列最近 100 条记录，而自身轨迹保留完整运行历史。均未扩展领域合同。无待裁决的阻塞问题；下一机制由设计会话选择。本批未实现 LLM、持久化或任何新增交互机制。
