# Build Log 0023：Persistent Script VM 海选章程与首轮候选审计

> 状态：**selection charter + official-source paper screening；尚无最终 winner；未授权 DramaBoard production migration**
>
> 记录日期：2026-08-26
>
> 前置证据：已归档的 [StateJournal-native experiments](../archive/statejournal-native.md)。

本文记录一项独立研究方向：选择一个开源、可嵌入、适合 Coding Agent 编写应用代码的 Script VM，把 StateJournal 已验证的 stable object identity、dirty tracking、object-level delta、version chain、branch/ref、reopen 与 ambiguous-commit recovery 思路下沉到 VM 的对象模型中。

目标不是把当前 StateJournal 原封不动嵌入某门语言，也不是替换一个 serializer。若研究成功，Script VM 中的领域对象本身就是 durable objects；脚本 class 同时承载领域行为、持久字段 schema 与 object reference semantics，应用不再维护 C# POCO、durable wrapper、Read/Write/Open/ExactKeys 与 reducer/adapter 双实现。

本文件同时是后续海选工作的规范输入。候选的流行度、源码行数或未修改 VM 的 benchmark 都不能替代本文的 hard gates 和统一 probe。

## 0. 当前裁决

### 0.1 研究对象

V1 研究对象是：

```text
persistent durable realm
+ run-to-completion transaction VM
```

而不是：

```text
arbitrary full VM heap snapshot
+ suspended stack / closure / Promise / Fiber resume
```

唯一推荐的执行方程是：

```text
(parent commit, canonical input, pinned code bundle)
    -- deterministic run-to-completion invocation -->
(child durable realm, semantic event/summary, durable outbox)
```

只有 exported transaction 正常返回、调用栈为空、没有 suspended execution state 时才允许 commit。异常、instruction/allocation budget exhaustion、host callback failure 或 validation failure 都使当前 VM/session fail-stop；不得在 partial-mutated realm 上继续执行。

### 0.2 Paper shortlist

首轮 official-source 审计得到三个 paper-primary P0 候选；它们仍须先关闭自己的unknown hard gates，才能进入RealmStore P1：

1. **PUC Lua 5.5.1**：integration-floor hypothesis；成熟小型 C VM、原生 signed Int64、标准 Lua/C API，检验最低集成复杂度能否承载目标语义。
2. **mruby 4.0.0**：class-first hypothesis；可固定 `MRB_INT64`、`RData`/mrbgem extension seam，检验自然领域 class 是否显著改善durable identity体验。
3. **QuickJS 2026-06-04**：tooling-ceiling hypothesis；标准JavaScript加TypeScript `checkJs`、`.d.ts`与Language Service sidecar代表Coding Agent工具上界，但 ES object semantics 和 Windows packaging 风险最大。

辅助候选：

- **AngelScript 2.38.x**：typed G4 challenger / possible graph-match control；先验证普通typed instance能否write-time direct store；若只能reflection/reconstruction，再量化graph matching是否已经足够简单。
- **Koto**：deep-fusion dark horse；`i64`、`IndexMap`、集中式 `Ptr/PtrMut` mutation seam、官方 `koto-ls` 与CLI formatter很有吸引力，但hard memory quota、Rust↔C# ABI、0.x upstream稳定性与runtime-only type hints尚未过门。
- **Luau**：typed Lua reserve；type checker、linter、sandbox 和 interrupt 很强，但 exact 64-bit integer 的 release/API/operator 语义必须先做 micro-gate。
- **Squirrel**：native-heap fork reserve；固定 instance slots、x64 Int64 与小型 C++ VM 很适合源码级研究，但工具链和 Agent 反馈弱。

交叉评审产生过两种不同的P1组合：

- information-gain portfolio：PUC Lua + AngelScript + Koto，分别测试成熟小VM、静态typed control与deep-fusion choke point；
- product/Agent-priority portfolio：PUC Lua + mruby + QuickJS，分别测试最低集成风险、自然OOP与最佳标准工具上界。

本文采用第二组作为paper-primary portfolio，而不是保送的P1名单。原因是它优先检验当前最有可能形成产品的integration floor、class-first与tooling ceiling；Koto不能只凭源码结构提前晋级，AngelScript也不能只凭官方serializer就被预设为graph-match control。为避免循环淘汰，在锁定P1席位前，Koto必须获得一次严格time-boxed的G4/G7/G9/C# ABI micro-gate，AngelScript必须获得同尺度的G4 challenger micro-spike；通过者晋升paper-primary pool，并在补齐完整P0后凭可执行证据竞争P1席位。

此处没有保送 P1 或 P2。三个paper-primary必须先关闭各自P0 unknowns才有资格竞争；最终获分配的P1候选完成完全相同的red spike。最多两个进入真实StateJournal-fusion probe，也允许只剩一个或`no winner`。

## 1. 产品目标与非目标

### 1.1 必须获得的应用体验

目标体验接近：

```text
script class definition = domain behavior + durable field schema
script instance          = Revision-bound durable object
script reference         = durable object reference
method invocation        = run-to-completion working transaction
commit / reopen / fork   = versioned durable realm operations
```

示意代码：

```text
class Actor
  durable Int64 hp
  durable Int64 mp
  durable Actor? peer

  castFireball(target)
    target.hp = checkedSub(target.hp, 10)
    self.mp = checkedSub(self.mp, 15)
```

一次成功的领域规则最终只保留一份 mutation implementation。研究期可以继续用现有 C# reducer 作 differential oracle；若路线被生产采用，C# oracle 不得永久成为第二个 mutation authority，否则本项目的 killer value 没有兑现。

### 1.2 非目标

V1 明确不购买：

- arbitrary instruction-point checkpoint；
- suspended call stack、Fiber、coroutine、generator、Promise 或 Task resume；
- mutable closure/upvalue 跨 commit；
- JIT/native machine-code snapshot；
- file/socket/process/CLR object/native pointer 持久化；
- weak reference、finalizer 或 GC timing 的业务可观察语义；
- ambient wall clock、locale、environment 或 global random；
- dynamic module loading、`eval`、hot reload 或 prototype/class mutation进入 durable authority；
- 多线程共享 mutable realm；
- 自动 event-sourcing completeness；
- 立即重写 DramaBoard production Runner、Save、Kernel 或现有 build logs。

### 1.3 External effect 边界

Player、LLM、tool、network 等异步效果必须拆成显式的两个 run-to-completion invocation：

```text
transaction A
  -> create PendingEffect / outbox(requestId, payload)
  -> commit canonical pending request
  -> reopen/classify that commit if publication is ambiguous

host dispatches only after the pending commit is confirmed
  -> use requestId as the external idempotency key
  -> await external system

transaction B(requestId, result)
  -> validate exact request/result binding
  -> mutate durable realm
  -> one commit
```

不得把修改了一半世界的 suspended script frame 当成 durable continuation。只有纯确定性host query，或外部系统已经以stable requestId提供可证明的幂等调用时，才可省略独立pending commit；这必须由该effect自己的failure contract证明，不能作为默认路径。

## 2. StateJournal 思路如何进入 VM

本研究不要求新 VM 依赖当前 `Atelia.StateJournal` public API 或 wire format。预期复用的是已经通过实验的技术与 failure laws：

| StateJournal 经验 | Script VM 中的目标形状 |
|---|---|
| Revision-local object identity | Realm-local stable ObjectId / handle；同realm共享引用保持 `Same` |
| Root reachability | `RealmRoot` 决定当前 durable closure |
| DurableObject dirty tracking | VM field/list/map write barrier直接标记dirty object |
| Rebase + delta version chain | 每个durable object追加immutable version；checkpoint可压缩读取成本 |
| GraphRoot commit | realm root、frontier、code/schema binding、summary/outbox同commit |
| Branch/ref | same-repository fork、rewind、independent suffix |
| Repository-owned lifetime | realm/session dispose后旧instance、iterator、host capability全部失效 |
| structured commit outcome | candidate后的failure必须poison、reopen、classify exact parent/child |

预期被替换或删除：

- 应用直接操作 `DurableDict/Deque/...` 的 public modeling surface；
- C# domain wrapper `_data` ceremony；
- wrapper factory / POCO hydration / reconciliation；
- `F`字段字符串、手写Create/Open/Read/Write/ExactKeys；
- production reducer与durable mutator双实现；
- 把语言对象转换成另一棵storage graph的commit-time serializer。

VM 可以保留普通 transient heap，同时新增 durable realm；不要求第一天就把所有普通 language objects 改成durable。最终合格形状至少要让领域 instance/list/map/ref 本身成为 VM 可见的 canonical durable identity，而不是在commit时复制一棵POCO图。

### 2.1 G4 的两个正交维度

为避免用词掩盖架构差异，G4分开记录mutation timing与object representation；同一实现必须在两个轴上各归类一次。

Mutation timing：

| 类别 | 赋值与commit行为 | 本研究中的裁决 |
|---|---|---|
| Direct durable mutation | script field/list/map opcode或native property hook立即读写当前transaction的RealmStore；commit不扫描另一棵mutable graph | **G4 pass所必需** |
| Commit-time graph reconciliation | ordinary VM heap先独立变化，commit再遍历、匹配stable ID、diff并写另一棵storage graph | **只作control；不能成为native-realm winner** |

Object representation：

| 类别 | identity / state表示 | 本研究中的裁决 |
|---|---|---|
| Host-backed durable proxy | VM native userdata/class instance只持Realm/ObjectId handle；每realm按ObjectId intern同一language instance；所有mutable state都在RealmStore | **允许成为最终winner**，前提是没有mutable shadow copy、per-field host ceremony或commit-time full graph scan |
| VM-native durable object kind | ObjectId/realm binding进入VM value/object representation；allocation、field op、GC trace和write barrier直接理解durable object | **weighted architectural advantage**，不是强制迁移终点 |

因此“native durable realm”的最终行为契约是write-time direct store、ObjectId interning、无mutable shadow copy、无commit-time full graph scan。P1仍须给出allocation/get/set/ref/GC的source map，用于评估proxy维护成本与未来deep-fusion可行性；它不是承诺winner必须再改造成新的VM object kind。

## 3. Durable 与 transient value universe

### 3.1 V1 durable values

- `null` / boolean；
- exact signed Int64；
- exact unsigned 64-bit opaque value，例如`WorldSeed64`/RNG coordinate；V1只要求bit-exact codec、equality、hash与host-call binding，不开放script arithmetic或与Int64隐式混型；
- immutable UTF-8 string / bytes；
- durable object reference；
- durable fixed-schema instance；
- durable list/deque；
- canonical ordered map/set；
- optional durable text；
- opaque domain identifiers with canonical codec。

### 3.2 V1 transient-only values

- ordinary mutable table/object/array，除非显式copy到durable value；
- function、closure、upvalue cell；
- call frame、iterator、generator、Fiber、coroutine、Promise、Task；
- exception、backtrace、debug state；
- class/module/method table本体；对象只持stable logical `ClassId`；
- native/foreign pointer、host service、C# object；
- weak reference、finalizer、GC handle；
- bytecode/JIT/cache/intern table。

把 transient value 写入 durable field 必须在赋值点立即失败，并带 source span；不能等到reopen时才发现。

### 3.3 Code 与 schema

Realm root必须绑定：

```text
VM format version
bytecode ISA / builtin ABI version
immutable code-bundle hash
logical ClassId registry
durable schema versions
Ruleset / Definition binding
```

bytecode可以是可重建cache，但不能成为save authority。旧commit只能由exact code/ABI解释，或通过显式deterministic migration生成新branch commit；不能悄悄以新字段布局、新module slot或新numeric law读取旧realm。

### 3.4 V1 protocol laws

共同RealmStore contract在开始任何P1之前固定以下语义：

- durable Int64 arithmetic采用checked law；超出`long.MinValue..long.MaxValue`立即导致当前transaction失败。候选的native wrap或arbitrary-precision arithmetic不能成为另一条隐式authority路径；所有来自durable Int64的算术必须经过结构化的branded value/operator、编译/bytecode verifier或等价机制，不能靠作者记得调用helper；
- `WorldSeed64`等unsigned opaque value必须覆盖完整`0..2^64-1` bit pattern，但V1禁止script arithmetic；需要运算时交给具有canonical ABI的host pure function，或在未来protocol version增加独立numeric law；
- `DurableOrderedMap`按schema定义的canonical key codec升序枚举；insertion history不属于observable state。V1仅允许已有canonical total order的key type，例如Int64、UTF-8 string与明确注册的opaque ID；
- 数值比较、map key equality/order、host round-trip都使用同一schema codec；float或alternative boxed numeric不得与Int64隐式混型；
- 若后续证据要求另一种overflow或ordering语义，必须先提升protocol/VM ABI version并修改所有候选的共同fixture，不能让候选各自解释。

这些不是为probe虚构的新领域法则：当前[`ModelTime`](../../src/Kernel/Time/ModelTime.cs#L73)与FirstBoard generation/sequence更新已经使用checked arithmetic；[`FirstBoardGameState.WorldSeed`](../../src/FirstBoard/FirstBoardDomain.cs#L79)和[`ScenarioInstance.WorldSeed`](../../src/FirstBoard/ScenarioDefinition.cs#L641)已经是`ulong`，确定性随机测试也固定了超过`long.MaxValue`的[stable 64-bit pattern](../../tests/Kernel.Tests/Random/DeterministicRandomTests.cs#L49)。V1把后者建模成opaque unsigned bits，是为了保留现有能力而不额外购买第二套script arithmetic。

## 4. Hard gates

以下gate先于任何weighted score。任一失败即可淘汰；stars、语言热度、未修改VM性能或Agent熟悉度不能补偿。

| Gate | 必须提交的证据 | 淘汰条件 |
|---|---|---|
| G1 License / reproducible fork | 固定upstream tag/commit；许可证与NOTICE；Windows/Linux clean build；官方tests baseline | 修改/分发义务不可接受；依赖不可冻结；没有可运行baseline |
| G2 Exact 64-bit values | Int64的`long.MinValue/MaxValue`、`2^53+1` literal/operator/comparison/map key/host round-trip与checked overflow；opaque UInt64的`0/long.MaxValue+1/ulong.MaxValue` codec/equality/host-call binding | 任一路径静默经过double；Int64 overflow可wrap或继续为bigint；只能靠作者记得调用特殊API；signed/unsigned/float key equality混型 |
| G3 Deterministic ordered collections | insert/update/delete/reinsert、fresh process、GC、reopen、fork后observable iteration完全一致 | 只在commit/export时临时排序；脚本运行时仍依赖hash/address/seed顺序 |
| G4 Durable object seam | instance/list/map/ref的allocation、field get/set、identity、GC tracing与dirty marking有明确choke points；stable ObjectId进入VM value path | 只能commit-time POCO转换；raw object可混入canonical graph；mutation存在不可拦截旁路 |
| G5 Durable/transient isolation | closure/fiber/host handle/weak/native value写入durable field立即失败；root closure validator | unsupported value可逃逸；只能reopen后发现；必须持久化stack才能通过 |
| G6 Run-to-completion transaction | central exported call/return safepoint；empty stack；异常/quota/validation failure恢复exact parent | nested frame可commit；失败后继续使用partial realm；需要隐藏rollback魔法 |
| G7 Embedding / isolation / quotas | realm create/destroy；allocator、instructions、recursion/stack、output与cancellation限制；default无ambient IO/time/RNG/native loading | 无可靠interrupt/resource cap；global runtime state妨碍独立realm；host capability无法封闭 |
| G8 Reopen / branch / version | commit→dispose→reopen；historical fork→independent suffix；wrong code/schema/ABI fail closed | identity跨realm泄漏；必须加载旧heap snapshot/bytecode才能恢复；provenance含糊 |
| G9 Agent tool loop | pinned、documented、machine-checkable syntax；format/check/compile/test命令；source-span diagnostic与stack trace；host stubs/declarations | 每次业务调试要读VM内部；错误没有源码位置；没有可自动化test command |

G4允许使用官方native userdata/class extension seam形成长期host-backed durable proxy，也允许新增VM-native object kind；二者都必须满足第2.1节的direct-mutation行为契约。candidate必须提交相关源码choke-point与维护面证据，但不强制承诺从proxy迁移到新object kind。AngelScript commit-time graph matcher只作为control，不自动通过G4。

## 5. Weighted criteria

只对全部通过hard gates的候选评分：

| Criterion | Weight | 高分含义 |
|---|---:|---|
| Persistent-realm architectural fit | 25 | object allocation、field ops、containers、refs与GC集中在少数稳定choke points |
| Coding Agent / standard tooling | 20 | 标准语法先验、LSP/stubs、formatter、diagnostics与tests可直接使用 |
| Determinism / transaction margin | 20 | Int64、ordered iteration、safepoint、quota和capability由结构保证 |
| Fork maintenance / upstream absorption | 15 | patch语义集中；官方tests大部保留；升级冲突面清楚 |
| Embedding / isolation / observability | 10 | teardown、allocator/fuel、host audit、source stack完备 |
| Modified-runtime size / performance | 5 | **改造后**startup、memory、field access和commit delta满足目标 |
| Source / test / release quality | 5 | official tests、release可pin、build矩阵和安全升级路径清楚 |
| **Total** | **100** | hard gates已先通过；总分不负责拯救失败候选 |

每项只使用0–4粗粒度，并单列evidence confidence：

- `A`：统一probe可执行；
- `B`：精确源码审计；
- `C`：官方文档声明；
- `U`：未知。

P1前不发布精确总分。若候选差异小于证据不确定性，追加discriminating probe，而不是用小数点宣布winner。

## 6. 统一 candidate probe

### 6.1 Domain fixture

所有候选实现相同source-level domain与assertions：

```text
Actor {
  id: Int64
  hp: Int64
  mp: Int64
  peer: Actor?
  inventory: DurableList<Item>
}

Item {
  id: Int64
  owner: Actor
}

World {
  worldSeed: WorldSeed64 = 13_535_481_488_331_451_459
  modelTime: Int64 = 9_007_199_254_740_993
  actors: DurableOrderedMap<Int64, Actor>
  primaryActor: Actor
  primaryItem: Item
  audit: DurableList<String>
}

A.peer = B
B.peer = A
World.primaryActor = A
World.actors[A.id] = A
World.primaryItem = the same Item stored in A.inventory
Item.owner = A

castFireball(caster=A, target=B, damage=10, mana=15)
```

`World.primaryActor`与`actors[A.id]`必须得到同一language instance。A↔B cycle和Item alias必须在commit/reopen/fork后保持。

### 6.2 Normative assertion matrix

P1 pass必须执行相同test IDs与exact assertions；候选不得用一段happy-path demo自行声称满足hard gate。P1 fixture的authoritative map严格遵守第3.4节：按schema canonical key codec升序遍历；候选native container的默认顺序不能改变该contract。

P1表中的“crash/reopen”只表示销毁VM/realm session后，由test-owned in-memory store保留immutable commits并创建新session；“publication ambiguous”只表示scripted exact-parent/exact-child fault injection。真实process crash、repository reopen与MayHavePublished publication fault只能由P2 StateJournal-fusion证据证明，P1报告不得把模拟结果写成durability结论。

| Test ID | Normative assertion |
|---|---|
| `NUM-01` | `long.MinValue`、`long.MaxValue`、`2^53+1`在literal、field、operator、host round-trip后bit-exact |
| `NUM-02` | `long.MaxValue + 1`、`long.MinValue - 1`与乘法overflow都必须在发生算术的当前transaction产生checked failure并恢复exact parent；native wrap、扩大为bigint后继续执行或仅在最终field write截断都失败 |
| `NUM-03` | Int64作为map key保持exact equality/order；float、JS Number、boxed alternative或host coercion混型必须立即拒绝 |
| `U64-01` | `0`、`long.MaxValue+1`、`ulong.MaxValue`作为`WorldSeed64`经script field、canonical codec、commit/reopen/fork、equality/hash与host RNG调用后保持同一64-bit pattern；任何script arithmetic或signed/float隐式混型立即拒绝 |
| `ORD-01` | insert/update/delete/reinsert的exact golden order与value sequence一致 |
| `ORD-02` | `ORD-01`在不同insertion order、fresh process、force GC、abort/reopen和fork后保持一致 |
| `ID-01` | `primaryActor`与`actors[A.id]`引用同一instance；A↔B和Item alias在GC/reopen/fork后保持 |
| `TRN-01` | ordinary mutable object/table/array、closure/function、fiber/coroutine、weak/native/host handle写入direct field立即失败 |
| `TRN-02` | `TRN-01`同样覆盖list element、map key、map value和nested durable object |
| `BYP-01` | 每门语言列出并实测raw/property/metaprogramming旁路，例如Lua `rawset/next`、JS descriptor/Proxy/internal slot；不得绕过schema、dirty或transient barrier |
| `QTA-01` | infinite loop在deterministic instruction budget终止；candidate realm不可继续使用partial state |
| `QTA-02` | allocation bomb、deep recursion/stack、output flood与host cancellation分别被硬限制，且恢复exact parent |
| `TXN-01` | 第一字段写入后exception、validation failure、host callback fault与budget abort都commit nothing并恢复exact parent |
| `EFF-01` | PendingEffect commit确认后、dispatch前crash：reopen仍看见同requestId，可安全dispatch |
| `EFF-02` | dispatch后、result前crash：不得生成第二requestId；外部idempotency或inbox查询能恢复 |
| `EFF-03` | result取得后、transaction B commit前crash：result以requestId重新输入，不依赖suspended stack |
| `EFF-04` | transaction B publication ambiguous：poison/reopen后只接受exact pending parent或exact consumed child |
| `VER-01` | wrong VM/ABI/code bundle/ClassId/schema fail closed；field-add migration生成新branch且source commit不变 |
| `TOOL-01` | compile/load/runtime error含source path与span；generated host declarations能在执行前捕获一次API rename/type mismatch |
| `SCH-01` | 新增`armor:Int64`及旧commit migration时，业务作者只修改一份authoritative script class/schema source与tests；native bindings、stubs与metadata只能确定性生成，禁止每字段手写C/C++/Rust/C#；记录handwritten/generated LOC与semantic duplicate |
| `AGT-01` | P2前由一个未读VM内部源码的Coding Agent完成`armor`+invariant/migration并修复一次host API rename；只能使用项目public docs、format/check/compile/test反馈，记录成功率、迭代数与unsupported-feature hallucination |

### 6.3 Evidence phases

| Phase | 统一验证内容 |
|---|---|
| P0 Paper/source audit | pin、license、official build/tests；定位value、allocation、field get/set、collection、GC、call/return、interrupt与embedding源码 |
| P0 micro-gate | 任何被激活candidate的G1/G2/G3/G7/G9 unknowns或廉价可验证项必须先关闭；reserve在被激活时也不得跳过；失败即停止 |
| P1 RealmStore red spike | 不接真实StateJournal；接最小in-memory transactional/versioned RealmStore：stable ObjectId、dirty set、immutable CommitId/parent与branch refs；脚本字段直接命中store。test host在session disposal后保留commits，并可script exact-parent/exact-child ambiguous outcome；它不实现durable file format、object delta/rebase或真实publication layer |
| P1 graph | cycle、shared alias、new object、ordered map、force GC；transient escape即时拒绝 |
| P1 transaction | one invocation；empty-stack commit；第一字段改写后异常、validation failure与budget abort均恢复exact parent |
| P1 external effect | 以deterministic parent/candidate/ref fault injection覆盖`EFF-01`至`EFF-04`四个logical crash window；pending/outbox先commit，再dispatch，再由第二次调用消费result；真实durability留给P2 |
| P1 code/schema | pin source hash + ClassId + schema；wrong binding fail closed；做一次显式field-add migration |
| P1 schema-authoring | 执行`SCH-01`；拒绝为fixture手写每字段native binding后宣称体验成立 |
| P1 tooling / early Agent task | clean build、format/check/test、source-span diagnostic、stack trace、host API stubs；执行`AGT-01`后才可选择P2 |
| Typed control | AngelScript普通对象图 + reflection matcher；记录full scan、stable-ID、failure recovery与schema ceremony，不冒充native realm |
| P2 StateJournal fusion | 只给P1前两名：object-level delta/rebase、reopen、same-repo fork、detach、Published/MayHavePublished fault |
| P3 Agent A/B | 同prompt/tool budget完成字段+migration、identity-safe transfer、nondeterminism bugfix，比较成功率与错误类别 |

AngelScript与Koto的“strictly time-boxed micro-gate”采用共同默认上限：每个candidate累计不超过8小时agent/engineer execution、500行handwritten非测试spike代码、一个host platform executable slice；vendored/generated code不计LOC但必须记录。开始前的work order必须冻结具体问题、预算和expected evidence；任一上限先到即停止，保留tests/notes并标记`DEFERRED-BUDGET`，不视为PASS、不得进入P1。只有发现会改变所有candidate共同contract的新事实时，才可先修订本章程后重新批准预算，不能在原work order内滚动续期。

### 6.4 Agent A/B任务

P3的每个Agent都从同一份`pre-AGT-01` frozen baseline独立开始；P1为`SCH-01/AGT-01`生成的`armor`实现不进入该baseline，避免任务退化成no-op或让后执行者看到前一个Agent的答案。

1. 新增`armor:Int64`，并为旧commit写显式migration；
2. 新增`transfer(item, from, to)`，保持alias、owner与inventory一致；
3. 修复一个故意注入的unordered iteration bug；
4. 改一次host API名称/类型，要求静态或load前diagnostic定位全部调用；
5. 根据runtime stack/source span修复一次transaction fault。

记录：

- 是否一次成功；
- format/check/compile/test迭代次数；
- 修改文件与semantic duplicate数量；
- unsupported feature hallucination；
- 是否必须阅读VM C/C++/Rust源码才能修改普通domain code；
- fault/reopen后是否保留exact parent。

## 7. 首轮候选审计

### 7.1 Tier A：paper-primary，GO P0 only

#### PUC Lua 5.5.1

官方来源：[Lua 5.5 manual](https://www.lua.org/manual/5.5/manual.html)、[5.5.1 source](https://www.lua.org/ftp/)、[license](https://www.lua.org/license.html)、[official mirror](https://github.com/lua/lua)。

优势：

- standard build原生signed 64-bit integer + double；
- 小型、成熟、MIT、ISO C、无mandatory JIT；
- full userdata、metatable、uservalue和成熟C API适合先实现canonical durable instance；
- custom allocator、count hook、host-selected libraries；
- Lua语法、手册与Coding Agent先验丰富；LuaLS/LuaCATS是必须实测的外部authoring sidecar；
- 若extension seam成功，继续深入table/value/GC源码仍具可审计性。

硬约束：

- build/startup断言`lua_Integer`恰为signed 64-bit，禁用`LUA_32BITS`；
- Lua primitive integer overflow默认wrap；若不能用branded checked value/operator、bytecode verifier或等价结构阻止durable Int64进入raw arithmetic，G2失败；explicit helper convention本身不合格；
-普通table枚举顺序未定义；authoritative map只能使用`DurableOrderedMap`；
- ordinary table/function/thread/lightuserdata不能进入durable closure；
- 不加载`io/os/debug/package/coroutine`等ambient surface；
- .NET需要repo-owned窄C ABI shim + `LibraryImport/PInvoke`。

Paper verdict：**PAPER PRIMARY；GO P0 integration-floor micro-gates，不保送P1。**

#### mruby 4.0.0

官方来源：[mruby repo](https://github.com/mruby/mruby)、[architecture](https://mruby.org/docs/api/file.architecture.html)、[build configuration](https://mruby.org/docs/api/file.mrbconf.html)、[mrbgems](https://mruby.org/docs/api/file.mrbgems.html)、[4.0 release](https://mruby.org/releases/2026/04/20/mruby-4.0.0-released.html)。

优势：

- Ruby class/method/instance语法与领域OOP自然贴合；
- `MRB_INT64`可固定exact signed Int64；
- `RObject/RClass/RArray/RHash/RData/RProc/RFiber`对象种类明确；
- incremental mark/sweep、register VM、可嵌入、MIT；
- `RData` + mrbgem可先实现`Durable::Object/List/OrderedMap`，以后仍有深入VM object kind的路径；
- Ruby/RBS/LSP可作为待验证的authoring sidecar，`mrbc/mrbtest`作为runtime truth。

风险：

- mruby是Ruby subset；standard Ruby LSP/gems可能产生错误假设；
- integer width、boxing、gembox与build flags必须固定；
- 必须实测并结构化拦截native integer overflow；只有field-write range check或helper convention不能通过G2；
-普通Hash顺序不能直接成为persistent protocol；
- fuel/hard memory quota与C# Windows build闭环需要实测；
- `RData`若长期只包host token，可能停留在foreign proxy而没有深入native realm。

Paper verdict：**PAPER PRIMARY；GO P0 class-first micro-gates，不保送P1。**

#### QuickJS 2026-06-04

官方来源：[QuickJS repo](https://github.com/bellard/quickjs)、[manual](https://bellard.org/quickjs/quickjs.html)、[C API](https://github.com/bellard/quickjs/blob/master/quickjs.h)、[license](https://github.com/bellard/quickjs/blob/master/LICENSE)。

优势：

- standard JavaScript runtime配合TypeScript `checkJs`、`.d.ts`与Language Service authoring sidecar，Coding Agent先验最强；
- native JSClass、opaque pointer与exotic property hooks；
- custom allocator、memory/max-stack limit与interrupt handler；
- BigInt可精确承载Int64；
- MIT、小型C实现、无mandatory external dependency。

硬风险：

- durable Int64字段必须是`bigint`；runtime与generated `.d.ts`都必须拒绝`number`；
- 必须给durable Int64提供checked branded/operator或等价编译约束；原生BigInt越界后继续执行、直到写field才range-check不能通过G2；
- prototype、descriptor、Proxy、Promise、WeakRef、typed arrays与jobs扩大subset封闭面；
- V1应使用`JS_NewContextRaw`装白名单intrinsics，禁用eval/Promise/Weak/Date/std/os/native modules；
- turn结束必须zero pending jobs；
-官方binary JSON/bytecode格式不稳定或与具体版本绑定，不能作save authority；
- Windows clean native build与C# ownership/exception边界必须实测。

Paper verdict：**PAPER PRIMARY；GO P0 tooling-ceiling micro-gates，不保送P1。**

### 7.2 Typed control 与 conditional reserve

| Candidate | 角色 | 进入下一阶段的门 |
|---|---|---|
| [AngelScript](https://github.com/anjo76/angelscript) | typed graph-match control + G4 challenger | 先做与primary同尺度的time-boxed G4 source/red micro-spike；若ordinary typed instance/member/ref能集中direct-store，则晋升paper-primary pool、补齐完整P0后竞争P1；若只能reflection/reconstruction，则固定为control并量化full-scan、stable-ID与failure recovery |
| [Koto](https://github.com/koto-lang/koto) | activated deep-fusion challenger | 在锁定P1席位前完成严格time-boxed G4/G7/G9/C# ABI micro-gate，并证明checked Int64、`IndexMap` canonical contract、`PtrMut` dirty choke point、memory quota与Rust panic containment；通过则晋升paper-primary pool并补齐完整P0，不通过即停止 |
| [Luau](https://github.com/luau-lang/luau) | typed Lua reserve | pin exact release并证明integer literal/operator/key/C API/typechecker全路径；若脚本易误入double `number`，淘汰 |
| [Squirrel](https://github.com/albertodemichelis/squirrel) | native-heap fork reserve | 固定x64 `SQInteger`；验证fixed field slots、class freeze、array/table write barriers、cycle GC与tooling；只在Tier A都无法深入时启用 |
| [Gravity](https://github.com/marcobambini/gravity) | compact class/ivar VM reserve | 先固定可维护upstream与release；再验证exact Int64、map order、field/GC mutation choke point、interrupt/quota和Agent tool loop；没有决定性优势则不占实现席位 |
| [MicroPython](https://github.com/micropython/micropython) | Python authoring reserve | 固定x64 embed port、long-int config、ordered durable type、feature matrix与`.pyi`；实际runtime必须否定CPython-only假设 |
| [JerryScript](https://github.com/jerryscript-project/jerryscript) | compact JS reserve | BigInt、halt handler、fixed heap、TS output compatibility与native class通过同一micro-gate |

AngelScript官方[serialization](https://www.angelcode.com/angelscript/sdk/docs/manual/doc_serialization.html)与[reflection](https://www.angelcode.com/angelscript/sdk/docs/manual/doc_adv_reflection.html)非常接近graph-match问题，但paper evidence只能证明这条seam存在，不能提前证明它没有direct durable-object路径。P0先给它与primary同尺度的G4 micro-spike；只有micro-spike仍落在commit-time reconciliation时，才固定为小型control而不占primary名额。

Koto官方runtime把`KValue`区分为i64/f64/object/function/iterator等，`KMap`使用有序`IndexMap`，`PtrMut`是很有价值的集中mutation seam；官方还有[`koto-ls`](https://github.com/koto-lang/koto-ls)与CLI formatter、VM execution limit。它仍缺hard memory quota与稳定C# ABI，type hints也不是成熟static checker；必须先证明architecture fit真的能抵消语言/Agent语料与interop税。[KValue](https://docs.rs/koto_runtime/latest/koto_runtime/enum.KValue.html)、[KMap](https://docs.rs/koto_runtime/latest/koto_runtime/struct.KMap.html)。

### 7.3 Paper reject / negative control

| Candidate | 本轮裁决 | 决定性原因 |
|---|---|---|
| [MoonSharp](https://github.com/moonsharp-devs/moonsharp) | API-only throwaway control | 纯C# interop极好，但官方CLR mapping警告Int64→double可能静默丢精度，G2失败 |
| [LuaJIT](https://github.com/LuaJIT/LuaJIT) | reject | exact Int64依赖FFI cdata；FFI/native pointer与sandbox目标冲突，JIT不是当前价值 |
| [Wren](https://github.com/wren-lang/wren) | reject first round | only-double、safe integer止于`2^53-1`、Map order未定义、无可靠public fuel/LSP；修补后成为私有方言 |
| [Duktape](https://github.com/svaarala/duktape) | reject | legacy ES、无BigInt、timeout hook非可靠security boundary、维护节奏较低 |
| [MuJS](https://mujs.com/) | heap research only | VM小，但only-double、无BigInt与可靠instruction interrupt；不满足产品gate |
| [Hermes](https://github.com/facebook/hermes) | reject | RN/AOT/IR/bytecode/Hades GC/JSI体量远超收益 |
| [Boa](https://github.com/boa-dev/boa) | paper reserve only | Rust native class与modern JS有吸引力，但experimental API、hard heap cap和C ABI仍不确定 |
| [RustPython](https://github.com/RustPython/RustPython) | reject as main fork | Python surface/async/compiler/stdlib过大，官方production readiness不足，Rust安全不减少semantic surface |
| CPython / Ruby MRI | negative controls | 强工具与生态恰好带来不可封闭的native extension、frame、GC、thread和compatibility surface |
| [Rhai](https://github.com/rhaiscript/rhai) | stock-proxy control only | AST evaluator和value container模型不提供可直接深融的canonical cyclic VM heap |
| [Rune](https://github.com/rune-rs/rune) | defer | Rust-like工具较好，但unordered Object与AnyObj/borrow体系扩大改造面 |
| [Janet](https://github.com/janet-lang/janet) | VM reference only | C embedding/GC清楚，但boxed Int64、unordered table、macro/fiber/FFI和较低Agent熟悉度 |
| Perl | reject | SV/AV/HV、magic、XS、mortal stack、context-sensitive semantics与低Agent可靠熟悉度都不匹配 |

## 8. 执行顺序

### 8.1 P0：固定证据

当前三个paper-primary直接执行完整P0。AngelScript G4 challenger与Koto deep-fusion challenger已明确激活，先只执行各自严格time-boxed的micro-gate；通过者晋升paper-primary pool，并且必须补齐同一套exact pin/license、Windows/Linux clean build、official tests与全部unknown hard gates，才能获得P1资格。Luau、Squirrel、Gravity等reserve仅在以后明确激活后执行表中micro-gate，MicroPython、JerryScript等未激活reserve不自动扩张本轮范围。

当前P0对象需要：

- 固定exact tag/commit，不以`main/master`作实验authority；
- 保存license/NOTICE；
- Windows/Linux clean build；
- 跑官方tests；
- 记录value、object、collection、GC、call/return、interrupt和embedding的源码choke points；
- 记录generated artifacts、native binaries、transitive dependencies与升级步骤。

### 8.2 P1：最多三个RealmStore red spikes

PUC Lua、mruby、QuickJS以及任何由challenger晋升的candidate，都只有在完整P0与unknown hard gates关闭后才有资格进入P1；未关闭者直接失去资格。最多三个P1席位按可执行证据、hypothesis覆盖与实现成本分配，不按本文列举顺序保送QuickJS或任何challenger；若AngelScript只证明graph matching，它转入独立control且不占席位。所有P1共用同一fake RealmStore contract、domain fixture、host API、normative test IDs、faults与assertions，不得各自修改题目来绕过弱点。

P1可以使用官方native userdata/class extension seam，但必须满足：

- language-side domain object就是唯一canonical realm instance；
-字段操作直接读写RealmStore，不在commit时复制POCO graph；
- raw transient objects不能混入durable closure；
- 同ObjectId重复访问保持instance identity；
- candidate声明采用host-backed proxy还是VM-native object kind，并给出allocation/get/set/ref/GC source map、patch面与维护成本；proxy无需承诺未来必然下沉，但不得有mutable shadow state或per-field handwritten native code。

### 8.3 Typed control

若P0 G4 micro-spike只证明graph matching，AngelScript control独立计量，不与native-realm P1共享winner席位。它回答：

> 如果普通typed object graph + reflection matcher已经足够简单，我们是否根本不需要native durable realm？

若control明显更简单且failure/reopen没有双authority问题，可以重裁目标；不得为了维护原结论而忽略反证。

### 8.4 P2：最多两个真实融合

按P1 executable evidence选择最多两个候选，把StateJournal技术直接重构进RealmStore：

- stable ObjectId与identity map；
- per-object dirty tracking；
- rebase/delta/version chain；
- root reachability与branch-aware retention；
- commit/reopen/same-repo fork；
- historical readonly与detach；
- typed publication outcome、poison/reopen/exact parent-or-child；
- code/schema/VM ABI binding；
- external effect两阶段turn。

此阶段仍是独立research或test-only integration，不授权DramaBoard production cutover。

### 8.5 P3：Agent A/B与最终裁决

只有P2通过后，才让多个Coding Agent在同等context/tool budget下执行第6.4节任务。最终winner必须同时证明：

- storage/domain duplicate真的减少；
- standard tooling或高质量project tooling可用；
- Agent不频繁误用transient language features；
- schema/rule变化主要落在一份script class和tests；
- failure、branch、determinism与migration没有退化。

## 9. Stop rules

| 触发条件 | 动作 |
|---|---|
| 任一hard gate失败 | 淘汰；不得用stars、性能、生态或低LOC补偿 |
| red spike只能commit-time graph conversion | 不作为native-realm winner；可保留为graph-match control |
| exact Int64依赖作者自律，不受schema/operator强制 | 淘汰 |
| deterministic order只能在commit/export排序 | 淘汰 |
| central empty-stack safepoint无法证明 | 淘汰 |
| transaction failure后realm仍可继续使用partial state | 淘汰；必须abort/poison/reopen |
| 开始持久化Fiber/closure/host handle来完成domain state | 停止；已经偏离章程 |
| 一次field write必须同时侵入parser/compiler/VM/stdlib且无stable choke point | 停止该candidate |
| candidate需要长期保留C# reducer与script mutation双authority | 停止；killer value未兑现 |
| Agent必须读VM native源码才能修改普通domain rule | P3失败 |
| top candidate只有文档证据，runner-up有full probe | 不得靠纸面分数选top；先补同级证据 |
| 两个candidate差异小于证据不确定性 | 保留二者并增加单项discriminating probe |
| 所有已分配P1席位完成统一probe，且其中两个candidate完整通过P2 | 停止扩大海选；不得因按顺序前两个P0/P1通过而跳过已获席位的tooling/deep-fusion hypothesis |
| 无candidate通过 | 接受`no winner`；回到C# generator、stock proxy或自建小语义核，不下调gate |

## 10. 尚未裁决的产品选择

- Script code是否只来自trusted content pack，还是未来允许untrusted mod；后者可能要求out-of-process isolation。
- 是否在后续protocol version中增加unchecked/wider numeric type；V1 durable Int64已经固定为checked transaction error。
- 是否另增insertion-ordered collection；V1 `DurableOrderedMap`已经固定为canonical key order，不能因候选native map行为而改变。
- source bundle如何自包含、签名、pin与迁移；bytecode是否只作cache。
- realm是每branch一个VM还是每invocation重建VM；需以measurements裁决。
- semantic event payload需要多完整；当前至少允许每transaction附一条lossy summary或typed event metadata，但不承诺event replay completeness。
- deep integration最终采用“transient heap + durable realm”还是让更多ordinary VM instance变durable；P1/P2 evidence决定，不预设答案。
- 与C# host的native ABI、crash isolation和deployment packaging；每个native candidate都必须给Windows x64可复现方案。

## 11. 当前最佳下一步

先不要fork三个VM，也不要把当前 StateJournal 移植三遍。

下一项工作应是先创建候选无关的 `RealmStore` red-spike contract、normative test manifest与fixture，然后按下列paper-primary顺序关闭P0并决定是否进入P1：

```text
PUC Lua 5.5.1
→ mruby 4.0.0
→ QuickJS 2026-06-04
```

每个candidate只做到当前gate所需的最小代码。AngelScript先做G4 challenger micro-spike，不能在paper阶段被预设为只有graph matcher；若结果确为matcher，control随后严格限制为普通typed graph。Koto的G4/G7/G9/C# ABI micro-gate已经激活，必须在锁定P1席位前完成；两种challenger通过micro-gate后都只获得“补齐完整P0”的资格，不可直接进入P1，也不提前建设完整adapter。Luau、Squirrel、Gravity只有在以后明确激活时才做各自micro-gate/source proof。

P1完成前，本文件不授权：

- 新建production Script Runtime；
- 修改DramaBoard domain authority；
- 重写0021/0022裁决；
- 把任一paper favorite写成正式dependency；
- 用candidate自带snapshot/bytecode格式冒充persistent realm。
