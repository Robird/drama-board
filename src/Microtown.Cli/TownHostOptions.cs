using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Microtown.Cli;

public sealed record TownHostOptions {
    public string? Store { get; init; }
    public string? Branch { get; init; }
    public int Hours { get; init; } = 48;
    public bool ExplicitHours { get; init; }
    public int MaxOperations { get; init; } = 200;
    public bool Llm { get; init; }
    public string? HumanActor { get; init; }
    public bool Gym { get; init; }
    public bool Feedback { get; init; }
    public CompletionConnectionOptions Connection { get; init; } = new();
    public int MaxCalls { get; init; } = 128;
    public int MaxFailures { get; init; } = 3;
    public int MaxContextCharacters { get; init; } = 200000;
    public string? ExperimentNote { get; init; }

    public void Validate() {
        if (Hours is < 1 or > 720 || MaxOperations <= 0 || MaxCalls <= 0 || MaxFailures <= 0 || MaxContextCharacters <= 0) {
            throw new ArgumentException("Durations and budgets must be positive; game duration is at most 720 hours.");
        }
        if (HumanActor is not null && (!Llm || HumanActor is not ("a" or "b" or "c"))) {
            throw new ArgumentException("--human requires --llm and one resident ID: a, b or c.");
        }
        if (Feedback && !Llm || ExperimentNote is not null && !Llm) { throw new ArgumentException("Feedback and experiment notes require --llm."); }
        if ((Gym || Feedback) && Store is null) { throw new ArgumentException("Gym requires --store."); }
        if (ExperimentNote?.Length > 8000) { throw new ArgumentException("Experiment note exceeds 8000 characters."); }
        if (Branch is not null && (Store is null || !Directory.Exists(Store))) { throw new ArgumentException("--branch requires an existing --store."); }
        if (Store is not null && Directory.Exists(Store) && ExplicitHours) { throw new ArgumentException("--hours cannot change a saved game's endpoint; omit it when reopening."); }
    }
}
