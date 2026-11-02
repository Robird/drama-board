# Shared character goals

`DramaBoard.Goals` owns immutable, character-authored goal graphs and their command rules. It depends only on DurableGraph; it does not read a world, advance time, choose actions, call a model, or decide that an objective has actually been achieved.

The composing game owns one `GoalSet` per character, supplies positive context/operation sequence numbers, calls `GoalRules.Apply`, and publishes the returned graph and `GoalReceipt` atomically with its interaction record. Rejected operations retain the original graph. `GoalSet.ValidateThrough` checks that a frozen character view does not contain future finish or revision records. Sequence numbers describe the game's interaction order, not model time.

`GoalCommandParser` provides the shared text command protocol. `GoalLimits` keeps the initial bounded graph and text policy shared; content and intention remain character-authored. Focus is an explicit path through an acyclic graph, and links are first-class records with their own reasons. Shared children do not propagate completion to parents.

Call `GoalDurableModels.Register` when composing the game's DurableGraph registry. The module uses `DramaBoard.Goals.*` durable identities; former `DramaBoard.ResourceCycle.Goal*` identities are not silently aliased. The ResourceCycle extraction started a new game revision at that time; the subsequent Playground rename establishes fresh application identities without changing revision 22.

Pure rules and parser tests live in `tests/Goals.Tests`; Playground retains the tests of interaction integration and pending-event recovery.
