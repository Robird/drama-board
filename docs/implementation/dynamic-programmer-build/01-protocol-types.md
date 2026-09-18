# R1 · Protocol：指令与程序契约类型

状态：未开始。依赖：无。产物：`src/Protocol`、`tests/Protocol.Tests`。

## 目标

新增 Dynamic-Programmer 边界的纯数据契约：三种指令、主观工作区、Programmer 请求/响应与激活标识。全部为带构造校验的不可变记录；本轮不接入任何生产消费者（现有 `DecisionRequest`/`Intent` 等保持原样，R8 才删除）。

## 前置阅读

- [机制模型](../../design/player-runtime/dynamic-programmer.md) §3（写入边界）、§5（一次响应的生效）、§6（再想想）
- 风格参照：`src/Protocol/DecisionRequest.cs`（FrozenList 与构造校验）、`src/Protocol/StableIdentifiers.cs`（标识类型与长度/控制字符规则）、`src/Protocol/Intent.cs`（ProtocolValue 校验风格）

## 类型清单

全部放 `DramaBoard.Protocol` 命名空间，文件归入现有 Protocol 项目；英文 XML doc 注释，风格与现有文件一致。

```csharp
public readonly record struct PassageHandle   // string Value；非空白、≤256、无控制字符（复用 StableIdentifier 规则）
public readonly record struct ActivationId    // string Value；同上

public abstract record Instruction;
public sealed record MoveInstruction(PassageHandle Passage) : Instruction;
public sealed record ThinkInstruction(string Note) : Instruction;   // Note 非空白、≤4096（ProtocolValue 风格）
public sealed record StayInstruction : Instruction;

public sealed record SubjectiveWorkspace(string Text);              // 仅要求非 null；可为空串；长度不在此层设限

public enum ProgrammingReason { MissingInstruction, MoveBlocked, ThinkCompleted }
// 不设 Location 联合：首批 respond 只在 AwaitingProgrammer 激活，而该状态只在地点出现
//（genesis、到达耗尽、受阻、思考完成）；在途维护属延期项，需要时再扩展协议。

public sealed record KnownPassage(PassageHandle Handle, string EndpointA, string EndpointB,
    long ExpectedDurationMs, bool EnterableFromA, bool EnterableFromB);
// ExpectedDurationMs > 0；EndpointA/EndpointB 非空白；
// EnterableFromA/B 为已知图静态方向位（数据源见 R6：不读世界实时入口状态）

public sealed record ProgrammingRequest(...);
// 字段：ActivationId ActivationId, string ActorId, long ModelTimeMs,
//       ProgrammingReason Reason, string? ReasonDetail,
//       string PlaceId,
//       IReadOnlyList<KnownPassage> KnownPassages,
//       SubjectiveWorkspace Workspace,
//       IReadOnlyList<Instruction> RemainingProgram
// 构造校验：ActivationId/ActorId 已由类型或显式检查保证；PlaceId 非空白；
// KnownPassages 冻结快照（FrozenList.Snapshot）且 Handle 唯一；
// RemainingProgram 冻结快照且元素非 null；Workspace 非 null。

public sealed record ProgrammingResponse(...);
// 字段：ActivationId ActivationId, IReadOnlyList<Instruction> Program, SubjectiveWorkspace Workspace
// 构造校验：Program 冻结快照、元素非 null；可为空列表。
```

要点：

- `Program` 为空列表是**合法完整响应**（规范化为 `[Stay]` 发生在 R6 的接受步骤）；本类型不做任何规范化，也不区分"缺失"与"空"——那是 R7 HTTP 解析层的职责。
- 不为这些类型添加 JSON converter 或序列化支持；HTTP 解析在 R7 的 Server.Web 层以 `DecisionInput` 风格手工完成。
- `SubjectiveWorkspace` 不设长度上限：工作区随 Think 笔记追加自然增长。提交侧上限（R2）只约束 Programmer 发起的增长——`提交长度 ≤ max(65_536, 当前工作区长度)`——系统追加后的内容永远可以原样重交，合法 Think 不会因累计越限而失败。

## 测试清单（Protocol.Tests）

- 三种指令构造校验：空白/超长 handle、空白/超长 note 拒绝；合法值相等语义。
- `SubjectiveWorkspace`：null 拒绝、空串与长文本允许。
- `ProgrammingRequest`：KnownPassages 冻结（外部修改源列表不影响）、Handle 重复拒绝、元素 null 拒绝、RemainingProgram 冻结、PlaceId 空白拒绝。
- `ProgrammingResponse`：空 Program 合法、冻结、元素 null 拒绝。
- 标识类型：`PassageHandle`/`ActivationId` 的空白、超长、控制字符拒绝。

## 验收

```text
dotnet test DramaBoard.slnx
→ 全绿；本轮无任何生产代码引用新类型。
```

## 禁止事项

- 不修改现有 `DecisionRequest`/`Observation`/`PlayerDecision`/`Intent`/`ProtocolKinds`/`StableIdentifiers` 的行为。`PassageHandle/ActivationId` 与 `StableIdentifier` 同属 Protocol 程序集，直接复用其 internal 规则即可，无需可见性改动。
- 把 internal `FrozenList` 从 `DecisionRequest.cs` 迁至独立文件（纯移动、零行为变化）：R8 将删除 `DecisionRequest.cs`，新契约的冻结快照不能连带丢失。
- 不引入 JSON 序列化、不接 HTTP、不动其它程序集。
