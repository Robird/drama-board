# FirstBoard and LLM archive

This is a recovery index, not an active implementation guide. The archived FirstBoard scenario, Demo, persistence adapter, LLM Player, dedicated tests, and old product documents are kept under [`archive/firstboard-llm/`](../../archive/firstboard-llm/).

`manifest.json` records the 163 Git-managed source files, their original paths, archive paths, and matching pre/post SHA-256 hashes. It records pre-archive HEAD `5a7de712ad8de0d8c8357832be0d97b13ad11d78` and a clean tracked worktree. Generated `bin/obj` output is not source evidence and is not included in that manifest.

To recover the original build context, create a separate worktree at the recorded commit, then prepare its fixed DurableGraph packages before building:

```powershell
git worktree add --detach ..\drama-board-firstboard-archive 5a7de712ad8de0d8c8357832be0d97b13ad11d78
```

The active repository retains only the Kernel, Spatial, Protocol, Player, Decision.Validation, Host, and Player.Agency projects plus their tests. It has no active playable scene or disk-resume program.
