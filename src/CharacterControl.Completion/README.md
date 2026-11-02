# Shared character model invocation

`LlmDecisionLoop` owns the operational tool loop. It borrows the selected `ICompletionClient`; the composing host owns the connection pool and configuration. One loop can serve different characters sequentially. Call/protocol budgets are host state, not character memory.

Each call supplies a `LlmDecisionInput`: actor identity, positive context sequence, already-rendered actor-visible material and game-specific policy. The loop supplies the `act`/optional `report-feedback` protocol. It returns `CommandSubmission`, preserving provider tool-call identity and assigning an operation identity. A returned submission is not a successful game operation: the game still parses, validates, records and applies it.

The optional feedback sink must durably record feedback before returning its receipt. The loop then retains the native assistant message and matching tool result within this decision only. Subsequent decisions start without this Tail. Trace callbacks expose actual request/response identities to the host; they do not publish game state. Host failures must not be translated into invented game actions.

The game must publish a frozen decision boundary before invoking the loop if it needs Gym fork/recovery. Renderers that hold presentation state need their own sequential protection. The [Playground adapter](../Playground.Completion/LlmCharacterController.cs) protects rendering and invocation as one serial service. Shared transport material does not replace a game's persistent Context or visibility checks.

## Host connection configuration and pool

`LlmConnectionConfiguration.Load(path, overridesPath?)` freezes the two host files. `GetConnectionId(actorId)` selects an opaque actor ID override or the required default; `ConnectionIds` and `OverrideActorIds` support host validation. The game host checks that actors exist and gives Human control priority. A Human actor may retain a dormant override. See the [two-file contract and examples](../../docs/design/multi-llm-connections.md).

`LlmConnectionPool.GetAsync(id, ct)` lazily returns `LlmDecisionBinding(Client, ModelId, ConnectionId)` and caches by connection ID. The pool owns and disposes clients; the loop and game adapters borrow them. Atelia.Completion parses the embedded V3 catalog and creates provider clients. Credentials/environment values are resolved only for actually used connections. Hosts freeze configuration at startup, complete pending recovery and reconcile evidence before the first model call. Management commands need no client.

The resolver-based `LlmDecisionLoop` fixes one binding for each decision, including its local feedback Tail. One loop shares call and consecutive-failure budgets across actors and connections. The fixed-client constructor remains available for programmatic consumers; externally supplied clients remain caller-owned. There is no hot reload, automatic failover or cross-model native Tail replay.

Connection configuration and credentials are host data, never character memory or game facts. Gym and audit records capture actual actor/model/connection/protocol identities at each call. Resume may select a different configuration without rewriting old evidence. General-purpose provider errors may contain sensitive values; the host controls their presentation.

## Operational boundaries

`LlmControllerOptions` defaults to 128 model calls per instance, 3 consecutive protocol failures, 200,000 UTF-16 code units of rendered material plus local protocol history, and 8,192 code units per action. Oversize material pauses instead of being silently truncated. Feedback defaults to at most 3 successful reports per service and 4,000 combined decoded characters per report. These are process-local budgets, shared across the characters using one instance; restarting creates new budgets.

`LlmControllerPausedException` reports a pause, not a fallback action. Successful feedback persistence precedes the tool receipt; neither feedback nor a model proposal proves that a game effect committed. Native Tail survives only the current service, not cold reopen or another character's decision. There is no generic persistent model session or automatic retry of an external call.

Pure cross-game protocol and connection tests live in [CharacterControl.Completion.Tests](../../tests/CharacterControl.Completion.Tests). Game-specific adapter tests live in [Playground.Completion.Tests](../../tests/Playground.Completion.Tests).
