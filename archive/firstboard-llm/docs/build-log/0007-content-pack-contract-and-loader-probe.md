# Build Log 0007：Content Pack contract、one-shot loader 与 Pack A 激活

> 状态：**Planned implementation vertical slice**
>
> 依赖：[Build Log 0005](0005-game-definition-and-ruleset-bindings.md)
>
> 吸收原 Build Log 0008 的 Pack A externalization。
>
> 范围：一次建立 Ruleset-specific Provider、两字段 manifest、Default ALC loader 与真实 Pack A project，并把 production Runner 切换到 `--content`；不泛化 roster 或引入 Pack B。

## 1. 完成状态

Pack A 的普通 `dotnet build` output 可以直接交给同一个 Runner 做 `content validate` 或 new run。production 不再从 FirstBoard Ruleset 内的 `ScenarioDefinition.Default` / `CreateDefault()` 构造作者内容；Runner 不静态引用 Pack A，Pack A 不引用 Runner。

真实 Pack A 是成功 loader fixture。Default ALC 的失败面仍由 one-shot 子进程隔离验证，不能因为 0007/0008 合并就把 assembly-loading 证据缩成纯 parser tests。

## 2. Pack project

```text
content/DuchessLetterMarket/
  Content.DuchessLetterMarket.csproj
  DuchessLetterMarketContentProvider.cs
  content-pack.json
```

项目明确使用自然且唯一的 assembly identity，并把 manifest 复制到 build/publish output：

```xml
<AssemblyName>DramaBoard.Content.DuchessLetterMarket</AssemblyName>
<RootNamespace>DramaBoard.Content.DuchessLetterMarket</RootNamespace>
<None Update="content-pack.json"
      CopyToOutputDirectory="PreserveNewest"
      CopyToPublishDirectory="PreserveNewest" />
<ProjectReference Include="..\..\src\FirstBoard\FirstBoard.csproj"
                  Private="false" />
```

SDK 默认已把 manifest 纳入 `None` items，因此使用 `Update` 而不是重复 `Include`。`Private="false"` 保证 Pack output 不携带第二份 Ruleset/provider contract。Content project 加入两个 solution 的 `/content/` folder。

## 3. Provider、bootstrap 与 loader

1. FirstBoard project 暴露 `IFirstBoardContentProvider.CreateDefinition()`；Provider 只返回 sealed Definition。
2. bootstrap 只接受 `format + entryAssembly`，未知属性拒绝。
3. `entryAssembly` 必须是 Pack root 中的 basename-only、非 rooted、无 separator/`..` 的 regular `.dll` file。
4. Runner 已先加载共享 Ruleset contract；随后用 `AssemblyLoadContext.Default.LoadFromAssemblyPath` 加载唯一 entry assembly。
5. exported types 中必须恰好一个 public、concrete、closed、具有 public parameterless constructor 的 Provider。
6. loader 创建 Provider 一次、调用 `CreateDefinition()` 一次，立即 `Validate → Freeze → canonicalize → hash`，随后丢弃 Provider。
7. load/type/constructor/CreateDefinition/null/incompatible contract 错误带 Pack path、assembly 和稳定错误类别，在 Genesis 前拒绝。

production loader 只调用 Provider 一次。Content 项目自己的 deterministic-build smoke 才调用两次 Provider，并断言两次 frozen canonical bytes/hash exact 相同；不要为了测试纯度而让 production loader 执行两次作者代码。

## 4. One-shot ALC test discipline

`tests/FirstBoard.ContentRunner.Probe` 是 test-only `OutputType=Exe`。它静态引用 Runner/loader，但不引用任何 Provider fixture；本 slice 先提供 `validate <pack-path>` 能力，后续 0009 可在同一 probe 上增加 session smoke。

- manifest JSON 与纯 path/predicate tests 可以在 xUnit 进程运行；Content 项目也可直接构造自己静态引用的 Provider 做 deterministic-build unit test。
- 任何通过 runtime loader 调用 `LoadFromAssemblyPath`、反射激活或执行 Provider 的 case 都启动新的 probe process。
- 不在同一个 xUnit process 顺序加载 0-provider、2-provider、失败 fixture 或真实 Pack。
- fixture 的 ProjectReference 只建立 build order，使用 `ReferenceOutputAssembly="false"` 或等价设置，不让测试/probe 静态加载 fixture assembly。
- 每个 Content Module assembly simple name 唯一；测试不尝试 unload、同进程换包或覆盖磁盘 DLL 后重载。

最小动态矩阵包括：

```text
real Pack A success
0 valid Provider（可同时证明 abstract/open generic 被排除）
2 valid Providers
constructor failure
CreateDefinition failure
null Definition
incompatible/stale load failure
```

这些 case 可以复用最少数量的 fixture projects，但每次实际 load 都必须 fresh process。

## 5. Pack A migration 与 production activation

1. 将现有 C# Pack A factory 与 author-owned ID constants 移到 Content project。
2. 使用真实 build output 验证 manifest、唯一 entry DLL，以及不复制 Ruleset contract。
3. Runner 增加 `content validate --content <pack-dir>`；它只做 bootstrap、Provider 和 Definition validation，不创建 Player/backend。
4. new-run Program 增加必需 `--content <pack-dir>` 并使用同一 loader。
5. 迁移并最终删除 production `ScenarioDefinition.Default`、`ScenarioInstance.CreateDefault()`、`FirstBoardWorld.CreateInitial()` 和 default `FirstBoardScenario.RunAsync(...)` overload。
6. FirstBoard、Runner、Persistence tests 通过唯一 Pack A test dependency/factory 获得 Definition，不复制第二份 factory。
7. 以外置前后的 canonical bytes/hash、Genesis 和 scripted trace exact 等价收敛。

## 6. 非目标

- 不枚举任意 FirstBoard roster，不清理 Runner 的 Alice/Bob options、Presentation 或 report；由 0009 完成。
- 不新增 Pack B。
- 不建立 custom/collectible ALC、registry、DI container、private dependency resolver、hot reload 或 binary compatibility negotiation。
- 不在同一进程加载多个 Pack，不把 ALC 当安全边界。
- 不修改 Save、Journal 或 production codec reader。

## 7. 验收

- [ ] parser 对坏 format、未知字段、absolute/`..`/separator path、缺文件稳定拒绝。
- [ ] 所有经 runtime loader 的 assembly load/Provider execution case 都由 fresh one-shot process 覆盖。
- [ ] 真实 Pack A build output 含两字段 manifest 与唯一 entry assembly，不含 Ruleset/provider contract 副本，可直接 validate/run。
- [ ] real success、0/2 Provider、invalid type、ctor/create/null 和 incompatible load 均有稳定结果。
- [ ] Provider production 调用恰好一次；Content deterministic smoke 的两次 canonical identity exact 相同。
- [ ] 外置前后 Definition、Genesis 与 trace 等价。
- [ ] production Default creation APIs 已删除；Ruleset production project 对 Pack A author IDs 零消费。
- [ ] Runner 不 `ProjectReference` Pack A；Pack A 不引用 Runner；probe/测试不静态引用 loader fixtures。
- [ ] 主/本地 solution 均构建 Pack A；`dotnet test` 全量通过。

## 8. 建议提交边界

这是一个 activation，可按“contract/loader/one-shot tests”和“Pack A migration/Program cutover”分成两个提交；第二个提交完成前不得把未被 production 使用的 loader 当作竖切完成。
