# 当前源码依赖

开发入口直接使用相邻 `../durable-graph` 与 `../atelia-storage` 的当前工作树，包括本地未提交修改。用户已选择上下游共同快速演进，不再要求先准备或发布包；项目不自动 fetch、pull、reset，也不回退到旧包。

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx -c Release --no-build --no-restore -m:1
```

`DramaBoard.Local.slnx` 是等价入口。源码不在相邻目录时，为构建命令提供两个绝对路径：

```powershell
dotnet build DramaBoard.slnx -c Release -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify `
  -p:DurableGraphSourceRoot=D:/dev/durable-graph `
  -p:StorageSourceRoot=D:/dev/atelia-storage
```

## 接线和边界

- `Directory.Build.props` 指定默认 DG 根；`Directory.Build.rsp` 为还原与构建同时设置 `UseStorageSources=true`、`StorageSourceRoot` 与 `ShouldUnsetParentConfigurationAndPlatform=false`。响应文件中的默认路径相对文件自身，从其他工作目录调用也有效。
- Kernel 和 Spatial 导入 `eng/DurableGraph.Source.props` / `.targets`，直接引用 runtime、Generator 与 Build 工具，并导入上游 schema 配置。Generator 作为 analyzer 加载；Build 工具通过项目的 `GetTargetPath` 取得当前配置输出，不依赖包内工具路径。
- Schema 模式沿用上游默认，本地普通构建会 Publish；验收和 CI 显式选择 Verify，不用自动发布历史掩盖 schema 差异。
- Spatial.Tests 单独引用 Persistence 源码，用于真实模型捕获与冷读资格检查。生产三核心没有新增磁盘 adapter 或 Repository 依赖。
- DG 校验 Storage HEAD 必须匹配其 `eng/StorageDependency.props` 的 `StorageSourceRevision`；Storage 提交前进时先协调 DG 声明。未提交的源码变化可直接联调，验收须记录两仓提交和工作树状态。
- 只从 nuget.org 还原第三方包；默认构建不再读取历史候选 feed。旧包缓存与 `scripts/Prepare-DurableGraph.ps1` 保留历史用途，原验收见[固定包来源](worksets/durablegraph-package-source.md)。
- CLI 自动读取响应文件。不读取 `Directory.Build.rsp` 的 IDE 宿主需要为还原与构建显式提供同样的全局属性，本次不宣称已验收所有 IDE。
- CI 直接 checkout DG `master` 和当前匹配的 Storage 提交，通过绝对根参数构建，不打包。它跟随上游变化，不是历史构建复现承诺；Storage pin 变化后相应更新 CI。
- 多个消费者共享上游 `bin/obj` 时串行构建，避免输出竞争。切换源码路径后重新还原和构建，不能拿旧输出证明新路径可用。

## 验证

2026-10-27 使用 DG `dd645c1bde973c82f95885b2fd0a395624ba6edb`、Storage `70b406cd8d1117733eee81a33e7f978399aa98b2` 完成以下验证。这是验收来源，不额外锁定 DG 提交。

- 清除进程内 `UseStorageSources`、`StorageSourceRoot`、`DurableGraphSourceRoot` 与 `ShouldUnsetParentConfigurationAndPlatform` 环境变量后，普通 Release solution 构建成功，零警告、零错误。
- 从仓库外工作目录使用 `DramaBoard.Local.slnx`、显式两个绝对源码根和 Schema Verify 构建成功。Kernel 验证 7 个当前及历史定义，Spatial 验证 24 个当前及历史定义；历史文件未修改。
- 三个核心测试项目全通过：Host 2、Kernel 114、Spatial 60，共 176 项，零失败、零跳过，包含真实模型资格、冷读与跨三库时空场景。
- 实际 restore assets 中，Kernel/Spatial 生产闭包各含 3 个 Atelia 项目（runtime、Serialization、Binary），Spatial.Tests 含 10 个 Atelia 项目，全部 `type=project`；没有混入旧 Atelia 包。Generator 与 Build 工具由源码项目构建为 Release 输出。
- `--no-restore` 下传入缺失 DG 或 Storage 路径会明确报错，无包回退。对应架构守卫已从固定包名约束改为检查源码接线，继续禁止 Persistence 与构建工具成为 Spatial 的生产运行时依赖。

详细日志保存在忽略目录 `artifacts/source-integration/`：`build.log`、`external-verify.log`、`tests.log`、`assets-audit.json` 与 `missing-*.log`。未运行远端 CI、Linux 或 IDE 构建；没有新增磁盘 adapter、修改游戏语义或运行模型 API。
