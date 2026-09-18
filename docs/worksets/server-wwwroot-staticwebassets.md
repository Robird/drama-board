# Server wwwroot 与 .NET 10 静态资源清单故障调查

> 层级：调查记录。日期：2026-09-19。状态：已修复（[Server.csproj](../../src/Server/Server.csproj) 的 `EnsureWwwrootDirectory` 目标）；本地全部测试恢复绿色。

## 症状

自 [77f9df7]（加入本地 Server 与 WebUI）起，CI 的 `dotnet test` 阶段在多个提交上持续失败：`tests/Server.Tests` 的 4 个 `HttpBoundaryTests` 用例在 `WebApplicationFactory.CreateClient` 阶段抛出：

```text
System.IO.DirectoryNotFoundException : <repo>\src\Server\wwwroot\
  at Microsoft.Extensions.FileProviders.PhysicalFileProvider..ctor(String root, ...)
  at Microsoft.AspNetCore.StaticWebAssets.ManifestStaticWebAssetFileProvider..ctor(...)
  at Microsoft.AspNetCore.Hosting.StaticWebAssets.StaticWebAssetsLoader.UseStaticWebAssetsCore(...)
  at WebApplication.CreateBuilder(...)  -- Program.cs:5
```

其余 8 个测试项目不受影响。发布链路（[Publish-Server.ps1](../../scripts/Publish-Server.ps1) → 发布目录 apphost）始终正常，0025 的 Windows apphost/Chromium 证据未受波及；故障只在开发/测试宿主路径。

## 根因

[Server.csproj](../../src/Server/Server.csproj) 原用 `Content Include="../WebUI/dist/**/*" Link="wwwroot/..."` 把前端产物映射进 wwwroot。在 .NET SDK 10.0.201 下，构建生成的开发期清单（`obj/.../staticwebassets.development.json`）把全部 dist 资产锚定到 `src\Server\wwwroot\` 作为 ContentRoot；宿主启动时 `ManifestStaticWebAssetFileProvider` 为每个 ContentRoot 创建 `PhysicalFileProvider`，而该目录在仓库中从不物理存在，构造即抛 `DirectoryNotFoundException`。

关键事实：[Program.cs](../../src/Server/Program.cs) 把 `ContentRootPath`/`WebRootPath` 钉在 `AppContext.BaseDirectory`。因此：

- `dotnet run` 开发运行走 Production 环境 + `bin\wwwroot`（由 `CopyToOutputDirectory + Link` 复制），不读开发清单；
- 发布运行走发布目录的 wwwroot；
- 只有测试宿主（WebApplicationFactory）会加载源码树的开发清单，因而只有它需要 `src/Server/wwwroot` 存在。

## 修复

构建目标 `EnsureWwwrootDirectory`（`BeforeTargets="Build"`）只做 `MakeDir`，保证空目录存在。空目录即可满足清单 ContentRoot；静态文件本体仍按原路径供给（开发 → `bin\wwwroot`，发布 → `publish\wwwroot`），行为零变化。`.gitignore` 增加 `src/Server/wwwroot/` 防止手工文件污染。

## 已否决的替代方案（避免重蹈）

| 方案 | 结果 |
|---|---|
| 去掉 `Link` 元数据 | 清单 Assets 变空、开发清单不再生成；测试绿但 `dotnet publish` 的 `publish/wwwroot` 为空，违反发布脚本合同，开发服务也断 |
| 保留 `Link` + 构建期物理同步 dist → wwwroot | wwwroot 物理文件与 Content 条目被静态资源管线双注册，二次构建时 `ApplyCompressionNegotiation` 抛 `MSB4018: An item with the same key has already been added` |
| 去掉 `Link` + npm build 末尾同步 dist → wwwroot（单一物理真理源） | 测试与发布绿，但 `dotnet run` 坏：Program 钉死 `AppContext.BaseDirectory`，bin 下不再有 wwwroot；另发现清单增量陈旧（wwwroot 事后出现时旧清单不自动刷新，需强制重建） |
| 手工建空目录（临时容纳） | 本地有效但不可在 CI/新克隆复现，即本次修复的 Motivation |

## 重开条件

- 未来出现"通过 WebApplicationFactory 请求静态文件"的测试或功能：清单当前把静态文件映射到空的源码树 wwwroot，会 404；届时应改为物理 wwwroot 单一真理源并同步解除 Program.cs 的 BaseDirectory 钉定。
- R7 产品切换重做 Server 托管/发布布局时，可一并重审该目标是否仍需要。
