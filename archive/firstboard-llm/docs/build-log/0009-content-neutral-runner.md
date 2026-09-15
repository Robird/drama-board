# Build Log 0009：Content-neutral Runner 与 Pack B 防伪验收

> 状态：**Planned implementation vertical slice**
>
> 依赖：[Build Log 0007](0007-content-pack-contract-and-loader-probe.md)
>
> 吸收原 Build Log 0010 的 Pack B anti-fraud acceptance。
>
> 范围：把 Runner 的 roster、options、LLM composition、Presentation、manifest 与 report 全部改为 Definition-driven，并以真实 Pack B 证明同一 production Runner pipeline 和 Ruleset 跨 Pack 工作；不跨 Ruleset 泛化。

## 1. 完成状态

Runner 不再把 Alice/Bob 或 Pack A labels 当作进程结构。Pack A 与 Pack B 的 Actor、Place、Passage、Object ID sets 完全不相交；同一 production Runner binary 能 validate 两包，同一 production post-bind run pipeline 能分别推进两包的 Session。

现有 mixed-model 能力保留，但固定 Alice/Bob 参数改成 ActorId-keyed override。自动化验收使用 test-only probe 注入 deterministic Random Players，不为测试向 production CLI 增加 `--driver random`。

## 2. Options 两阶段绑定与 mixed-model 迁移

```text
parse bootstrap and syntax
→ load/freeze Definition
→ ordinal-exact bind actor-specific options
→ resolve complete Player slots
→ create backends
```

最小 production 参数：

```text
--human <actorId>
--backend <backend> --model <model>
--actor-llm <actorId> <backend> <model>   # 可重复
--memory-backend <backend> --memory-model <model>
```

`--backend/--model` 是所有 AI Actor 的默认 decision config；重复的 `--actor-llm` 覆盖一个 Definition Actor。使用三个独立 CLI token，不发明 `actorId=value`/`actorId:...` 编码，因为 author-owned ID 未承诺排除这些分隔符。

syntax parse 不提前认识 Actor roster；Definition 加载后按 ordinal exact ID 校验。unknown Actor、重复 override、针对 Human slot 的无效 override 都在 backend 创建前拒绝。Human slot 不创建 LLM resource。Memory backend/model 仍为 process-level resolved config，并与各 decision backend 按结构值去重。

旧 `--alice-*` / `--bob-*` spelling 未发布，可直接删除，不建兼容层；但不同 AI Actor 使用不同 backend/model 的已存在能力不得删除。manifest、report 与后续 Save composition 记录的是每个 slot 的 resolved config，而不是 default/override 的 CLI 来源。

## 3. Runner surface migration

1. roster 来自 frozen `Definition.Actors`，为每个非 Human Actor 创建一个 Player。
2. CharacterCard、ReferenceMaterial、Memory schema/initial content 来自对应 Actor definition。
3. Program 启动摘要、profiler、run manifest 与 report 枚举 resolved slots，不写固定 Actor fields。
4. Presentation/report 使用 Ruleset facts 与 Definition 的 Actor/Place/Passage/Object labels/descriptions，不含 Pack A entity-ID switch。
5. Human selection 在加载 Definition 后验证；Human barrier、committed presentation frontier、取消、flush 与 cleanup 行为保持。
6. 在 Runner 程序集内收束一个 production run pipeline。Program 传入由 CLI 绑定得到的完整 LLM/Human `ResolvedPlayerSlot` plan；测试 probe 在同一个 composition seam 传入完整 deterministic Random plan。descriptor 与 factory 必须成对注入，pipeline 的 manifest/report/cleanup 始终枚举实际 plan。
7. production new run 通过可注入 allocator 分配 fresh、collision-resistant root `LineageId`；固定 `FirstBoardScenario.LineageId=10001` 只保留为测试 fixture，不再让两个独立 run 产生相等 `WorldVersion`。

这个 seam 不成为 Content extension point，不建立通用 plugin/DI framework，也不允许 Content 注册 Player factory。

## 4. Pack B 边界

```text
content/<PrototypeB>/
  Content.<PrototypeB>.csproj
  <PrototypeB>ContentProvider.cs
  content-pack.json
```

Pack B 只改变 Actor、Role/Memory、Graph、Objects、labels、初态、typed bindings 与 closed typed prose；不新增 action、fact、第三名角色、脚本、第二 Ruleset 或新世界 Law。A/B 的所有 author-owned Actor/Place/Passage/Object IDs 必须完全不相交。

0005 的内存态 B Definition 只是最早期 test fixture。引入真实 Pack B 时，必须把该 Definition factory 移到 Pack B Content project，或让测试直接消费 Pack B Provider 暴露的同一 factory；随后删除临时副本。core exact trace、Pack B provider 与 Runner smoke 不得长期各维护一份会漂移的 B schema/config。

## 5. 两层验收，不增加 production random CLI

### 5.1 Production binary / pipeline smoke

```text
production Runner child + content validate + Pack A
production Runner child + content validate + Pack B

test-only ContentRunner.Probe child + production post-bind pipeline + Pack A
test-only ContentRunner.Probe child + production post-bind pipeline + Pack B
```

0007 的 one-shot probe 增加 `smoke <pack-path> <seed>`。它通过 production loader、roster、run pipeline、manifest、report 与 cleanup，在 composition seam 为每个 Definition Actor 构造 descriptor/factory 一致的 `Random(seed)` resolved slot plan；recording wrapper 只是非权威观测层，不改变 descriptor。A/B 各用新的 child process，构造全部 Actor 的首个 request 并推进 Session；backend factory 调用必须为零。production LLM/Human options 的两阶段绑定由同一 `ResolvedPlayerSlot` builder 的 unit/integration tests 另行锁定，probe 不伪装成 LLM options run。

验收必须表述为“同一 production Runner pipeline through probe”，不能声称 production EXE 提供不存在的 random CLI。后继 0018 可以把现有 request-addressed `Random(seed)` 纳入 closed checkpoint union，用于严格的 Save/continue harness；这仍不等于用户可选择的 production mode。若未来用户真实需要无 LLM 本地玩法，再单独设计 CLI。

### 5.2 Ruleset exact trace

使用 `ScriptedPlayerDriver` 对实际 A/B Definition 分别运行 exact integration trace：首个 DecisionRequest、travel、deadline、unlock container、reveal object、inspect knowledge 与 replay。不要创建外部 script format 或 test driver plugin。

对 World、Observation、Presentation、manifest、Journal、report 的结构化 author-ID fields 做 Pack A 集合零命中检查。自然语言不做 raw substring 禁令，但 Pack B 的名称、描述、authenticity/content text 必须来自 Pack B Definition，不得出现 Ruleset 中固定的 Pack A prose。

## 6. 非目标

- 不增加 production `--driver random`、`--random-seed` 或只服务测试的 CLI/environment hook。
- 不删除 per-Actor mixed-model capability。
- 不跨 Ruleset 泛化 Runner，不新增 `Runner.Core`、Player plugin、Content service registration 或通用 composition framework。
- 不增加 GUI、MCP、asset resolver、第三个 Prototype 或新世界 Law。
- 不做项目/namespace/type rename；0011 是非阻塞机械 leaf。

## 7. 验收

- [ ] roster/Human/AI composition 对任意 Definition Actor collection 工作。
- [ ] default decision config、多个 ActorId override 和 process-level Memory config 正确 resolve/deduplicate。
- [ ] duplicate/unknown/Human-slot override 在 backend 创建前拒绝；Human slot 不创建 LLM resource。
- [ ] Program、Presentation、LLM、manifest、report、profiler 和 cleanup 中无 Alice/Bob fixed slots 或 Pack A label switch。
- [ ] 同一 production Runner binary 在独立子进程 validate A/B build output。
- [ ] 同一 production post-bind pipeline 经 one-shot probe 为 A/B 全部 Actor 构造首个 request 并推进 Session，backend 调用为零。
- [ ] probe 的 resolved descriptor 与实际 Random factory 一致；manifest/report 不出现未创建的 LLM backend/model，recording wrapper 不进入权威 composition。
- [ ] A/B scripted exact traces 完成 travel、deadline、container、reveal、inspect 与 replay。
- [ ] Pack B Definition 只有一个 Content-owned factory；0005 临时 B fixture 已迁移/删除，core trace 与 Provider 消费同一份数据。
- [ ] Pack B 的结构化 author IDs 对 Pack A 集零命中，Prompt/Presentation/report 使用 B 自己的 names/descriptions/typed prose。
- [ ] Runner 不静态引用 A/B；A/B 不引用 Runner；两个 solutions 和全量 tests 通过。
- [ ] 两次独立 new run 得到不同 root lineage；测试可注入固定 allocator，production 不复用常量 lineage。

## 8. 建议提交边界

这是一个以真实 Pack B 收敛的 Runner vertical。可并行组织“options/roster”和“Presentation/report/manifest”，再加入 Pack B/probe acceptance；只有 A/B production-pipeline smoke 与 exact trace 都通过后才标记完成。
