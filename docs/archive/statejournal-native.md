# StateJournal-native experiments

`StateJournalNative` was a test-only research track. It did not wire StateJournal into the production FirstBoard, Spatial, Runner, or Save paths. The code and its two build logs left the main branch on 2026-09-11 before DurableGraph integration work began.

## Retained conclusions

- Keep business identity and frontier (`LineageId`, `WorldVersion`, logical time) separate from storage commit addresses, branch names, and object-local identifiers.
- Validate the complete Game, Spatial, and prepared private-state transition before publication; expose neither a batch intermediate nor a partially installed world.
- When publication is ambiguous, reconcile only against the exact expected parent and candidate child. Contradictory evidence fails closed.
- Reopen from a complete validated state. Presentation consumes only the exact committed suffix after that baseline and must not synthesize old cues.

These are design lessons, not a claim that DurableGraph already provides forks, rewind, portable export, Player persistence, or StateJournal-compatible recovery semantics.

## Preserved evidence and recovery

The complete source, tests, and Build Logs 0021/0022 are fixed by annotated tag `research/pre-durablegraph-20260911`, targeting DramaBoard commit `bd73e64aa78bf225abd62a897d27e41f33469872`. The tag is the preferred recovery reference; the commit is an immutable fallback if the local tag is not carried to another clone.

The experiment history is also represented by commits `8ff97cd`, `d543307`, `8f40a89`, `d772cdb`, `06a5dad`, `bd4a6e9`, `3cecbfb`, `605b179`, `94d49d5`, `0c756ef`, and `5fb3f4a`.

It used Atelia StateJournal at fixed commit `742fcd62e691b6b6acca4113a3ac3638bc7275ba`; use that revision when reproducing its historical results.

To inspect a removed file without restoring it, run `git show research/pre-durablegraph-20260911:tests/FirstBoard.Persistence.Tests/StateJournalNative/UnifiedFirstBoardProbeStore.cs`. To restore the complete experiment into a separate working tree, run `git worktree add --detach <directory> research/pre-durablegraph-20260911`; do not copy its StateJournal dependency into the DurableGraph workstream.

The historical test project's default Atelia path is no longer valid. After checking out Atelia commit `742fcd62e691b6b6acca4113a3ac3638bc7275ba` at an absolute path such as `E:\repos\Atelia-org\atelia-statejournal-742fcd`, run `dotnet test tests\FirstBoard.Persistence.Tests\FirstBoard.Persistence.Tests.csproj -p:AteliaRepositoryRoot=E:\repos\Atelia-org\atelia-statejournal-742fcd` from the restored DramaBoard worktree.
