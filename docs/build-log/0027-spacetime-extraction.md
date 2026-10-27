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

## Step 2 — current DurableGraph packages and generated models

Accepted on Windows with SDK 10.0.201, 2026-10-27. [Package source](../worksets/durablegraph-package-source.md) owns the exact G/S pins and reproduction procedure. DG source is `c549124f1db53fe028f4640ddcafd8f338bb076b`, G is `0.0.0-dramaboard.20261027.gc549124.1`; Storage source is `70b406cd8d1117733eee81a33e7f978399aa98b2`, S is `0.1.3-dev.20261019.g70b406c`. The official six-package Storage preparation and eleven-package DG candidate export ran successfully. Both upstream working trees remain unchanged.

Preparation's default reuse path passed identity, source revision and package SHA256 checks. Negative checks rejected a copied receipt with a modified package hash, and rejected a fresh output/checkouts request for G already present in the fixed consumer cache before creating any worktree. Exact SDK selection applies to both producer stages. The feed and receipt are ignored local outputs, reproduced by the checked-in script; no packages or tags were published remotely.

For the first consumer upgrade, all six active projects' old bin/obj directories were moved into `artifacts/pre-dg-upgrade-20261027-102345/`, preserving their old outputs while excluding stale generator manifests. The same Rebuild/Verify and full test commands shown above then passed with the new source, explicit NuGet.Config and isolated consumer cache:

- Compilation: zero warnings/errors; production Kernel/Spatial/Host source and package-reference boundaries unchanged.
- Generation/schema: Kernel 7 and Spatial 24 records verified exactly, with no committed schema changes. Generated registrations work through the existing public registration methods.
- Behavior: all 176 tests passed, no skips (Kernel 114, Spatial 60, Host 2). This includes the unchanged 175-test old-package baseline and one new real-model qualification test. The no-Player Host scenario retains the same event order and final state.
- Package consumption: all six project.assets.json files select the fixed versions and repository-local cache. All eleven unique Atelia/K4os cached nupkg files match receipt/feed SHA256; their restore metadata identifies the new candidate feed, not the old package source.

[DurableModelQualificationTests](../../tests/Spatial.Tests/Acceptance/DurableModelQualificationTests.cs) registers the production Kernel and Spatial generated models against the real StateModelRegistry. Using test-only Persistence, it saves three independent roots, closes the repository, creates a fresh registry, and reopens read-only: a nonempty polymorphic GraphSpatialState; OccurrenceEvent<GraphSpatialFact> with a polymorphic fact array; and OccurrenceEvent<KernelCursor> as a generic-value qualification sample. Assertions verify restored values/types, nullable/property-backed cursor fields, distinct hydrated instances and within-graph shared reference identity. No fake registry, hand-edited generated files, new production model or disk adapter is involved.

Compilation compatibility, schema identity and real model capture/hydration are separately demonstrated. This does not provide application E/S crash recovery, old experimental save compatibility, a runtime disk adapter, or MUD integration. The Kernel's in-memory E/S/pending tests remain the authority for its own contract. Linux and remote CI were not executed; the checked-in two-OS workflow selects these same pins, SDK and commands.
