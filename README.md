# DramaBoard

DramaBoard is an experimental deterministic Simulation Kernel and Graph Spatial project. The active core provides time, occurrence arbitration, objective movement/navigation, and Player decision boundaries; it does not currently provide a playable scene, Human frontend, LLM runtime, or disk-resume program.

Read [PROJECT-STATE.md](PROJECT-STATE.md) for the active boundary and deferred decisions. [AGENTS.md](AGENTS.md) defines repository collaboration rules, and [docs/README.md](docs/README.md) classifies the current design, evidence, package source, and historical archive.

## Build and test the core

Requires .NET 10, PowerShell 7, and the fixed sibling `durable-graph` / `atelia` source revisions. Prepare the package feed, then build and test either equivalent solution entry point:

```powershell
pwsh -File scripts/Prepare-DurableGraph.ps1
dotnet build DramaBoard.slnx -t:Rebuild -m:1 -warnaserror -p:DurableGraphSchemaHistoryMode=Verify
dotnet test DramaBoard.slnx --no-build --no-restore -m:1
```

The fixed package source and its validation rules are documented in [DurableGraph package source](docs/worksets/durablegraph-package-source.md). Historical FirstBoard, Demo, LLM, and persistence-adapter material is recoverable through the [FirstBoard and LLM archive](docs/archive/firstboard-llm.md), but is not part of the active build.
