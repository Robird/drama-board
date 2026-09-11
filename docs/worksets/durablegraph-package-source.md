# DurableGraph 固定包来源

本批真实接入通过 NuGet 包消费固定源码。准备入口为 [Prepare-DurableGraph.ps1](../../scripts/Prepare-DurableGraph.ps1)，调用方负责随后执行消费者 restore/build/test；脚本本身只准备源码工作树和九个包。

| 来源 | 完整提交 |
|---|---|
| [Atelia-org/durable-graph](https://github.com/Atelia-org/durable-graph/tree/f68388f88ba09354e9fa90420dc2cf22b146b6cf) | `f68388f88ba09354e9fa90420dc2cf22b146b6cf` |
| [Atelia-org/atelia](https://github.com/Atelia-org/atelia/tree/742fcd62e691b6b6acca4113a3ac3638bc7275ba) | `742fcd62e691b6b6acca4113a3ac3638bc7275ba` |

默认版本为 `0.0.0-dramaboard.20260912.f68388f.1`，九个项目全部使用相同 `PackageVersion`。按固定上游 `experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1` 的顺序串行打包：Data、Primitives、Rbf、RbfSegmentStore、EventJournal、DurableGraph.StateStore.Serialization、DurableGraph、DurableGraph.StateStore.Storage、DurableGraph.StateStore。包 ID 均以 `Atelia.` 开头；Generator 与 Build 工具按上游 DurableGraph 包的既有规则随包分发，不另造消费者手工接线。

从 DramaBoard 根目录运行（PowerShell 7+、Git、.NET 10 SDK）：

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
```

默认源仓库为兄弟 `../durable-graph` 与 `../atelia`。缺少目标 checkout 时，脚本从源仓库执行 `git worktree add --detach <target> <完整 SHA>`，目标为忽略目录 `artifacts/durablegraph-integration/fixed/{durable-graph,atelia}`。这会登记源仓库的 worktree 元数据，不修改其当前工作树。源仓库必须已有固定提交对象；脚本不自行 fetch、reset 或删除目录。已有目标必须是准确的仓库根目录、HEAD 匹配且 tracked 文件干净，否则立即停止。源仓库的未提交开发不会被复制过去。

可显式指定源仓库、checkout 根与 feed；CI 也可以事先把固定源码 checkout 到同一根下的 `durable-graph` 和 `atelia` 两个目录，随后用同一脚本校验、打包，已有 checkout 不需要访问源仓库：

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1 `
    -DurableGraphSource E:/repos/Atelia-org/durable-graph `
    -AteliaSource E:/repos/Atelia-org/atelia `
    -CheckoutRoot artifacts/durablegraph-integration/fixed `
    -Feed artifacts/durablegraph-integration/feed
```

两个 checkout 必须保持上述兄弟目录关系，因为固定上游仍有相对 ProjectReference。每个 pack 都在所属仓库根目录运行，命令为 `dotnet pack <project> --configuration Release --output <feed> -p:PackageVersion=<version> -m:1`，退出码非零立即失败；日志在 `<feed>/logs/<version>/`。

嵌套 checkout 的 MSBuild 配置已核对：两个固定仓库都有自己的根 `Directory.Build.props`，默认向上搜索在这里终止，不会继承 DramaBoard 的 AssemblyName、TargetFramework 等设置；脚本也要求此文件存在。Atelia 有自己的 `Directory.Build.targets`；DurableGraph 没有该文件，而当前 DramaBoard 根也没有。若未来新增祖先 targets 或使用显式 `DirectoryBuildPropsPath`/`DirectoryBuildTargetsPath` 覆盖，需要重新核对导入边界。

完整成功后 `<feed>/source-<version>.json` 记录两库完整 SHA、版本和九个 nupkg 的 SHA256。再次调用会检查固定源码及所有包哈希，通过后复用，避免同版本重新 pack。已有包但无完整 manifest、缺包或哈希变化均拒绝覆盖；失败留下的部分产物也不会被删除。首次 pack 前还检查 `NUGET_PACKAGES`（未设置时为用户默认缓存）是否已有同版本；消费者另用 NuGet.Config 指定其他缓存时，调用方仍须保证版本没有被复用。

需要重新生成时使用未消费过的 freshVersion，并让全部消费者的 `DurableGraphPackageVersion` 同步，例如：

```powershell
$freshVersion = '0.0.0-dramaboard.20260912.f68388f.2'
pwsh -File scripts/Prepare-DurableGraph.ps1 -PackageVersion $freshVersion
# 后续 restore/build/test 同时传 -p:DurableGraphPackageVersion=$freshVersion。
```

不通过清理全局 NuGet 缓存掩盖同版本不同内容；默认版本是本批固定来源标识。更换源码提交时须显式修订脚本中的 pin、此文档以及消费者版本，不能只改变版本号便声称来自新源码。

本轮证据（2026-09-12）：在上述两个干净 checkout 上执行默认命令，九个包全部打包成功；再次执行校验并复用九个包成功。首个 Kernel 消费构建通过并发布七份 schema history。这些结果证明固定依赖与包接线可用，游戏保存、冷恢复及完整验收仍由真实接入计划推进，不能由本条推断已完成。
