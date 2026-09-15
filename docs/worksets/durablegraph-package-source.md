# DurableGraph 固定包来源

本批真实接入通过 NuGet 包消费固定源码。准备入口为 [Prepare-DurableGraph.ps1](../../scripts/Prepare-DurableGraph.ps1)，调用方负责随后执行消费者 restore/build/test；脚本本身只准备源码工作树和九个包。

| 来源 | 完整提交 |
|---|---|
| [Robird/durable-graph](https://github.com/Robird/durable-graph/tree/4cea773e3eac7a2df2d57e518f45e90459d53cf3) | `4cea773e3eac7a2df2d57e518f45e90459d53cf3` |
| [Atelia-org/atelia](https://github.com/Atelia-org/atelia/tree/742fcd62e691b6b6acca4113a3ac3638bc7275ba) | `742fcd62e691b6b6acca4113a3ac3638bc7275ba` |

默认版本为 `0.0.0-dramaboard.20260912.4cea773.1`，九个项目全部使用相同 `PackageVersion`。按固定上游 `experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1` 的顺序串行打包：Data、Primitives、Rbf、RbfSegmentStore、EventJournal、DurableGraph.Serialization、DurableGraph、DurableGraph.Storage、DurableGraph.Persistence。包 ID 均以 `Atelia.` 开头；Generator 与 Build 工具按上游 DurableGraph 包的既有规则随包分发，不另造消费者手工接线。

从 DramaBoard 根目录运行（PowerShell 7+、Git、.NET 10 SDK）：

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
```

默认源仓库为兄弟 `../durable-graph` 与 `../atelia`。缺少目标 checkout 时，脚本从源仓库执行 `git worktree add --detach <target> <完整 SHA>`，目标为忽略目录 `artifacts/durablegraph-integration/fixed-4cea773/{durable-graph,atelia}`。这会登记源仓库的 worktree 元数据，不修改其当前工作树。源仓库必须已有固定提交对象；脚本不自行 fetch、reset 或删除目录。已有目标必须是准确的仓库根目录、HEAD 匹配且 tracked 文件干净，否则立即停止。源仓库的未提交开发不会被复制过去。

可显式指定源仓库、checkout 根与 feed；CI 也可以事先把固定源码 checkout 到同一根下的 `durable-graph` 和 `atelia` 两个目录，随后用同一脚本校验、打包，已有 checkout 不需要访问源仓库：

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1 `
    -DurableGraphSource E:/repos/Atelia-org/durable-graph `
    -AteliaSource E:/repos/Atelia-org/atelia `
    -CheckoutRoot artifacts/durablegraph-integration/fixed-4cea773 `
    -Feed artifacts/durablegraph-integration/feed
```

两个 checkout 必须保持上述兄弟目录关系，因为固定上游仍有相对 ProjectReference。每个 pack 都在所属仓库根目录运行，命令为 `dotnet pack <project> --configuration Release --output <feed> -p:PackageVersion=<version> -m:1`，退出码非零立即失败；日志在 `<feed>/logs/<version>/`。

嵌套 checkout 的 MSBuild 配置已核对：两个固定仓库都有自己的根 `Directory.Build.props`，默认向上搜索在这里终止，不会继承 DramaBoard 的 AssemblyName、TargetFramework 等设置；脚本也要求此文件存在。Atelia 有自己的 `Directory.Build.targets`；DurableGraph 没有该文件，而当前 DramaBoard 根也没有。若未来新增祖先 targets 或使用显式 `DirectoryBuildPropsPath`/`DirectoryBuildTargetsPath` 覆盖，需要重新核对导入边界。

完整成功后 `<feed>/source-<version>.json` 记录两库完整 SHA、版本和九个 nupkg 的 SHA256。再次调用会检查固定源码及所有包哈希，通过后复用，避免同版本重新 pack。已有包但无完整 manifest、缺包或哈希变化均拒绝覆盖；失败留下的部分产物也不会被删除。首次 pack 前还检查 `NUGET_PACKAGES`（未设置时为用户默认缓存）是否已有同版本；消费者另用 NuGet.Config 指定其他缓存时，调用方仍须保证版本没有被复用。

需要重新生成时使用未消费过的 freshVersion，并让全部消费者的 `DurableGraphPackageVersion` 同步，例如：

```powershell
$freshVersion = '0.0.0-dramaboard.20260912.4cea773.2'
pwsh -File scripts/Prepare-DurableGraph.ps1 -PackageVersion $freshVersion
# 后续 restore/build/test 同时传 -p:DurableGraphPackageVersion=$freshVersion。
```

不通过清理全局 NuGet 缓存掩盖同版本不同内容；默认版本是本批固定来源标识。更换源码提交时须显式修订脚本中的 pin、此文档以及消费者版本，不能只改变版本号便声称来自新源码。

## DB-068 接口迁移证据

2026-09-12：从 `f68388f` 包迁移至 `1c6083c` 包（当时版本 `0.0.0-dramaboard.20260912.1c6083c.1`），Kernel / Spatial 与归档 FirstBoard 的 21 处框架基类声明改为 `IDurableObject`。保留普通 class、字段及版本、构造、相等与集合快照行为；本次未转换为 record，也不是业务 Schema 升版。

九个包首次准备和再次校验复用均通过。全 solution 执行 Clean 后，以 `-warnaserror -p:DurableGraphSchemaHistoryMode=Verify` 构建，零警告、零错误；64 份已提交 `.dgschema` 的路径与 SHA256 均未改变。Windows 下 `dotnet test DramaBoard.Local.slnx --no-build --no-restore -m:1 -nr:false` 的 11 个测试程序集共 515 项通过、零失败、零跳过；本轮未运行远端 CI 或 Linux。

另用真实旧包程序写出存档，新包程序跨进程恢复和续写，未通过诊断 JSON 重建世界。这是[归档 process witness](../../archive/firstboard-llm/tests/FirstBoard.Persistence.Process/Program.cs)的历史消费者证据，旧版为 DramaBoard `0155b30` / 包 `0.0.0-dramaboard.20260912.f68388f.1`。Continue、Reverse 分别验证：

- 旧程序 `create <save> <response> 1` 写 S1；新程序 `open <save> <response> 0` 精确恢复且不改存档字节，再 `open ... 1` 续写到 S2。
- 旧程序 `pending <save> <response> 1` 留下 E2；新程序 `open ... 0` 只 fold pending facts、零 Forecast/Plan，发布 S2。
- 两条路径的完整 World、Cursor、NextRequest、Pending 与旧程序连续 `create ... 2` 的结果一致；续写事件与 pending 事件内容一致。再次冷开均无 replay，存档文件 SHA256 不变。

上述 CLI 形式和冷进程回归均属于归档 FirstBoard 消费者；复现时从[归档索引](../archive/firstboard-llm.md)恢复其完整上下文。当前一次性日志、两代输入及比较脚本保留在忽略目录 `artifacts/idurableobject-migration-20260912-035105/`。
