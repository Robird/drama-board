# Shared character experiment evidence

`GymJournal` records host evidence in a flushed, hash-chained JSONL file. `Read` is read-only and reports incomplete trailing bytes; opening a writer may trim only an incomplete final line. Complete corruption fails closed. This journal is not a game event log and never authorizes recovery or execution.

`GymMessageProjection` preserves concrete Completion content for audit, including native replay fields. Those fields are host evidence, not automatically character-visible dialogue. Do not supply clients or credential configuration to this projection.

`GymTraceExporter` selects one actor/branch and follows `branch-fork` ancestry only through the recorded source cutoff. Request/response, feedback, operation, perception and run records retain their existing format-1 payload conventions. New operation `Result.Status` values use names; legacy numeric `Interrupted = 2` remains readable. Missing evidence is reported rather than reconstructed from a current world snapshot.

Games provide the adapter that captures a valid completed-State decision boundary, associates feedback with its actual request, records committed operation results, supplies actor-visible perception material, and performs exact-State forks. `Playground.Cli/Gym/GymSession.cs` and `GymCommands.cs` are current examples; the shared assembly does not reference that game or its save model.

Pure journal, native content projection and cutoff/export tests live in `tests/CharacterGym.Tests`; game-boundary/fork integration remains in `tests/Playground.Cli.Tests`.
