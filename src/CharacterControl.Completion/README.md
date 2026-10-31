# Shared character model invocation

`LlmDecisionLoop` owns the operational tool loop. It borrows an `ICompletionClient`; the composing host owns client lifetime and connection configuration. One loop can serve different characters sequentially. Call/protocol budgets are host state, not character memory.

Each call supplies a `LlmDecisionInput`: actor identity, positive context sequence, already-rendered actor-visible material and game-specific policy. The loop supplies the `act`/optional `report-feedback` protocol. It returns `CommandSubmission`, preserving provider tool-call identity and assigning an operation identity. A returned submission is not a successful game operation: the game still parses, validates, records and applies it.

The optional feedback sink must durably record feedback before returning its receipt. The loop then retains the native assistant message and matching tool result within this decision only. Subsequent decisions start without this Tail. Trace callbacks expose actual request/response identities to the host; they do not publish game state. Host failures must not be translated into invented game actions.

The game must publish a frozen decision boundary before invoking the loop if it needs Gym fork/recovery. Renderers that hold presentation state need their own sequential protection; `ResourceCycle.Completion/LlmCharacterController.cs` is one strongly typed adapter example. Shared transport material does not replace a game's persistent Context or visibility checks.

Pure cross-game input tests live in `tests/CharacterControl.Completion.Tests`. The original game's adapter and native feedback regression tests remain in `tests/ResourceCycle.Completion.Tests`.
