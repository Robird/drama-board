# DB-071 命名空间重组试用

2026-09-12：DramaBoard 从 `30cafe0ed075281bf424e19d71ae00d845cfeac9` 适配上游 DB-071。未发现需要调整的 API 或持久化行为问题；正式依赖已固定到上游 `4cea773e3eac7a2df2d57e518f45e90459d53cf3`，默认包版本为 `0.0.0-dramaboard.20260912.4cea773.1`。

## 来源与适配范围

试用包为 `0.0.0-db071.20260912.1`，来自本机 `E:/repos/Atelia-org/durable-graph/obj/db071-new/feed`，同时消费四个 DG 包及五个 Atelia 包。上游当时 HEAD 为 `c7ee495`，DB-071 位于未提交工作树，不能把该 HEAD 当成新包源码 pin。交接入口为上游 `docs/design-branches/0071-dramaboard-adaptation-handoff.md`。

仅修改五个消费者文件：FirstBoard 的 Persistence 包引用，OccurrenceHistory、Metrics 和两份持久化测试的 using；`StateModelBinding` 增加 `.Runtime` 引用。模型、业务字段、SchemaId/版本、Generated 登记 facade 均无需改动。

## 本轮证据与复现

在 DramaBoard 根目录运行：

```powershell
$properties = @(
    '-p:DurableGraphPackageSource=E:/repos/Atelia-org/durable-graph/obj/db071-new/feed',
    '-p:DurableGraphPackageVersion=0.0.0-db071.20260912.1',
    '-p:DurableGraphSchemaHistoryMode=Verify'
)
dotnet build DramaBoard.slnx -v:q -warnaserror -m:1 -nr:false @properties
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet test DramaBoard.Local.slnx --no-build --no-restore -v:q -m:1 -nr:false @properties
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
```

- 新包 solution 构建零警告、零错误；11 个测试程序集共 527 项通过、零失败、零跳过，其中持久化 28 项包含 10 个冷进程案例。
- 64 份 accepted `.dgschema` 的文件集合和 SHA256 均不变。
- Process 消费者实际还原九个指定版本包；输出九个 DLL 的 SHA256 与试用 feed 中对应 nupkg 的 `lib/net10.0` 资产逐一相同。
- 先以默认旧包 `0.0.0-dramaboard.20260912.1c6083c.1` 构建并保留完整进程输出，再用旧程序创建 Continue/Reverse 的连续基线、S1 和 pending E2 存档。新程序跨进程打开 S1、续写到 S2，以及仅 fold pending E2；完整 World、Cursor、NextRequest、Pending 与旧程序连续运行一致，事件内容相同，pending 恢复零 Forecast/Plan。再次冷开无 replay，纯 S-head 读取前后存档字节不变。

跨包验证复用[归档进程见证](../../../archive/firstboard-llm/tests/FirstBoard.Persistence.Process/Program.cs)；常规冷恢复也属于归档 FirstBoard 测试。本机一次性旧消费者、存档、诊断 JSON、比较脚本和包哈希记录保留在忽略目录 `artifacts/db071-adaptation-20260912/`。本轮验证范围为 Windows 本地，未运行远端 CI 或 Linux。

## 正式来源固定

无需上游增加兼容 namespace 或修改领域 API。包准备脚本、默认版本、CI checkout 和[包来源说明](../../worksets/durablegraph-package-source.md)已一起固定到上述新提交；Atelia 仍固定 `742fcd62e691b6b6acca4113a3ac3638bc7275ba`。新包使用独立版本，保留原试用包及旧包，不覆盖 NuGet 缓存。

日常构建恢复使用 README 的默认流程，无需上述历史试用属性：`pwsh -File scripts/Prepare-DurableGraph.ps1`，随后执行默认 build/test。

正式固定包再次验证：九包首次准备与哈希校验复用通过；不带来源/版本覆盖的 `dotnet build DramaBoard.Local.slnx -v:q -warnaserror -m:1 -nr:false -p:DurableGraphSchemaHistoryMode=Verify` 零警告、零错误，随后 `dotnet test DramaBoard.Local.slnx --no-build --no-restore -v:q -m:1 -nr:false` 的 527 项全部通过。Process 实际还原九个正式版本包，64 份 history 路径与哈希不变；以新建旧包存档重跑上述 Continue/Reverse 跨包验证也通过，证据在 `artifacts/db071-pinned-20260912/`。文档本地链接与 `git diff --check` 通过。
