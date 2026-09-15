# DurableGraph 接入前准备批次

状态：已交付，2026-09-11。项目当前焦点统一维护在 [PROJECT-STATE](../../PROJECT-STATE.md)。

## 问题与范围

让 DramaBoard 在 Codex MCP 尚未就绪时继续前进：移除已放弃实验的默认维护面，建立清晰的文档导航和可复现验证入口，并准备有源码依据的消费者试验提案。
本批不修改 Kernel/Spatial 的业务语义，不切换生产持久化，不实现分叉/倒带，不修改 DurableGraph 或 Atelia，不开发 MCP 服务。

## 工作包与验收

| 包 | 交付 | 验收 |
|---|---|---|
| A：旧实验归档 | StateJournalNative 和 0021/0022 退出主线，保留精简经验与恢复索引 | 固定历史引用可恢复全部原文；默认编译不再依赖 StateJournal；EventJournal 回归保留 |
| B：文档整理 | 活跃主题采用稳定名称，历史资料有明确入口，修复迁移引用 | 索引区分设计/实现证据/提案/历史；变更链接可解析；历史正文不伪装为新裁决 |
| C：依赖与验证 | 统一 AteliaRepositoryRoot 默认值，保留显式覆盖与 CI 固定版本 | 正确解析兄弟仓库；使用 CI 固定依赖串行构建并运行完整 Local solution |
| D：消费者前置研究 | 最小真实场景、模型候选、API 摩擦与验收提案 | 基于当前两库源码；区分确证缺口、待实验事项和未裁决需求；无生产接线 |

历史恢复点：本地附注标签 `research/pre-durablegraph-20260911`，指向 `bd73e64aa78bf225abd62a897d27e41f33469872`；该提交也可独立作为恢复引用。

## 集成纪律

- 主线程负责范围、文件所有权、项目状态、集成检查和提交；子代理交付独立文件包。
- 文档迁移映射在写入前协调；测试 csproj 的归档改动和依赖改动串行进行。
- 由一个验证代理串行执行 .NET 构建/测试，避免共享输出目录竞争；不移动兄弟仓库的工作分支。
- 交付后独立复核默认依赖、历史可恢复性、文档引用和研究结论；具体结果在本文件收敛记录，不追加逐轮日志。

## 验证与后续

- A：移除 9 个 StateJournalNative 源文件、两份实验日志、专用禁并行设置及 StateJournal 项目引用；保留 EventJournal 现有回归。[归档索引](../archive/statejournal-native.md)提供经验、固定依赖和恢复命令。
- B：6 个活跃主题与 4 份历史正文分类改名；`.ignore` 仅排除 `docs/archive/legacy/`，两个[归档入口](../archive/README.md)仍可检索。见[文档索引](../README.md)。
- C：统一默认依赖路径并验证默认值、命令行覆盖和 CI 环境变量覆盖。使用 Atelia `742fcd62e691b6b6acca4113a3ac3638bc7275ba` 的独立 checkout，未修改兄弟库工作树。
- D：[消费者前置研究](../research/durablegraph-consumer-preflight.md)经独立源码复核；产物是待裁决提案，未接入生产持久化。下一包可先提取真实 encounter 场景与完整恢复边界对照。

完整 `DramaBoard.Local.slnx` 构建 0 警告、0 错误；12 个测试项目共 **478/478 通过，0 失败、0 跳过**，含 FirstBoard.Persistence 10 项、Journal.Atelia 14 项。
主线程独立汇总 TRX 并核对依赖提交；本次本地日志与 TRX 在忽略目录 `artifacts/validation-20260911/`，结果不依赖这些临时文件才能复现。

使用已 checkout 到上述 CI 固定版本的 Atelia 目录，可从仓库根串行复现：

```powershell
$ateliaCheckout = 'E:/path/to/atelia-at-ci-revision' # 替换为固定版本 checkout 的绝对路径
dotnet build DramaBoard.Local.slnx -warnaserror -m:1 "-p:AteliaRepositoryRoot=$ateliaCheckout"
dotnet test DramaBoard.Local.slnx --no-build --no-restore -m:1 "-p:AteliaRepositoryRoot=$ateliaCheckout"
```

独立复核未发现归档/依赖或消费者研究的阻碍问题；研究建议补充了完整持久闭包核对要求。文档复核发现的历史检索命令缺 pattern 已修正。
主线程最终检查 46 份 Markdown 的 183 个本地链接与 4 个源码行号锚点，全部有效；默认 rg 排除 4 份 legacy 正文，显式查询仍可枚举，归档索引保持可见。集成 diff 的空白检查通过。
长期 roadmap 与后续裁决只在 PROJECT-STATE 更新，本批记录不再作为滚动任务表。
