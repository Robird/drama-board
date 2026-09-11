# DramaBoard documentation

Start with the repository's [current project state](../PROJECT-STATE.md). It identifies the active goal, source anchors, and unresolved decisions. This index classifies documents; it does not replace that state.

## Current design

- [Simulation Kernel](design/simulation-kernel.md): the governing occurrence, time, and publication law.
- [Graph Spatial World](design/graph-spatial-world.md): the current objective-space model and its boundary with FirstBoard.

## Current implementation boundaries and evidence

- [Kernel occurrence baseline](implementation/kernel-occurrence-baseline.md): the implemented Kernel semantics and acceptance boundary.
- [Game content and Save boundary](implementation/game-content-save-boundary.md): the current-format Save direction; it is a target boundary, not a DurableGraph integration claim.
- [TravelTo](build-log/0001-travel-to.md), [passage encounter](build-log/0002-passage-encounter.md), and [live-session playback](build-log/0003-live-session-playback.md): implemented vertical-slice evidence.

## Active research

- [Player spatial knowledge](research/player-spatial-knowledge.md): frozen Getter seam and deferred fog-of-war work.
- [Persistent Script VM selection](research/persistent-script-vm-selection.md): research charter; selection and production migration remain deferred.
- [EventJournal + StateStore draft](research/event-journal-state-store-draft.md): independent event/state graphs, resumable processing, and the delivered EventHistory capability check; editable consumer contract.
- [DurableGraph consumer preflight](research/durablegraph-consumer-preflight.md): consumer closure, validation scenario, and earlier integration alternatives.

## Consumer feedback

- [DramaBoard → DurableGraph](feedback/durablegraph/README.md): concrete API and feature feedback, evidence, upstream responses, and follow-up validation.

## Historical material

[Archive](archive/README.md) preserves retired experiments and early work records. Its `legacy/` subtree is deliberately excluded from default local text search; use `rg --no-ignore -n "keyword" docs/archive/legacy` when historical evidence is required.
