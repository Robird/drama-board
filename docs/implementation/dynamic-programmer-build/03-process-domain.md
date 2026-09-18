# R3 · Server/FreePlay/Runtime：Process 领域

状态：未开始。依赖：R1。产物：`src/Server/FreePlay/Runtime/`、`src/Server/Properties/AssemblyInfo.cs`、`tests/Server.Tests`。

## 目标

实现纯 Process 领域：不可变 Buffer、互斥执行状态、七种 Process facts 与单一 reducer。不接 Kernel、不接 Session（集成在 R5/R6/R7）。

## 前置阅读

- [机制模型](../../design/player-runtime/dynamic-programmer.md) §4（顺序指令与执行边界）、§4.1（Stay）、§5.3（默认 Stay 与防重入）
- 本目录 [README](README.md) 的"提交语义总表"与裁决表

## 可见性

全部类型 `internal`，命名空间 `DramaBoard.Server.FreePlay.Runtime`。新增 `src/Server/Properties/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("DramaBoard.Server.Tests")]
```

## 类型清单

```csharp
public sealed class InstructionBuffer
{
    public static InstructionBuffer Empty { get; }
    public static InstructionBuffer Of(IEnumerable<Instruction> instructions);  // 冻结快照、元素非 null
    public bool IsEmpty { get; }
    public Instruction? Head { get; }          // 空则 null
    public InstructionBuffer Tail();           // 要求非空，否则 InvalidOperationException
    public IReadOnlyList<Instruction> Items { get; }
    // 值相等按元素序列；不做 [Stay] 规范化，允许空。
}

public abstract record ProcessMode;
public sealed record ReadyProcess : ProcessMode;
public sealed record AwaitingProgrammerProcess(ProgrammingReason Reason) : ProcessMode;
public sealed record RunningMoveProcess(long MovementGeneration) : ProcessMode;   // >= 0
public sealed record RunningThinkProcess(long CompletesAtMs) : ProcessMode;       // > 0
public sealed record RunningStayProcess : ProcessMode;

public sealed record PlayerProcessState(
    SubjectiveWorkspace Workspace,
    InstructionBuffer Pending,
    ProcessMode Mode,
    long ControlRevision);   // >= 0
public static class PlayerProcessStateGenesis
{
    public static PlayerProcessState Create() =>
        new(new(""), InstructionBuffer.Empty, new AwaitingProgrammerProcess(ProgrammingReason.MissingInstruction), 0);
}

public abstract record FreePlayProcessFact;
public sealed record ProgramAcceptedFact(IReadOnlyList<Instruction> Program, SubjectiveWorkspace Workspace)
    : FreePlayProcessFact;   // Program 非空、冻结（空程序不允许进入 fact——规范化在上游完成）
public sealed record MoveStartedFact(long MovementGeneration) : FreePlayProcessFact;
public sealed record MoveArrivedFact(long ExpectedMovementGeneration) : FreePlayProcessFact;
public sealed record MoveBlockedFact(string PassageId) : FreePlayProcessFact;   // 非空白
public sealed record ThinkStartedFact(string Note, long DurationMs) : FreePlayProcessFact;   // Note 非空白、DurationMs > 0
public sealed record ThinkCompletedFact : FreePlayProcessFact;
public sealed record StayStartedFact : FreePlayProcessFact;
```

facts 是普通 record（本批内存运行，不加 DurableType；持久化闭包出现时另行设计）。

## Reducer 转移表

`PlayerProcessReducer.Apply(PlayerProcessState state, LogicalInstant instant, FreePlayProcessFact fact) -> PlayerProcessState`。前置不满足时抛 `InvalidOperationException`（fold 失败 → 零提交）。每条 fact 折叠后 `ControlRevision` 恰好 +1（checked）。

| Fact | 前置 | 状态变化 |
|---|---|---|
| ProgramAccepted(program, workspace) | Mode 为 AwaitingProgrammer；program 非空 | Pending = program；Workspace = workspace；Mode = Ready |
| MoveStarted(gen) | Mode 为 Ready 且 Head 是 MoveInstruction | Mode = RunningMove(gen)；Pending = Tail() |
| MoveArrived(expectedGen) | Mode 为 RunningMove(g) 且 g == expectedGen | Mode = Pending 非空 ? Ready : Awaiting(MissingInstruction)；Workspace 不变 |
| MoveBlocked(passageId) | Mode 为 Ready 且 Head 是 MoveInstruction | Mode = Awaiting(MoveBlocked)；**Pending 不变（头部保留）** |
| ThinkStarted(note, durationMs) | Mode 为 Ready 且 Head 是 ThinkInstruction | Mode = RunningThink(instant.ModelTime.Ticks + DurationMs)；Pending = Tail()；Workspace = AppendNote |
| ThinkCompleted | Mode 为 RunningThink(c) 且 instant.ModelTime.Ticks == c | Mode = Awaiting(ThinkCompleted) |
| StayStarted | Mode 为 Ready 且 Head 是 StayInstruction | Mode = RunningStay；Pending = Tail() |

笔记追加格式（固定，无本地化）：

```text
AppendNote(text, instant, note) = text.Length == 0
    ? $"[{instant.ModelTime.Ticks}] {note}"
    : $"{text}\n[{instant.ModelTime.Ticks}] {note}"
```

## 不变量校验

`PlayerProcessValidator.Validate(PlayerProcessState)`：

- Mode 为 Ready ⇒ Pending 非空（其余 Mode 对 Pending 无要求）；
- ControlRevision ≥ 0；RunningMove 的 generation ≥ 0；RunningThink 的 CompletesAtMs > 0；
- Workspace 非 null。

跨域不变量（traversing ⇔ RunningMove）**本轮不加入**：R4–R6 期间旧决策路径仍在驱动世界，会在途时让 Process 停留在 Awaiting；该不变量在 R7 切换时随新规则一起激活。

## 测试清单（Server.Tests，新文件）

- Buffer：Empty/Of/Head/Tail/相等语义；空 Buffer Tail 抛错；Of 冻结源列表。
- 转移表逐行：合法转移的 Mode/Pending/Workspace/Revision 断言；每条 fact 的非法前置（错 Mode、错头部指令种类、gen 不匹配、ThinkCompleted 时刻不符、ProgramAccepted 空程序）抛错且零状态变化。
- `[Stay, Move(cd)]` 折叠 StayStarted 后 Pending 仍含 `Move(cd)`；`[Stay, Think(note)]` 同理且工作区不含该笔记（尾部不执行、不提前记录——机制 §7.2"保留安排"行）；`[]` 不可能进入 fact（构造即抛）。
- ThinkStarted 追加格式（空工作区与已有文本两种）；连续两次 Think 的笔记按时间戳分两行。
- ControlRevision 连续折叠单调 +1。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；FreePlayScene/Session 行为零变化。
```

## 禁止事项

- 不建第二套指令定义（直接内嵌 R1 的 Protocol 类型）。
- reducer 不读 Spatial、不读 EffectiveTime（ThinkStarted 的完成时刻来自 batch instant 参数）。
- 不实现"缺指令自动补 Stay"之类的全局规则——默认 Stay 只发生在 R6 的接受步骤。
