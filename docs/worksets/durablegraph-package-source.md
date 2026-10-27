# DurableGraph 固定包来源

> 历史验收说明：2026-10-27 后续开发决定已切换为[相邻源码引用](../source-dependencies.md)，下文固定包流程不再是当前构建入口。旧脚本保留复现来源，不会切换当前项目回包模式；精确旧消费者入口请使用提交历史。

本次历史验收通过真实 NuGet 包消费固定源码；[Prepare-DurableGraph.ps1](../../scripts/Prepare-DurableGraph.ps1)准备闭包，调用方随后执行 restore/build/test。新版验收及其边界见[独立化验收](../build-log/0027-spacetime-extraction.md)。

## 固定输入

| 输入 | 固定值 |
|---|---|
| DurableGraph | `c549124f1db53fe028f4640ddcafd8f338bb076b`，来源 `Atelia-org/durable-graph` |
| DG 包版本 G | `0.0.0-dramaboard.20261027.gc549124.1` |
| Storage | `70b406cd8d1117733eee81a33e7f978399aa98b2`，来源 `Atelia-org/atelia-storage` |
| Storage 包版本 S | `0.1.3-dev.20261019.g70b406c` |
| SDK | `10.0.201`，根 global.json 与 CI 一致 |

选择 `c549124` 是为了使用远端可取得的完整提交。2026-10-27 调查时，DG 本地最新 `dd645c1` 比远端多两次提交，差异仅为 Persistence 的 ListTags 和相关测试/文档；本仓生产核心消费的 DurableGraph、Serialization、Generator、Build 与 Storage pin 源码完全相同。本步不发布或推送上游。

包闭包沿用上游正式脚本：Storage 的 `eng/Pack.ps1` 先准备六包（Primitives、Data、Rbf、Binary、FrameStore、HeadStore），DG 的 `eng/Prepare-DurableGraphCandidate.ps1` 再导出四包（Serialization、DurableGraph、Storage、Persistence）及原始签名 `K4os.Compression.LZ4 1.3.8`，共十一包。Atelia 包的 nuspec 版本和源码提交须匹配固定输入；Generator 与 BuildTool 随 DurableGraph 包分发。

生产 Kernel/Spatial 仍只直接引用 `Atelia.DurableGraph`，Host 只引用 Kernel。测试项目为真实模型资格检查使用 Persistence；这不使生产库拥有 Repository 或磁盘 adapter。

## 准备与消费

从仓库根目录运行（PowerShell 7+、Git、精确 SDK）：

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1
```

默认源仓库为兄弟 `../durable-graph`、`../atelia-storage`，必须已有固定提交对象。脚本创建 detached worktree 到 `artifacts/durablegraph-integration/fixed-c549124/`，核对根路径、提交、origin 和干净状态；不 fetch、reset 或改变源仓当前工作树。CI 先 checkout 同一固定来源，再通过 `-DurableGraphSource` / `-StorageSource` 传入；不需要旧 atelia 仓库。

默认候选目录是 `artifacts/durablegraph-integration/core-candidate/`，消费其 `feed/`。根 [NuGet.Config](../../NuGet.Config) 清除继承源和 mapping，将 `Atelia.*` 与 K4os 精确包导向候选 feed，其他第三方依赖来自 nuget.org。消费缓存独立为 `artifacts/nuget-packages/`，MSBuild 显式使用该配置和缓存，避免旧用户缓存掩盖包升级。

生产者也使用隔离配置和缓存：固定 checkout 父目录生成仅 nuget.org 的 NuGet.Config，阻止消费者配置向上游泄漏；DG Candidate 再使用上游自己的 task 配置和缓存；从本仓根目录调用其绝对路径脚本，并现场核对 SDK，避免上游 global.json 的 latestFeature 选中更高版本。两上游各有 Directory.Build.props，阻断祖先 MSBuild 属性；本仓不添加向其泄漏的 Directory.Build.targets。

候选目录中的 `source.json` 记录 G/S、完整提交、SDK、包 identity 和 SHA256，另保留上游 `packages.sha256`。完整成功后再次执行脚本只校验并复用，不重新打包。缺包、改包、来源不符或已有不完整候选均拒绝覆盖。日志在 `fixed-c549124/logs/<G>/`。首次 DG 打包前写入 attempt 记录；失败产物保留，已尝试版本不静默再发。无完整 receipt 的首次准备还会拒绝消费者固定缓存已含同 G，防止换目录重打包后误用旧缓存。

需新尝试时使用未消费的新 G 和新的候选目录；同步 Directory.Build.props 与 NuGet.Config 的候选 feed，再准备。仅修改版本字符串不能改变脚本固定源码。不要清空全局缓存来掩盖同版本不同内容。

## 从旧工作树升级

旧生成器的 `obj/.../generated` 路径与新版不同。首次切换前须移走或清理六个活跃项目的旧 `obj`/`bin`，再重新还原与构建，避免残留 manifests 混入 Verify；不要删除已提交的 `DurableGraphSchemaHistory/*.dgschema`。新 checkout 无这一步。

`DurableGraphSchemaHistoryMode=Verify` 保持启用，不能用 Publish 或手改生成文件掩盖差异。真实生成登记、复杂图捕获与冷读由 [模型资格测试](../../tests/Spatial.Tests/Acceptance/DurableModelQualificationTests.cs)验证；模拟调度、E/S/pending 合同仍由 Kernel 自己维护，不交给上游自动重放。

旧九包来源 `4cea773` / `742fcd6`、旧接口迁移和 FirstBoard 恢复证据保留在拆分前 tag `archive/player-runtime-20261027` 及[历史反馈](../feedback/durablegraph/README.md)。恢复应用时使用[归档基线](../archive/player-runtime.md)自己的说明，不能把其冷恢复结论套用到当前核心。此次不迁移归档 adapter，不承诺旧实验存档兼容，也不新增引擎磁盘恢复产品能力。
