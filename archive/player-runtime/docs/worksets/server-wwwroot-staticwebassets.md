# Server wwwroot 与 .NET 10 静态资源清单故障调查

> 层级：调查记录。日期：2026-09-19。状态：已定稿修复（[Server.csproj](../../src/Server/Server.csproj) 关闭 `StaticWebAssetsEnabled`，弃用此前的 `EnsureWwwrootDirectory` 空目录目标）；本地全量测试与发布冒烟绿。

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

[Server.csproj](../../src/Server/Server.csproj) 用 `Content Include="../WebUI/dist/**/*" Link="wwwroot/..."` 把前端产物映射进 wwwroot。在 .NET SDK 10.0.201 下，构建生成的开发期清单（`obj/.../staticwebassets.development.json`）把全部 dist 资产锚定到 `src\Server\wwwroot\` 作为 ContentRoot；宿主启动时 `ManifestStaticWebAssetFileProvider` 为每个 ContentRoot 创建 `PhysicalFileProvider`，而该目录在仓库中从不物理存在，构造即抛 `DirectoryNotFoundException`。

关键事实：[Program.cs](../../src/Server/Program.cs) 把 `ContentRootPath`/`WebRootPath` 钉在 `AppContext.BaseDirectory`。因此：

- `dotnet run` 开发运行走 Production 环境 + `bin\wwwroot`（由 `CopyToOutputDirectory + Link` 复制），不读开发清单；
- 发布运行走发布目录的 wwwroot；
- 只有测试宿主（WebApplicationFactory）会加载源码树的开发清单，因而只有它需要 `src/Server/wwwroot` 存在。

## 修复（定稿：关闭静态资源管线）

仓库没有 RCL/Blazor/scoped assets，静态文件全部是 WebUI dist 的普通 Content 复制，而 Program.cs 把 WebRootPath 钉在 `AppContext.BaseDirectory`，三种宿主（开发、测试、发布）本就一律服务二进制旁的物理 wwwroot——静态资源清单管线对本项目零收益、纯风险。Server.csproj 设 `StaticWebAssetsEnabled=false`：SDK（10.0.201 的 `Sdk.StaticWebAssets.CurrentVersion.targets`）只在该属性为 true 时才导入 `Microsoft.NET.Sdk.StaticWebAssets` 整条 targets 链，关闭后不再生成任何 `staticwebassets.*` 清单、压缩与 endpoints 文件，测试宿主自然无从加载。

相比临时修复的实质改善：清单不再把测试宿主的静态文件映射到空的源码树 wwwroot（此前请求必 404），WebApplicationFactory 现在直接服务测试输出目录的真实 dist 资产。验证证据：

- [HttpBoundaryTests](../../tests/Server.Tests/HttpBoundaryTests.cs) 新增 `RootRedirectsAndStaticAssetsFollowOutputWwwroot`：`/` 302 重定向 `/player`；资产存在时 `/player`、`/index.html` 均 200（text/html），wwwroot 缺失（前端未构建的全新克隆）时分别降级 503/404。
- 移走测试输出 wwwroot 后全量重跑 14/14 绿：宿主可无 wwwroot 启动，不再需要任何目录兜底。
- `dotnet publish`（与发布脚本同参数）+ 发布产物 HTTP 冒烟：`/`→302、`/player`→200、`/index.html`→200（StaticFileMiddleware 直读发布 wwwroot）、API→200；发布目录无清单残留。

迁移注意：旧检出拉取本变更后，需删除 `src/Server` 与 `tests/Server.Tests` 的 bin/obj 各一次——旧 `*.staticwebassets.runtime.json` 残留在输出目录且增量构建不清理，会让测试宿主按旧清单继续崩溃，直到清空重建。CI 为干净构建，不受影响。

## 已否决或被取代的方案（避免重蹈）

| 方案 | 结果 |
|---|---|
| `EnsureWwwrootDirectory` 构建前 MakeDir 空目录（本调查的临时修复，曾让 CI 转绿） | 有效但把测试宿主静态文件钉在空源码树 wwwroot（请求必 404），且需长期维护被忽略的幽灵目录；被关闭管线方案取代 |
| 去掉 `Link` 元数据 | 清单 Assets 变空、开发清单不再生成；测试绿但 `dotnet publish` 的 `publish/wwwroot` 为空，违反发布脚本合同，开发服务也断 |
| 保留 `Link` + 构建期物理同步 dist → wwwroot | wwwroot 物理文件与 Content 条目被静态资源管线双注册，二次构建时 `ApplyCompressionNegotiation` 抛 `MSB4018: An item with the same key has already been added` |
| 去掉 `Link` + npm build 末尾同步 dist → wwwroot（单一物理真理源） | 测试与发布绿，但 `dotnet run` 坏：Program 钉死 `AppContext.BaseDirectory`，bin 下不再有 wwwroot；另发现清单增量陈旧（wwwroot 事后出现时旧清单不自动刷新，需强制重建） |
| 手工建空目录（临时容纳） | 本地有效但不可在 CI/新克隆复现，即最初修复的 Motivation |

## 重开条件

- 引入真正依赖静态资源管线的资产（RCL、Blazor、scoped assets、build-time 压缩）时，重审 `StaticWebAssetsEnabled=false` 并连同宿主 wwwroot 供给方式一起重设计。
- R7 产品切换重做 Server 托管/发布布局时，可一并重审静态资源供给方式。
