# Player runtime and interaction archive

This is a recovery index for the frozen Player, planning and presentation experiment. The active engine is Kernel, Spatial and Host. The split and the subsequent core dependency upgrade have passed the [task brief's](../implementation/spacetime-engine-todo.md) separate acceptance checks. This archive continues to use its original baseline and dependencies.

## Contents

[`archive/player-runtime/`](../../archive/player-runtime/) retains the original directory layout:

- `src/`: Protocol, Player, Decision.Validation, Player.Agency, Server and WebUI.
- `tests/`: the five corresponding .NET test projects; WebUI browser tests remain inside its source directory.
- `scripts/Publish-Server.ps1`: the original application publishing script.
- `docs/`: application design, implementation orders, reviews and historical acceptance records.

The [manifest](../../archive/player-runtime/manifest.json) records 98 Git-managed files, their original and archived paths, and pre-archive SHA-256 hashes. Generated outputs are excluded. Documentation links are redirected for browsing in the new location; original-source hashes and final archive hashes distinguish those edits from the original snapshot. Archived projects are excluded from both active solutions and are not required to compile against future engine versions.

Design reserve entry points:

- [Player runtime and Plan-Maintainer principles](../../archive/player-runtime/docs/design/player-runtime/README.md).
- [World VM, Player Process and Dynamic-Programmer](../../archive/player-runtime/docs/design/player-runtime/dynamic-programmer.md).
- [Dynamic-Programmer implementation orders](../../archive/player-runtime/docs/implementation/dynamic-programmer-build/README.md) and [0026 historical acceptance](../../archive/player-runtime/docs/build-log/0026-dynamic-programmer-cutover.md).
- [Player spatial knowledge](../../archive/player-runtime/docs/research/player-spatial-knowledge.md).
- [Server/WebUI historical acceptance](../../archive/player-runtime/docs/build-log/0025-verification.md) and [static web asset investigation](../../archive/player-runtime/docs/worksets/server-wwwroot-staticwebassets.md).

The original documents retain their dates, conclusions and historical task language. They do not authorize new work. In particular, their Player planning, HTTP sessions, event projections, presentation caches and animation concerns are not engine obligations. Kernel `IOccurrenceHistory`, E/S publication, pending completion and replay contracts remain active.

## Restore the complete original context

The local tag `archive/player-runtime-20261027` identifies commit `57920784a5ca7dd33d8d359e2244d32e249a9482`, the complete tracked baseline before this split. It includes the original solution, dependency preparation, CI, application and documentation paths. The tag is local; no remote publication is implied.

```powershell
git worktree add --detach ..\drama-board-player-runtime-archive archive/player-runtime-20261027
```

Use that worktree's README and fixed package preparation instructions to rebuild the original experiment. Running the archived publishing script directly inside the new archive is not the restoration path: the complete baseline supplies its original core projects and build configuration. The commit hash can also be used if the local tag is unavailable.

The independent [FirstBoard archive](firstboard-llm.md) and its original recovery baseline are unchanged. Search this new unit explicitly with `rg --no-ignore -n "keyword" archive/player-runtime`.
