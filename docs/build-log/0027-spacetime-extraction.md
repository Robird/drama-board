# Spacetime engine extraction acceptance

## Step 1 — application archival, old dependency

Accepted on Windows, 2026-10-27. The original tracked tree, including the approved task brief, is commit `57920784a5ca7dd33d8d359e2244d32e249a9482`, tagged locally as `archive/player-runtime-20261027`. [The archive index](../archive/player-runtime.md) gives restoration instructions and the 98-file manifest. Source bytes are preserved; dedicated Markdown links are redirected for their archived locations. The earlier FirstBoard archive is unchanged.

Both solution files now list only Kernel, Spatial, Host and their three test projects. CI uses the same core build/test path, without Node, Server publication or Chromium. The package pin remains `0.0.0-dramaboard.20260912.4cea773.1` for this acceptance. No production core source or schema history changed.

Executed from the repository root:

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.Local.slnx --no-build --no-restore -m:1
```

The preparation script verified and reused the nine frozen packages and their hashes. Rebuild completed with zero warnings/errors; Kernel's 7 and Spatial's 24 schema history records matched. All 175 tests passed (Kernel 114, Spatial 59, Host 2), with no skips.

[SpacetimeHostScenarioTests](../../tests/Spatial.Tests/Acceptance/SpacetimeHostScenarioTests.cs) combines the three production libraries without Player, Protocol or Server. Two traversing entities on a length-11 road meet at floor(11/5)=2; a strict interior reversal at time 3 replaces one movement generation and invalidates its old arrival at time 4. Both entities reach A at time 6, including ceil(11/2). Assertions cover contact consumption, final locations, same-time causal ordering under permuted rule/entity inputs, and replay. Existing tests retain broader same-time arbitration, atomic transitions and pending E/S recovery coverage.

This proves the core extraction and in-memory simulation behavior on Windows. It does not claim a Linux or remote CI run, disk restoration, old application recompilation, or MUD integration. Step 2 has its own dependency and model-generation acceptance.
